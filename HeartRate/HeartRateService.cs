﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace HeartRate
{
    enum ContactSensorStatus
    {
        NotSupported,
        NotSupported2,
        NoContact,
        Contact
    }

    internal interface IHeartRateService : IDisposable
    {
        bool IsDisposed { get; }

        event HeartRateService.HeartRateUpdateEventHandler HeartRateUpdated;
        void InitiateDefault();
        void Cleanup();
    }

    internal class HeartRateServiceWatchdog : IDisposable
    {
        private readonly TimeSpan _timeout;
        private readonly IHeartRateService _service;
        private readonly Stopwatch _lastUpdateTimer = Stopwatch.StartNew();
        private readonly object _sync = new object();
        private bool _isDisposed = false;

        public HeartRateServiceWatchdog(
            TimeSpan timeout,
            IHeartRateService service)
        {
            _timeout = timeout;
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _service.HeartRateUpdated += _service_HeartRateUpdated;

            var thread = new Thread(WatchdogThread)
            {
                Name = GetType().Name,
                IsBackground = true
            };

            thread.Start();
        }

        private void _service_HeartRateUpdated(HeartRateReading reading)
        {
            lock (_sync)
            {
                _lastUpdateTimer.Restart();
            }
        }

        private void WatchdogThread()
        {
            while (!_isDisposed && !_service.IsDisposed)
            {
                var needsRefresh = false;
                lock (_sync)
                {
                    if (_isDisposed)
                    {
                        return;
                    }

                    if (_lastUpdateTimer.Elapsed > _timeout)
                    {
                        needsRefresh = true;
                        _lastUpdateTimer.Restart();
                    }
                }

                if (needsRefresh)
                {
                    Debug.WriteLine("Restarting services...");
                    _service.InitiateDefault();
                }

                Thread.Sleep(10000);
            }

            Debug.WriteLine("Watchdog thread exiting.");
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _isDisposed = true;
            }
        }
    }

    [Flags]
    internal enum HeartRateFlags
    {
        None = 0,
        IsShort = 1,
        HasEnergyExpended = 1 << 3,
        HasRRInterval = 1 << 4,
    }

    internal struct HeartRateReading
    {
        public HeartRateFlags Flags { get; set; }
        public ContactSensorStatus Status { get; set; }
        public int BeatsPerMinute { get; set; }
        public int? EnergyExpended { get; set; }
        public int[] RRIntervals { get; set; }
    }

    internal class HeartRateService : IHeartRateService
    {
        // https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.heart_rate_measurement.xml
        private const int _heartRateMeasurementCharacteristicId = 0x2A37;
        private static readonly Guid _heartRateMeasurementCharacteristicUuid =
            GattDeviceService.ConvertShortIdToUuid(_heartRateMeasurementCharacteristicId);

        public bool IsDisposed => _isDisposed;

        private GattDeviceService _service;
        private byte[] _buffer = new byte[256];
        private readonly object _disposeSync = new object();
        private bool _isDisposed;

        public event HeartRateUpdateEventHandler HeartRateUpdated;
        public delegate void HeartRateUpdateEventHandler(HeartRateReading reading);

        public void InitiateDefault()
        {
            var heartrateSelector = GattDeviceService
                .GetDeviceSelectorFromUuid(GattServiceUuids.HeartRate);

            var devices = AsyncResult(DeviceInformation
                .FindAllAsync(heartrateSelector));

            var device = devices.FirstOrDefault();

            if (device == null)
            {
                throw new ArgumentNullException(
                    nameof(device),
                    "Unable to locate heart rate device.");
            }

            GattDeviceService service;

            lock (_disposeSync)
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException(GetType().Name);
                }

                Cleanup();

                service = AsyncResult(GattDeviceService.FromIdAsync(device.Id));

                _service = service;
            }

            if (service == null)
            {
                throw new ArgumentOutOfRangeException(
                    $"Unable to get service to {device.Name} ({device.Id}). Is the device inuse by another program? The Bluetooth adaptor may need to be turned off and on again.");
            }

            var heartrate = service
                .GetCharacteristics(_heartRateMeasurementCharacteristicUuid)
                .FirstOrDefault();

            if (heartrate == null)
            {
                throw new ArgumentOutOfRangeException(
                    $"Unable to locate heart rate measurement on device {device.Name} ({device.Id}).");
            }

            var status = AsyncResult(
                heartrate.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify));

            heartrate.ValueChanged += HeartRate_ValueChanged;

            Debug.WriteLine($"Started {status}");
        }

        public void HeartRate_ValueChanged(
            GattCharacteristic sender,
            GattValueChangedEventArgs args)
        {
            var buffer = args.CharacteristicValue;
            if (buffer.Length == 0) return;

            var byteBuffer = Interlocked.Exchange(ref _buffer, null)
                ?? new byte[256];

            try
            {
                using var reader = DataReader.FromBuffer(buffer);
                reader.ReadBytes(byteBuffer);

                var readingValue = ReadBuffer(byteBuffer, (int)buffer.Length);

                if (readingValue == null)
                {
                    Debug.WriteLine($"Buffer was too small. Got {buffer.Length}.");
                    return;
                }

                var reading = readingValue.Value;
                Debug.WriteLine($"Read {reading.Flags:X} {reading.Status} {reading.BeatsPerMinute}");

                HeartRateUpdated?.Invoke(reading);
            }
            finally
            {
                Volatile.Write(ref _buffer, byteBuffer);
            }
        }

        internal static HeartRateReading? ReadBuffer(byte[] buffer, int length)
        {
            var ms = new MemoryStream(buffer, 0, length);
            var flags = (HeartRateFlags)ms.ReadByte();
            var isshort = flags.HasFlag(HeartRateFlags.IsShort);
            var contactSensor = (ContactSensorStatus)(((int)flags >> 1) & 3);
            var hasEnergyExpended = flags.HasFlag(HeartRateFlags.HasEnergyExpended);
            var hasRRInterval = flags.HasFlag(HeartRateFlags.HasRRInterval);
            var minLength = isshort ? 3 : 2;

            ushort ReadUInt16()
            {
                return (ushort)(ms.ReadByte() | (ms.ReadByte() << 8));
            }

            if (buffer.Length < minLength)
            {
                return null;
            }

            var reading = new HeartRateReading
            {
                Flags = flags,
                Status = contactSensor
            };

            if (buffer.Length > 1)
            {
                reading.BeatsPerMinute = isshort
                    ? ReadUInt16()
                    : ms.ReadByte();
            }

            if (hasEnergyExpended)
            {
                reading.EnergyExpended = ReadUInt16();
            }

            if (hasRRInterval)
            {
                var rrvalueCount = (buffer.Length - ms.Position) / sizeof(ushort);
                var rrvalues = new int[rrvalueCount];
                for (var i = 0; i < rrvalueCount; ++i)
                {
                    rrvalues[i] = ReadUInt16();
                }

                reading.RRIntervals = rrvalues;
            }

            return reading;
        }

        public void Cleanup()
        {
            var service = Interlocked.Exchange(ref _service, null);

            if (service == null)
            {
                return;
            }

            try
            {
                service.Dispose();
            }
            catch { }
        }

        private static T AsyncResult<T>(IAsyncOperation<T> async)
        {
            while (true)
            {
                switch (async.Status)
                {
                    case AsyncStatus.Started:
                        Thread.Sleep(100);
                        continue;
                    case AsyncStatus.Completed:
                        return async.GetResults();
                    case AsyncStatus.Error:
                        throw async.ErrorCode;
                    case AsyncStatus.Canceled:
                        throw new TaskCanceledException();
                }
            }
        }

        public void Dispose()
        {
            lock (_disposeSync)
            {
                _isDisposed = true;

                Cleanup();
            }
        }
    }
}

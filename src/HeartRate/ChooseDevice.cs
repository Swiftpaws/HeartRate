using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace HeartRate
{
    public partial class ChooseDevice : Form
    {
        public DeviceInformation device;
        private DeviceInformationCollection devices;
        public ChooseDevice()
        {
            InitializeComponent();

            var heartrateSelector = GattDeviceService
                .GetDeviceSelectorFromUuid(GattServiceUuids.HeartRate);

            devices = DeviceInformation
                .FindAllAsync(heartrateSelector)
                .AsyncResult();

            comboBox1.DataSource = devices.Select(x => x.Name).ToList();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            device = devices.First(x => x.Name.Equals(comboBox1.SelectedItem));

            this.Close();
        }
    }
}

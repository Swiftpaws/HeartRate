using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace HeartRate;

public class DebugLog
{
    protected readonly string _name;

    public DebugLog(string name)
    {
        _name = name;
    }

    public void Write(string s)
    {
        WriteLog($"{_name}: {s}");
    }

    private static FileStream _fs = null;
    private static FileStream _fsV = null;

    public static void Initialize(string filename, string valueFileName)
    {
        _fs = File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.Read);
        _fsV = File.Open(valueFileName, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    internal static string FormatLine(string s)
    {
        return $"{DateTime.Now}: {s}\n";
    }

    public static void WriteLog(string s)
    {
        Debug.WriteLine(s);

        if (_fs != null)
        {
            var bytes = Encoding.UTF8.GetBytes(FormatLine(s));

            if (_fs.Length > 1024 * 1024)
            {
                _fs.SetLength(0);
            }

            _fs.Write(bytes, 0, bytes.Length);
            _fs.Flush();
        }
    }

    public static void WriteValue(string s)
    {
        Debug.WriteLine(s);

        if (_fsV != null)
        {
            var bytes = Encoding.UTF8.GetBytes(FormatLine(s));

            if (_fsV.Length > 1024 * 1024)
            {
                _fsV.SetLength(0);
            }

            _fsV.Write(bytes, 0, bytes.Length);
            _fsV.Flush();
        }
    }
}
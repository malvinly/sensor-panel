using System.IO.Ports;
using Microsoft.Win32;

namespace SensorPanel.Display;

public static class PortFinder
{
    // The panel is a WCH USB CDC device; its serial number is USB35INCHIPSV2.
    private const string UsbEnumKey = @"SYSTEM\CurrentControlSet\Enum\USB\VID_1A86&PID_5722";

    /// <summary>
    /// Returns the COM port the panel currently has, or null if it is not plugged in.
    /// The port can change after a device reset, so this is looked up rather than configured.
    /// </summary>
    public static string? Find()
    {
        var present = SerialPort.GetPortNames();
        using var usb = Registry.LocalMachine.OpenSubKey(UsbEnumKey);
        if (usb is null) return null;

        foreach (var instance in usb.GetSubKeyNames())
        {
            using var parameters = usb.OpenSubKey(instance + @"\Device Parameters");
            if (parameters?.GetValue("PortName") is string port &&
                present.Contains(port, StringComparer.OrdinalIgnoreCase))
                return port;
        }
        return null;
    }
}

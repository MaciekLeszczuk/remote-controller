using System.Management;

// Brightness of the built-in panel through WMI, the same mechanism as the Windows brightness slider.
// External monitors don't support it; then Get returns null and Set does nothing.
static class Brightness
{
    public static int? Get()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementObject monitor in searcher.Get())
                using (monitor) return Convert.ToInt32(monitor["CurrentBrightness"]);
        }
        catch (Exception) { }
        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            foreach (ManagementObject monitor in searcher.Get())
                using (monitor) monitor.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Clamp(percent, 0, 100) });
        }
        catch (Exception) { }
    }
}

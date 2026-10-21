using System.Management;

namespace ABExplode.Platform.Windows;

/// <summary>Memória física do Windows (via WMI), usada pelo stress test de RAM.</summary>
public static class SystemMemory
{
    public static long FreePhysicalBytes()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in s.Get())
                return Convert.ToInt64(mo["FreePhysicalMemory"]) * 1024; // vem em KB
        }
        catch { }
        return 1L << 30;
    }
}

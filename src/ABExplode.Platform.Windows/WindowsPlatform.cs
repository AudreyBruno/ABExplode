using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace ABExplode.Platform.Windows;

/// <summary>Plataforma Windows: liga as interfaces de ABExplode.Platform ao código que já existia (WMI, LHM, ETW, registro).</summary>
public sealed class WindowsPlatform : IPlatform
{
    public string Name => "Windows";
    public bool IsElevated => SensorMonitor.IsElevated;

    public ISystemInfo SystemInfo { get; } = new WindowsSystemInfo();
    public IGpuInfo Gpu { get; } = new WindowsGpuInfo();
    public IDiskHealth DiskHealth { get; } = new WindowsDiskHealth();
    public IHardwareErrors HardwareErrors { get; } = new WindowsHardwareErrors();
    public IGameLibrary Games { get; } = new WindowsGameLibrary();
    public ISecretStore Secrets { get; } = new DpapiSecretStore();

    public ISensorSource CreateSensors() => new SensorMonitor();
    public IFrameSource CreateFrameSource() => new FrameMonitor();
}

internal sealed class WindowsSystemInfo : ISystemInfo
{
    public bool IsSupported => true;
    public List<InfoNode> Collect() => SystemInfoCollector.Collect();
    public void RefreshDisks(InfoNode storage) => SystemInfoCollector.RefreshDisks(storage);
    public List<DriveCard> Drives() => DriveMap.Read();
    public List<DiskHealthCard> Battery(IEnumerable<SensorRow>? sensors = null) => BatteryInfo.Read(sensors);
    public long FreePhysicalBytes() => SystemMemory.FreePhysicalBytes();
    public (long Total, long Available) MemoryStatus() => Optimizer.MemoryStatus();

    public string CpuName()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject mo in s.Get()) return mo["Name"]?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }
}

internal sealed class WindowsGpuInfo : IGpuInfo
{
    public bool IsSupported => true;
    public List<GpuInfo> Collect() => GpuInfoCollector.Collect();

    public string DriverVersionOf(string adapter)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
                if (mo["Name"] is string n && adapter.Contains(n.Trim(), StringComparison.OrdinalIgnoreCase) || mo["Name"] is string n2 && n2.Contains(adapter, StringComparison.OrdinalIgnoreCase))
                    return mo["DriverVersion"]?.ToString() ?? "";
        }
        catch { }
        return "";
    }
}

internal sealed class WindowsDiskHealth : IDiskHealth
{
    public bool IsSupported => true;
    public List<DiskHealthCard> Read(IEnumerable<SensorRow>? sensors = null) => ABExplode.Platform.Windows.DiskHealth.Read(sensors);
}

internal sealed class WindowsHardwareErrors : IHardwareErrors
{
    public bool IsSupported => true;
    public HardwareErrors ErrorsSince(DateTime since) => OcCheck.ErrorsSince(since);
    public (string Maker, string Model) Motherboard() => OcCheck.Motherboard();
}

internal sealed class WindowsGameLibrary : IGameLibrary
{
    public bool IsSupported => true;
    public List<LibGame> Scan(IEnumerable<CustomGame> custom) => GameLibrary.Scan(custom);
}

/// <summary>DPAPI do usuário atual; o texto gravado é o base64 dos bytes cifrados (mesmo formato das versões anteriores).</summary>
internal sealed class DpapiSecretStore : ISecretStore
{
    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedText)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }
}

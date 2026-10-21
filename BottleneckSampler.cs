namespace ABExplode;

/// <summary>Leitura para o detector de gargalo (Windows: sensores do LibreHardwareMonitor + NVML). A análise fica em ABExplode.Core.BottleneckAnalyzer.</summary>
public static class BottleneckSampler
{
    /// <summary>Lê os sensores atuais (LibreHardwareMonitor + NVML) da GPU principal e da CPU.</summary>
    public static LoadSample Sample(SensorMonitor m, GpuInfo? gpu, FrameStats? frames)
    {
        var rows = m.Rows;
        float? gpuLoad = null, gpuTemp = null;
        double? vramPct = null;
        bool thermal = false;
        if (gpu != null)
        {
            gpuLoad = rows.FirstOrDefault(r => r.Hardware == gpu.Name && r.Type == SensorKind.Load && r.Name == "GPU Core")?.RawValue;
            var temps = rows.Where(r => r.Hardware == gpu.Name && r.Type == SensorKind.Temperature).ToList();
            gpuTemp = (temps.FirstOrDefault(r => r.Name == "GPU Core") ?? temps.FirstOrDefault())?.RawValue;

            if (gpu.NvmlHandle != IntPtr.Zero && Nvml.MemoryInfo(gpu.NvmlHandle) is Nvml.Memory mem && mem.Total > 0)
                vramPct = 100.0 * mem.Used / mem.Total;
            else
            {
                var used = rows.FirstOrDefault(r => r.Hardware == gpu.Name && r.Name == "GPU Memory Used")?.RawValue;
                var total = rows.FirstOrDefault(r => r.Hardware == gpu.Name && r.Name == "GPU Memory Total")?.RawValue;
                if (used != null && total > 0) vramPct = 100.0 * used.Value / total.Value;
            }
            if (gpu.NvmlHandle != IntPtr.Zero && Nvml.ThrottleReasons(gpu.NvmlHandle) is ulong r)
                thermal = (r & 0x60) != 0; // SW/HW thermal slowdown
        }

        var cores = rows.Where(r => r.HardwareType == HardwareKind.Cpu && r.Type == SensorKind.Load &&
                                    r.Name.StartsWith("CPU Core #") && !r.Name.Contains("Thread") && r.RawValue != null).ToList();
        var busiest = cores.OrderByDescending(r => r.RawValue).FirstOrDefault();
        float? ram = rows.FirstOrDefault(r => r.HardwareType == HardwareKind.Memory && r.Type == SensorKind.Load && r.Name == "Memory")?.RawValue;

        return new LoadSample(gpuLoad, m.CpuLoad, busiest?.RawValue, busiest?.Name.Replace("CPU Core #", "núcleo "),
                              vramPct, ram, gpuTemp, m.CpuTemperature, thermal, frames?.Fps);
    }
}

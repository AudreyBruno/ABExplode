namespace ABExplode.Platform;

/// <summary>Uma placa de vídeo com os dados no estilo da aba "Graphics Card" do GPU-Z.</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string Subtitle { get; set; } = "";
    public string Vendor { get; init; } = "";
    public List<InfoNode> Sections { get; } = new();
    public IntPtr NvmlHandle { get; set; }
    public ulong VramBytes { get; set; }

    /// <summary>Leituras dinâmicas que o LibreHardwareMonitor não traz (P-state, PerfCap...).</summary>
    public List<KeyValuePair<string, string>> LiveExtras()
    {
        var list = new List<KeyValuePair<string, string>>();
        var h = NvmlHandle;
        if (h == IntPtr.Zero) return list;

        if (Nvml.PState(h) is int p) list.Add(new("Estado de desempenho", $"P{p}"));
        if (Nvml.ThrottleReasons(h) is ulong r) list.Add(new("PerfCap (limitador)", PerfCap(r)));
        if (Nvml.PowerUsageMw(h) is uint pw && Nvml.PowerLimitMw(h) is uint lim && lim > 0)
            list.Add(new("Consumo / limite", $"{pw / 1000.0:0.0} W · {100.0 * pw / lim:0} %"));
        if (Nvml.Util(h) is Nvml.Utilization u) list.Add(new("Controlador de memória", $"{u.Memory} %"));
        if (Nvml.EncoderUtil(h) is uint enc) list.Add(new("Encoder (NVENC)", $"{enc} %"));
        if (Nvml.DecoderUtil(h) is uint dec) list.Add(new("Decoder (NVDEC)", $"{dec} %"));
        if (Nvml.CurPcieGen(h) is uint g && Nvml.CurPcieWidth(h) is uint w) list.Add(new("PCIe em uso", $"{PcieName(g)} x{w}"));
        return list;
    }

    private static string PerfCap(ulong r)
    {
        var parts = new List<string>();
        if ((r & 0x4) != 0) parts.Add("Energia");
        if ((r & 0x60) != 0) parts.Add("Térmico");
        if ((r & 0x8) != 0) parts.Add("HW slowdown");
        if ((r & 0x80) != 0) parts.Add("Power brake");
        if (parts.Count == 0) return (r & 0x1) != 0 ? "Ociosa" : "Nenhum";
        return string.Join(" + ", parts);
    }

    public static string PcieName(uint gen) => gen switch
    {
        1 => "PCIe 1.1", 2 => "PCIe 2.0", 3 => "PCIe 3.0", 4 => "PCIe 4.0", 5 => "PCIe 5.0", 6 => "PCIe 6.0",
        _ => $"PCIe gen {gen}"
    };
}

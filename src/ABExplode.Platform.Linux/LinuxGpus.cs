using System.Text.RegularExpressions;

namespace ABExplode.Platform.Linux;

/// <summary>Uma placa de vídeo em /sys/class/drm (com o nome que a tela de GPU e os sensores usam igual).</summary>
internal sealed record DrmCard(string Card, string Device, string Slot, string VendorId, string DeviceId, string SubVendor,
                               string SubDevice, string Revision, string Driver, string Name, IntPtr NvmlHandle, bool BootVga)
{
    public string Vendor => VendorId switch { "10DE" => "NVIDIA", "1002" => "AMD", "8086" => "Intel", _ => VendorId };

    /// <summary>Pasta hwmon do driver (amdgpu, i915/xe, nouveau): temperatura, consumo, ventoinha.</summary>
    public string? Hwmon => Sys.Dirs(Path.Combine(Device, "hwmon"), "hwmon*").FirstOrDefault();
}

/// <summary>Lista as placas de vídeo PCI uma vez (o nome vem da NVML, do lspci ou do banco de especificações).</summary>
internal static class LinuxGpus
{
    private static List<DrmCard>? _cards;
    private static readonly object Gate = new();

    public static List<DrmCard> All()
    {
        lock (Gate) return _cards ??= Scan();
    }

    private static string Hex(string? s) => (s ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant().PadLeft(4, '0');

    private static List<DrmCard> Scan()
    {
        var list = new List<DrmCard>();
        var seen = new HashSet<string>();
        var nv = Nvml.Devices();
        foreach (var card in Sys.Dirs("/sys/class/drm", "card*").Where(c => Regex.IsMatch(Path.GetFileName(c), @"^card\d+$")))
        {
            string dev = Path.Combine(card, "device");
            string? ven = Sys.Read(Path.Combine(dev, "vendor"));
            if (ven == null) continue; // simpledrm/efifb: sem PCI
            string slot = Path.GetFileName(Sys.RealPath(dev));
            if (!seen.Add(slot)) continue;

            string vendorId = Hex(ven), deviceId = Hex(Sys.Read(Path.Combine(dev, "device")));
            string sv = Hex(Sys.Read(Path.Combine(dev, "subsystem_vendor"))), sd = Hex(Sys.Read(Path.Combine(dev, "subsystem_device")));
            string rev = Hex(Sys.Read(Path.Combine(dev, "revision")));
            if (rev.Length > 2) rev = rev[^2..];
            string driver = Sys.LinkName(Path.Combine(dev, "driver")) ?? "";

            IntPtr h = IntPtr.Zero;
            if (vendorId == "10DE")
            {
                // BusId da NVML: "00000000:01:00.0"; o slot do /sys: "0000:01:00.0"
                string tail = slot.Length > 7 ? slot[^7..] : slot;
                h = nv.FirstOrDefault(d => d.Pci.BusId?.EndsWith(tail, StringComparison.OrdinalIgnoreCase) == true).Handle;
            }

            // Nome comercial: NVML; product_name do amdgpu (placas com FRU); vulkaninfo (o nome do driver Mesa, exato);
            // por último o lspci, que às vezes junta vários modelos no mesmo ID.
            string? product = Sys.Read(Path.Combine(dev, "product_name"));
            string name = (h != IntPtr.Zero ? Nvml.Name(h) : null)
                          ?? (string.IsNullOrWhiteSpace(product) ? null : product)
                          ?? VulkanName(vendorId, deviceId)
                          ?? LspciName(slot, vendorId) ?? $"GPU {vendorId}:{deviceId}";
            list.Add(new DrmCard(card, dev, slot, vendorId, deviceId, sv, sd, rev, driver, name, h,
                                 Sys.Read(Path.Combine(dev, "boot_vga")) == "1"));
        }
        return list;
    }

    private static Dictionary<string, string>? _vulkan;

    /// <summary>"AMD Radeon RX 7900 XTX (RADV NAVI31)" do vulkaninfo --summary, pela ID PCI; null se não houver vulkan-tools.</summary>
    private static string? VulkanName(string vendorId, string deviceId)
    {
        if (_vulkan == null)
        {
            _vulkan = new Dictionary<string, string>();
            string? outp = Sys.Run("vulkaninfo", "--summary", 8000);
            if (outp != null)
                foreach (var block in Regex.Split(outp, @"\n(?=GPU\d+:)"))
                {
                    var ven = Regex.Match(block, @"vendorID\s*=\s*0x([0-9a-fA-F]+)");
                    var devm = Regex.Match(block, @"deviceID\s*=\s*0x([0-9a-fA-F]+)");
                    var nm = Regex.Match(block, @"deviceName\s*=\s*(.+)");
                    if (!ven.Success || !devm.Success || !nm.Success) continue;
                    string clean = Regex.Replace(nm.Groups[1].Value.Trim(), @"\s*\([^)]*\)\s*$", ""); // tira "(RADV NAVI31)"
                    if (clean.StartsWith("llvmpipe")) continue; // renderizador por software
                    _vulkan.TryAdd(Hex(ven.Groups[1].Value) + ":" + Hex(devm.Groups[1].Value), clean);
                }
        }
        return _vulkan.GetValueOrDefault(vendorId + ":" + deviceId);
    }

    /// <summary>"Navi 31 [Radeon RX 7900 XTX]" → "AMD Radeon RX 7900 XTX" (o lspci traz o nome comercial entre colchetes).</summary>
    private static string? LspciName(string slot, string vendorId)
    {
        string? outp = Sys.Run("lspci", $"-mm -s {slot}");
        if (outp == null) return null;
        var fields = Regex.Matches(outp, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        if (fields.Count < 3) return null;
        string device = fields[2];
        var br = Regex.Match(device, @"\[([^\]]+)\]");
        string name = br.Success ? br.Groups[1].Value : device;
        // Vários modelos no mesmo ID: "Radeon RX 6800/6800 XT / 6900 XT" — fica com o primeiro.
        if (name.Contains('/')) name = name.Split('/')[0].Trim();
        string prefix = vendorId switch { "1002" => "AMD", "8086" => "Intel", "10DE" => "NVIDIA", _ => "" };
        return prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? $"{prefix} {name}" : name;
    }

    /// <summary>"16.0 GT/s PCIe" → geração 4.</summary>
    public static uint? PcieGen(string? speed)
    {
        if (speed == null) return null;
        var m = Regex.Match(speed, @"([\d.]+)\s*GT/s");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double gt)) return null;
        return gt switch { < 3 => 1, < 6 => 2, < 9 => 3, < 20 => 4, < 40 => 5, _ => 6 };
    }

    /// <summary>Maior clock de uma tabela pp_dpm_* do amdgpu ("2: 2500Mhz *").</summary>
    public static uint? MaxDpm(string path)
    {
        uint? max = null;
        foreach (var l in Sys.Lines(path))
        {
            var m = Regex.Match(l, @"(\d+)\s*Mhz", RegexOptions.IgnoreCase);
            if (m.Success && uint.TryParse(m.Groups[1].Value, out uint v)) max = Math.Max(max ?? 0, v);
        }
        return max;
    }

    /// <summary>Clock em uso numa tabela pp_dpm_* (a linha marcada com "*").</summary>
    public static uint? CurDpm(string path)
    {
        foreach (var l in Sys.Lines(path))
        {
            if (!l.Contains('*')) continue;
            var m = Regex.Match(l, @"(\d+)\s*Mhz", RegexOptions.IgnoreCase);
            if (m.Success && uint.TryParse(m.Groups[1].Value, out uint v)) return v;
        }
        return null;
    }
}

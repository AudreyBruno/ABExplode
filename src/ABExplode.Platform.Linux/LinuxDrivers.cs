using System.Text.Json;
using System.Text.RegularExpressions;

namespace ABExplode.Platform.Linux;

/// <summary>Driver de um dispositivo PCI em uso. Mesmas propriedades do modelo do Windows.</summary>
public sealed class LinuxDriverRow
{
    public string Device { get; init; } = "";
    public string Class { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Version { get; init; } = "";
    public string Date { get; init; } = "—";
    public string SignedText { get; init; } = "";
}

/// <summary>Situação do driver de vídeo. Mesmas propriedades do modelo do Windows.</summary>
public sealed class LinuxGpuDriver
{
    public string Vendor { get; set; } = "";
    public string GpuName { get; set; } = "";
    public string Installed { get; set; } = "";
    public string Latest { get; set; } = "";
    public string Message { get; set; } = "";
    public string OfficialPage { get; set; } = "";
    /// <summary>Comando (root) que instala o driver recomendado; null se não houver um jeito seguro nesta distribuição.</summary>
    public string? InstallScript { get; set; }
    public bool CanAutoInstall => InstallScript != null;
}

public sealed class LinuxBoard
{
    public string Maker { get; set; } = "";
    public string Model { get; set; } = "";
    public string Bios { get; set; } = "";
    public string Brand { get; set; } = "";
    public string SupportUrl { get; set; } = "";
}

/// <summary>Atualização de firmware do LVFS (fwupd): o equivalente dos drivers do Windows Update para BIOS, SSD, dock...</summary>
public sealed record FirmwareUpdate(string Device, string Version, long SizeBytes)
{
    public string Title => $"{Device} → {Version}";
}

/// <summary>
/// RESET DRIVER no Linux. Os drivers vêm com o kernel; o que muda de verdade é o driver NVIDIA proprietário
/// (ubuntu-drivers no Ubuntu e derivados, akmod-nvidia no Fedora com RPM Fusion), o Mesa (AMD/Intel, vem com as
/// atualizações do sistema) e o firmware (fwupd/LVFS).
/// </summary>
public static class LinuxDrivers
{
    // ------------------------------------------------------------------ dispositivos

    /// <summary>Dispositivos PCI com o módulo do kernel em uso (lspci -vmm -k).</summary>
    public static List<LinuxDriverRow> Inventory()
    {
        var rows = new List<LinuxDriverRow>();
        string kernel = Sys.Read("/proc/sys/kernel/osrelease") ?? "";
        string? outp = Sys.Run("lspci", "-vmm -k", 10000);
        if (outp == null) return rows;
        foreach (var block in outp.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = new Dictionary<string, string>();
            foreach (var line in block.Split('\n'))
            {
                int i = line.IndexOf(':');
                if (i > 0) kv.TryAdd(line[..i].Trim(), line[(i + 1)..].Trim());
            }
            if (!kv.TryGetValue("Driver", out var drv)) continue; // sem driver: não há o que mostrar
            string taint = Sys.Read($"/sys/module/{drv}/taint") ?? "";
            rows.Add(new LinuxDriverRow
            {
                Device = kv.GetValueOrDefault("Device") ?? kv.GetValueOrDefault("Slot") ?? drv,
                Class = kv.GetValueOrDefault("Class") ?? "",
                Vendor = kv.GetValueOrDefault("Vendor") ?? "",
                Version = Sys.Read($"/sys/module/{drv}/version") is string v ? $"{drv} {v}" : $"{drv} (kernel {kernel})",
                SignedText = taint.Contains('P') ? "proprietário" : taint.Contains('O') ? "fora do kernel" : "do kernel",
            });
        }
        return rows.OrderBy(r => r.Class).ThenBy(r => r.Device).ToList();
    }

    // ------------------------------------------------------------------ vídeo

    /// <summary>Versão do Mesa (driver de vídeo livre do AMD e da Intel), pelo vulkaninfo ou glxinfo.</summary>
    private static string? MesaVersion()
    {
        string? text = Sys.Run("vulkaninfo", "--summary", 8000) ?? Sys.Run("glxinfo", "-B", 8000);
        return text != null && Regex.Match(text, @"Mesa\s+(\d+\.\d+(\.\d+)?)") is { Success: true } m ? m.Groups[1].Value : null;
    }

    private static bool IsUbuntuFamily => Sys.HasCommand("ubuntu-drivers");
    private static bool HasRpmFusionNonfree => Sys.HasCommand("dnf") && (Sys.Run("dnf", "repolist --enabled", 20000) ?? "").Contains("rpmfusion-nonfree");

    public static List<LinuxGpuDriver> ReadGpus()
    {
        var list = new List<LinuxGpuDriver>();
        string? mesa = null;
        foreach (var c in LinuxGpus.All())
        {
            var g = new LinuxGpuDriver { Vendor = c.Vendor, GpuName = c.Name };
            if (c.VendorId == "10DE")
            {
                g.OfficialPage = "https://www.nvidia.com/Download/index.aspx";
                if (c.Driver == "nvidia")
                {
                    g.Installed = Nvml.DriverVersion() ?? Sys.Read("/sys/module/nvidia/version") ?? "proprietário";
                    g.Message = "Driver NVIDIA proprietário em uso.";
                }
                else
                {
                    g.Installed = c.Driver.Length > 0 ? $"{c.Driver} (livre)" : "nenhum";
                    g.Message = c.Driver == "nouveau"
                        ? "Usando o driver livre nouveau: sem sensores completos e com muito menos desempenho em jogos. Instale o driver NVIDIA proprietário."
                        : "Nenhum driver de vídeo carregado para esta placa. Instale o driver NVIDIA proprietário.";
                }
                // Recomendação da distribuição
                if (IsUbuntuFamily && Sys.Run("ubuntu-drivers", "devices", 30000) is string dev &&
                    Regex.Match(dev, @"driver\s*:\s*(nvidia-driver-(\d+)(-open)?)\s.*recommended") is { Success: true } rec)
                {
                    g.Latest = $"{rec.Groups[2].Value}{(rec.Groups[3].Success ? " (open)" : "")} · recomendado pela distribuição";
                    bool outdated = int.TryParse(g.Installed.Split('.')[0], out int major) && int.TryParse(rec.Groups[2].Value, out int recMajor) && major < recMajor;
                    if (c.Driver != "nvidia" || outdated)
                    {
                        g.InstallScript = $"apt-get update && apt-get install -y {rec.Groups[1].Value}";
                        if (outdated) g.Message = $"Há um driver mais novo na distribuição ({rec.Groups[2].Value}).";
                    }
                }
                else if (c.Driver != "nvidia" && HasRpmFusionNonfree)
                {
                    g.Latest = "akmod-nvidia (RPM Fusion)";
                    g.InstallScript = "dnf install -y akmod-nvidia xorg-x11-drv-nvidia-cuda";
                }
                else if (c.Driver != "nvidia")
                    g.Message += Sys.HasCommand("dnf") ? " No Fedora, ative o repositório RPM Fusion nonfree e instale o akmod-nvidia."
                               : Sys.HasCommand("pacman") ? " No Arch: pacman -S nvidia-open nvidia-utils (RTX 20 ou mais nova) e reinicie."
                               : " Use o gerenciador de drivers da sua distribuição.";
            }
            else
            {
                mesa ??= MesaVersion();
                g.OfficialPage = c.VendorId == "1002" ? "https://www.amd.com/en/support/download/linux-drivers.html" : "https://dgpu-docs.intel.com/";
                g.Installed = mesa != null ? $"Mesa {mesa} ({c.Driver})" : c.Driver.Length > 0 ? c.Driver : "nenhum";
                g.Latest = "vem com as atualizações do sistema";
                g.Message = c.Driver.Length == 0
                    ? "Nenhum driver de vídeo carregado para esta placa: o kernel pode ser antigo demais para ela. Atualize o sistema."
                    : $"Driver livre do kernel ({c.Driver}) com o Mesa: já é o recomendado para jogos. Mantenha o sistema atualizado para ter o Mesa mais novo.";
            }
            list.Add(g);
        }
        return list;
    }

    /// <summary>Instala o driver recomendado (pede a senha). É preciso reiniciar depois.</summary>
    public static async Task<string> InstallGpuDriverAsync(LinuxGpuDriver g)
    {
        if (g.InstallScript == null) return "Não há instalação automática nesta distribuição.";
        var (ok, output) = await Sys.RunAsRootAsync(g.InstallScript, "DEBIAN_FRONTEND=noninteractive");
        return ok ? "Driver instalado. Reinicie o computador para usar." : "A instalação falhou: " + output;
    }

    // ------------------------------------------------------------------ placa-mãe

    public static LinuxBoard ReadBoard()
    {
        var b = new LinuxBoard
        {
            Maker = Sys.Dmi("board_vendor") ?? Sys.Dmi("sys_vendor") ?? "",
            Model = Sys.Dmi("board_name") ?? Sys.Dmi("product_name") ?? "",
            Bios = $"{Sys.Dmi("bios_version")} ({Sys.Dmi("bios_date")})".Trim(' ', '(', ')'),
        };
        (b.Brand, b.SupportUrl) = BoardSupport.Find(b.Maker, b.Model, Sys.Dmi("sys_vendor") ?? "", Sys.Dmi("product_name") ?? "");
        return b;
    }

    // ------------------------------------------------------------------ firmware (fwupd)

    public static bool FwupdAvailable => Sys.HasCommand("fwupdmgr");

    public static readonly LinuxDependency FwupdDep = new("fwupd", "fwupd", "atualizações de firmware (BIOS, SSD, dock) pelo LVFS",
        new Dictionary<string, string> { ["apt"] = "fwupd", ["dnf"] = "fwupd", ["pacman"] = "fwupd", ["zypper"] = "fwupd" });

    /// <summary>Atualizações de firmware disponíveis. O fwupdmgr sai com código 2 quando não há nenhuma.</summary>
    public static async Task<List<FirmwareUpdate>> SearchFirmwareAsync()
    {
        await Task.Run(() => Sys.Run("fwupdmgr", "refresh --force", 120000, acceptNonZero: true)); // baixa a lista do LVFS
        string? json = await Task.Run(() => Sys.Run("fwupdmgr", "get-updates --json", 120000, acceptNonZero: true));
        var list = new List<FirmwareUpdate>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Devices", out var devices)) return list;
            foreach (var d in devices.EnumerateArray())
            {
                string name = d.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                if (!d.TryGetProperty("Releases", out var rels) || rels.GetArrayLength() == 0) continue;
                var r = rels[0];
                string ver = r.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
                long size = r.TryGetProperty("Size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
                list.Add(new FirmwareUpdate(name, ver, size));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Instala todas as atualizações de firmware (pede a senha). Algumas só terminam ao reiniciar.</summary>
    public static async Task<string> InstallFirmwareAsync()
    {
        var (ok, output) = await Sys.RunAsRootAsync("fwupdmgr update -y --no-reboot-check");
        return ok ? "Firmware atualizado. Se alguma atualização pedir, reinicie o computador para terminar."
                  : "A atualização falhou: " + output;
    }
}

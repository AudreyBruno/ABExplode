using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ABExplode.Platform.Linux;

/// <summary>Tela Sistema, unidades e memória: /etc/os-release, /sys/class/dmi, /proc/cpuinfo, /proc/meminfo, lsblk e udev.</summary>
internal sealed class LinuxSystemInfo : ISystemInfo
{
    public bool IsSupported => true;

    public List<InfoNode> Collect()
    {
        var roots = new List<InfoNode>();
        Safe(roots, "Sistema operacional", CollectOs);
        Safe(roots, "Placa-mãe e BIOS", CollectBoard);
        Safe(roots, "Processador", CollectCpu);
        Safe(roots, "Memória RAM", CollectRam);
        Safe(roots, "Placa de vídeo", CollectGpu);
        Safe(roots, "Armazenamento", CollectDisks);
        return roots;
    }

    private static void Safe(List<InfoNode> roots, string title, Action<InfoNode> fill)
    {
        var node = new InfoNode { Header = title };
        try { fill(node); }
        catch (Exception ex) { node.Add("Erro ao ler", ex.Message); }
        roots.Add(node);
    }

    // ------------------------------------------------------------------ SO e placa-mãe

    private static void CollectOs(InfoNode n)
    {
        var os = Sys.EnvFile("/etc/os-release");
        n.Add("Nome", os.GetValueOrDefault("PRETTY_NAME") ?? os.GetValueOrDefault("NAME") ?? "Linux");
        if (os.GetValueOrDefault("VERSION_ID") is string ver) n.Add("Versão", ver);
        n.Add("Kernel", Sys.Or(Sys.Read("/proc/sys/kernel/osrelease")));
        n.Add("Arquitetura", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        string session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
        if (desktop.Length > 0 || session.Length > 0)
            n.Add("Ambiente gráfico", string.Join(" · ", new[] { desktop.Replace(':', '/'), session switch { "wayland" => "Wayland", "x11" => "X11", _ => session } }
                                                       .Where(s => s.Length > 0)));
        n.Add("Nome do computador", Environment.MachineName);
        n.Add("ABExplode rodando como", Sys.InFlatpak ? "Flatpak (comandos do sistema pelo flatpak-spawn)"
                                      : Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } ai ? $"AppImage ({Path.GetFileName(ai)})"
                                      : "executável avulso");
    }

    private static string DmiDate(string? s) =>
        s != null && DateTime.TryParseExact(s, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy") : Sys.Or(s);

    private static void CollectBoard(InfoNode n)
    {
        var board = n.Add("Placa-mãe");
        board.Add("Fabricante", Sys.Or(Sys.Dmi("board_vendor")));
        board.Add("Modelo", Sys.Or(Sys.Dmi("board_name")));
        board.Add("Versão", Sys.Or(Sys.Dmi("board_version")));
        board.Add("Nº de série", Sys.Dmi("board_serial") ?? (Sys.IsRoot ? "—" : "— (só como root)"));
        if (Sys.Dmi("product_name") is string product && product != Sys.Dmi("board_name"))
            board.Add("Produto", $"{Sys.Dmi("sys_vendor")} {product}".Trim());
        var bios = n.Add("BIOS");
        bios.Add("Fabricante", Sys.Or(Sys.Dmi("bios_vendor")));
        bios.Add("Versão", Sys.Or(Sys.Dmi("bios_version")));
        bios.Add("Data", DmiDate(Sys.Dmi("bios_date")));
        bios.Add("Modo de boot", Directory.Exists("/sys/firmware/efi") ? "UEFI" : "Legado (BIOS/CSM)");
        // Variável EFI: 4 bytes de atributos e 1 byte com o estado.
        const string secureBoot = "/sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c";
        try
        {
            if (File.Exists(secureBoot) && File.ReadAllBytes(secureBoot) is { Length: >= 5 } b)
                bios.Add("Secure Boot", b[4] == 1 ? "Ligado" : "Desligado");
        }
        catch { }
    }

    // ------------------------------------------------------------------ CPU

    /// <summary>Nome comercial da CPU ("model name"; em ARM, "Model"/"Hardware").</summary>
    public static string ReadCpuName()
    {
        foreach (var line in Sys.Lines("/proc/cpuinfo"))
        {
            int i = line.IndexOf(':');
            if (i <= 0) continue;
            string k = line[..i].Trim();
            if (k is "model name" or "Model" or "Hardware") return Regex.Replace(line[(i + 1)..].Trim(), @"\s+", " ");
        }
        return "CPU";
    }

    private static void CollectCpu(InfoNode n)
    {
        var lines = Sys.Lines("/proc/cpuinfo");
        int threads = 0;
        var cores = new HashSet<string>();
        var sockets = new HashSet<string>();
        string phys = "0", vendor = "", flags = "";
        foreach (var line in lines)
        {
            int i = line.IndexOf(':');
            if (i <= 0) continue;
            string k = line[..i].Trim(), v = line[(i + 1)..].Trim();
            switch (k)
            {
                case "processor": threads++; break;
                case "physical id": phys = v; sockets.Add(v); break;
                case "core id": cores.Add(phys + "/" + v); break;
                case "vendor_id": vendor = v; break;
                case "flags" when flags.Length == 0: flags = v; break;
            }
        }
        var cpu = n.Add(ReadCpuName());
        cpu.Add("Fabricante", vendor switch { "GenuineIntel" => "Intel", "AuthenticAMD" => "AMD", "" => "—", _ => vendor });
        if (sockets.Count > 1) cpu.Add("Soquetes", sockets.Count.ToString());
        cpu.Add("Núcleos físicos", cores.Count > 0 ? cores.Count.ToString() : "—");
        cpu.Add("Threads (lógicos)", threads > 0 ? threads.ToString() : Environment.ProcessorCount.ToString());
        if (Sys.ReadLong("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq") is long maxKhz)
            cpu.Add("Clock máximo", $"{maxKhz / 1000} MHz");
        if (Sys.ReadLong("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency") is long baseKhz)
            cpu.Add("Clock base", $"{baseKhz / 1000} MHz");

        foreach (var idx in Sys.Dirs("/sys/devices/system/cpu/cpu0/cache", "index*"))
        {
            string? level = Sys.Read(Path.Combine(idx, "level")), type = Sys.Read(Path.Combine(idx, "type")), size = Sys.Read(Path.Combine(idx, "size"));
            if (level == null || size == null || type == "Instruction") continue;
            string label = level == "1" ? "Cache L1 (dados)" : $"Cache L{level}";
            // L1 e L2 são de cada núcleo; o L3 é compartilhado (o tamanho lido é o de um bloco de cache).
            cpu.Add(label, size.Replace("K", " KB").Replace("M", " MB") + (level is "1" or "2" ? " por núcleo" : ""));
        }
        cpu.Add("Governador (cpufreq)", Sys.Or(Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor")));
        if (Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference") is string epp) cpu.Add("Preferência de energia (EPP)", epp);
        string virt = flags.Contains(" vmx") ? "VT-x" : flags.Contains(" svm") ? "AMD-V" : "";
        cpu.Add("Virtualização", virt.Length == 0 ? "Não suportada ou desligada na BIOS"
                               : File.Exists("/dev/kvm") ? $"Habilitada ({virt}, KVM disponível)" : $"Suportada ({virt}); KVM não carregado");
    }

    // ------------------------------------------------------------------ RAM

    private sealed record Module(string Bank, string Loc, ulong Cap, string Type, string Form, int Rated, int Conf, string Maker,
                                 string Part, string Serial, int DataW, int TotalW, string Rank, string VoltCfg, string VoltRange);

    private static readonly Regex SpeedInPart = new(
        @"(?<!\d)(2133|2400|2666|2933|3000|3200|3466|3600|3733|3800|4000|4133|4266|4400|4800|5200|5600|5800|6000|6200|6400|6600|6800|7000|7200|7400|7600|7800|8000|8200|8400|8800)(?!\d)",
        RegexOptions.Compiled);

    private static readonly Regex ChannelInLocator = new(
        @"(?i)(?:channel|chan|ch)[\s_-]*([A-H0-9])|dimm[\s_-]*([A-H])\d|(?:^|[^A-Za-z])([A-H])[12](?:$|[^0-9])", RegexOptions.Compiled);

    private static string? ChannelOf(string locator)
    {
        var m = ChannelInLocator.Match(locator);
        if (!m.Success) return null;
        for (int g = 1; g <= 3; g++) if (m.Groups[g].Success) return m.Groups[g].Value.ToUpperInvariant();
        return null;
    }

    private static string Volt(string? mv) => int.TryParse(mv, out int v) && v > 0 ? $"{v / 1000.0:0.00#} V" : "—";

    private static int Int(string? s) => int.TryParse(Regex.Match(s ?? "", @"\d+").Value, out int v) ? v : 0;

    /// <summary>
    /// Módulos pelo udev (systemd 248+ guarda o DMI em propriedades legíveis sem root: MEMORY_DEVICE_N_*);
    /// sem udev, pelo dmidecode (só como root).
    /// </summary>
    private static (List<Module> Mods, int Slots, ulong MaxBytes, string? Source) ReadModules()
    {
        var mods = new List<Module>();
        string? props = Sys.Run("udevadm", "info -q property /sys/devices/virtual/dmi/id");
        if (props != null && props.Contains("MEMORY_DEVICE_"))
        {
            var d = new Dictionary<string, string>();
            foreach (var l in props.Split('\n'))
            {
                int i = l.IndexOf('=');
                if (i > 0) d[l[..i]] = l[(i + 1)..].Trim();
            }
            string? G(string k) => d.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
            int devices = Int(G("MEMORY_ARRAY_NUM_DEVICES"));
            ulong max = ulong.TryParse(G("MEMORY_ARRAY_MAX_CAPACITY"), out ulong mx) ? mx : 0;
            for (int i = 0; i < Math.Max(devices, 16); i++)
            {
                string p = $"MEMORY_DEVICE_{i}_";
                if (G(p + "PRESENT") == "0" || !ulong.TryParse(G(p + "SIZE"), out ulong size) || size == 0) continue;
                string rank = Int(G(p + "RANK")) switch { 1 => "1 (single)", 2 => "2 (dual)", 4 => "4 (quad)", 0 => "—", var r => r.ToString() };
                string range = G(p + "MINIMUM_VOLTAGE") is string mn && G(p + "MAXIMUM_VOLTAGE") is string mxv ? $"{Volt(mn)} – {Volt(mxv)}" : "—";
                mods.Add(new Module(G(p + "BANK_LOCATOR") ?? "—", G(p + "LOCATOR") ?? $"Slot {i}", size, G(p + "TYPE") ?? "—",
                                    G(p + "FORM_FACTOR") ?? "—", Int(G(p + "SPEED_MTS")), Int(G(p + "CONFIGURED_SPEED_MTS")),
                                    G(p + "MANUFACTURER") ?? "—", G(p + "PART_NUMBER") ?? "—", G(p + "SERIAL_NUMBER") ?? "—",
                                    Int(G(p + "DATA_WIDTH")), Int(G(p + "TOTAL_WIDTH")), rank, Volt(G(p + "CONFIGURED_VOLTAGE")), range));
            }
            if (mods.Count > 0) return (mods, devices, max, "udev");
        }

        if (!Sys.IsRoot) return (mods, 0, 0, null);
        string? dmi = Sys.Run("dmidecode", "-t 16,17");
        if (dmi == null) return (mods, 0, 0, null);
        int slots = 0; ulong maxBytes = 0;
        foreach (var block in Regex.Split(dmi, @"\n(?=Handle )"))
        {
            var kv = new Dictionary<string, string>();
            foreach (var l in block.Split('\n'))
            {
                int i = l.IndexOf(':');
                if (i > 0) kv[l[..i].Trim()] = l[(i + 1)..].Trim();
            }
            string? K(string k) => kv.TryGetValue(k, out var v) && v.Length > 0 && v != "Unknown" && v != "Not Specified" ? v : null;
            if (block.Contains("Physical Memory Array"))
            {
                slots += Int(K("Number Of Devices"));
                if (K("Maximum Capacity") is string mc) maxBytes += ParseSize(mc);
                continue;
            }
            if (!block.Contains("Memory Device") || K("Size") is not string sz || sz.StartsWith("No Module")) continue;
            string mv = K("Configured Voltage") is string cv ? ((int)(double.Parse(cv.Split(' ')[0], CultureInfo.InvariantCulture) * 1000)).ToString() : "";
            string rangeD = K("Minimum Voltage") is string a && K("Maximum Voltage") is string b ? $"{a.Replace(" V", "")} – {b}" : "—";
            string rk = Int(K("Rank")) switch { 1 => "1 (single)", 2 => "2 (dual)", 4 => "4 (quad)", 0 => "—", var r => r.ToString() };
            mods.Add(new Module(K("Bank Locator") ?? "—", K("Locator") ?? "—", ParseSize(sz), K("Type") ?? "—", K("Form Factor") ?? "—",
                                Int(K("Speed")), Int(K("Configured Memory Speed")), K("Manufacturer") ?? "—", K("Part Number") ?? "—",
                                K("Serial Number") ?? "—", Int(K("Data Width")), Int(K("Total Width")), rk, Volt(mv), rangeD));
        }
        return (mods, slots, maxBytes, "dmidecode");
    }

    private static ulong ParseSize(string s)
    {
        var m = Regex.Match(s, @"([\d.]+)\s*(B|kB|KB|MB|GB|TB)", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return (ulong)(v * m.Groups[2].Value.ToUpperInvariant() switch { "KB" => 1024d, "MB" => 1048576d, "GB" => 1073741824d, "TB" => 1099511627776d, _ => 1d });
    }

    private static void CollectRam(InfoNode n)
    {
        var (mods, slots, maxBytes, source) = ReadModules();
        var (memTotal, memAvail) = Meminfo();
        ulong total = mods.Count > 0 ? (ulong)mods.Sum(m => (double)m.Cap) : (ulong)memTotal;
        n.Add("Total instalado", Sys.Bytes(total));

        var sum = n.Add("Resumo");
        sum.Add("Módulos instalados", mods.Count == 0 ? "—" : slots > 0 ? $"{mods.Count} de {slots} slots" : mods.Count.ToString());
        if (maxBytes > 0) sum.Add("Capacidade máxima suportada pela placa", Sys.Bytes(maxBytes));
        if (mods.Count > 0)
        {
            sum.Add("Tipo", string.Join(" / ", mods.Select(m => m.Type).Distinct()));
            int conf = mods.Where(m => m.Conf > 0).Select(m => m.Conf).DefaultIfEmpty(0).Min();
            int rated = mods.Select(m => m.Rated).DefaultIfEmpty(0).Max();
            sum.Add("Velocidade em uso", conf > 0 ? $"{conf} MT/s" : "—");
            sum.Add("Velocidade nominal reportada pelo módulo", rated > 0 ? $"{rated} MT/s" : "—");

            // Perfil EXPO/XMP: o sistema não diz se está ligado; estimamos pela velocidade escrita no part number.
            int label = 0;
            foreach (var m in mods)
                if (SpeedInPart.Match(m.Part) is { Success: true } mt) label = Math.Max(label, int.Parse(mt.Value));
            string profile;
            if (conf <= 0) profile = "Não foi possível ler a velocidade em uso.";
            else if (label > 0 && conf < label * 0.95)
                profile = $"Provavelmente DESLIGADO: a RAM roda a {conf} MT/s, mas o part number indica {label} MT/s. Ative EXPO/XMP na BIOS.";
            else if (label > 0) profile = $"Provavelmente ATIVO: roda a {conf} MT/s (part number indica {label} MT/s).";
            else if (rated > 0 && conf < rated) profile = $"Roda abaixo da velocidade nominal ({conf} de {rated} MT/s). Confira EXPO/XMP na BIOS.";
            else profile = "Sem como estimar (o part number não traz a velocidade). Confira na BIOS.";
            sum.Add("Perfil EXPO/XMP (estimado)", profile);

            var chans = mods.Select(m => ChannelOf(m.Loc) ?? ChannelOf(m.Bank)).ToList();
            string chText;
            if (mods.Count == 1) chText = "Canal único (1 módulo). Com 2 módulos iguais a RAM roda em dual channel e fica bem mais rápida.";
            else if (chans.All(c => c != null))
            {
                int distinct = chans.Distinct().Count();
                chText = distinct >= 2 ? $"{distinct} canais (dual channel ou mais)" : "Canal único: os módulos estão no mesmo canal. Mude para os slots recomendados no manual da placa.";
            }
            else chText = $"Não deu para identificar pelos nomes dos slots ({string.Join(", ", mods.Select(m => m.Loc))}). Veja o manual da placa.";
            sum.Add("Canais (estimado)", chText);
            sum.Add("ECC", mods.Any(m => m.TotalW > m.DataW && m.DataW > 0) ? "Sim (correção de erros)" : "Não");
            var volts = mods.Select(m => m.VoltCfg).Where(v => v != "—").Distinct().ToList();
            if (volts.Count > 0) sum.Add("Tensão em uso", string.Join(" / ", volts));
        }
        else
            sum.Add("Módulos", source == null && !Sys.IsRoot
                ? "Os detalhes dos módulos (slots, velocidade, fabricante) só aparecem com o udev do systemd 248+ ou abrindo o app como root (dmidecode)."
                : "Não foi possível ler os módulos.");
        sum.Add("Timings (CL, tRCD, tRP...)", "O sistema não expõe. Veja na BIOS.");
        if (memTotal > 0)
        {
            long used = memTotal - memAvail;
            sum.Add("Em uso (no momento da leitura)", $"{Sys.Bytes(used)} de {Sys.Bytes(memTotal)} ({(double)used / memTotal:P0})");
            var mi = Sys.KeyValues("/proc/meminfo");
            if (Kb(mi, "SwapTotal") is long st && st > 0)
                sum.Add("Swap em uso", $"{Sys.Bytes((st - (Kb(mi, "SwapFree") ?? 0)) * 1024)} de {Sys.Bytes(st * 1024)}");
        }

        foreach (var m in mods)
        {
            var slot = n.Add($"Módulo — {m.Bank} / {m.Loc}");
            slot.Add("Capacidade", Sys.Bytes(m.Cap));
            slot.Add("Tipo", m.Type);
            slot.Add("Formato", m.Form);
            slot.Add("Ranks", m.Rank);
            slot.Add("Velocidade em uso", m.Conf > 0 ? $"{m.Conf} MT/s" : "—");
            slot.Add("Velocidade nominal", m.Rated > 0 ? $"{m.Rated} MT/s" : "—");
            slot.Add("Tensão em uso", m.VoltCfg);
            slot.Add("Faixa de tensão do módulo", m.VoltRange);
            slot.Add("Largura de dados / total", m.DataW > 0 ? $"{m.DataW} / {m.TotalW} bits" : "—");
            slot.Add("Fabricante", m.Maker);
            slot.Add("Part number", m.Part);
            slot.Add("Nº de série", m.Serial);
        }
    }

    private static long? Kb(Dictionary<string, string> d, string k) =>
        d.TryGetValue(k, out var v) && long.TryParse(v.Split(' ')[0], out long kb) ? kb : null;

    /// <summary>MemTotal e MemAvailable em bytes.</summary>
    private static (long Total, long Available) Meminfo()
    {
        var d = Sys.KeyValues("/proc/meminfo");
        return ((Kb(d, "MemTotal") ?? 0) * 1024, (Kb(d, "MemAvailable") ?? Kb(d, "MemFree") ?? 0) * 1024);
    }

    // ------------------------------------------------------------------ GPU

    private static void CollectGpu(InfoNode n)
    {
        var gpuInfo = new LinuxGpuInfo();
        foreach (var c in LinuxGpus.All())
        {
            var gpu = n.Add(c.Name);
            gpu.Add("Driver", c.Driver.Length > 0 ? c.Driver : "nenhum");
            gpu.Add("Versão do driver", Sys.Or(gpuInfo.DriverVersionOf(c.Name)));
            if (c.BootVga) gpu.Add("Placa principal (boot)", "Sim");
            // Saídas conectadas desta placa (card0-DP-1, card0-HDMI-A-1...) e a maior resolução que o monitor anuncia.
            foreach (var conn in Sys.Dirs("/sys/class/drm", Path.GetFileName(c.Card) + "-*"))
            {
                if (Sys.Read(Path.Combine(conn, "status")) != "connected") continue;
                string mode = Sys.Lines(Path.Combine(conn, "modes")).FirstOrDefault() ?? "";
                gpu.Add($"Saída {Path.GetFileName(conn)[(Path.GetFileName(c.Card).Length + 1)..]}", mode.Length > 0 ? $"monitor conectado · até {mode}" : "monitor conectado");
            }
        }
        if (n.Children.Count == 0) n.Add("Nenhuma placa de vídeo encontrada", "—");
    }

    // ------------------------------------------------------------------ discos

    public void RefreshDisks(InfoNode storage)
    {
        storage.Children.Clear();
        try { CollectDisks(storage); }
        catch (Exception ex) { storage.Add("Erro ao ler", ex.Message); }
    }

    internal sealed record BlockDisk(string Name, string Model, long Size, string Serial, string Tran, bool Rota, string Rev, bool Removable);

    /// <summary>Discos físicos (sem loop, zram, CD): lsblk em JSON; sem lsblk, /sys/block.</summary>
    internal static List<BlockDisk> Disks()
    {
        var list = new List<BlockDisk>();
        string? json = Sys.Run("lsblk", "-J -b -d -o NAME,MODEL,SIZE,SERIAL,TRAN,ROTA,REV,TYPE,RM");
        if (json != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var d in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
                {
                    string S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
                    bool B(string k) => d.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && v.GetString() == "1");
                    long size = d.TryGetProperty("size", out var sz) ? sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : long.TryParse(sz.GetString(), out long l) ? l : 0 : 0;
                    if (S("type") != "disk" || size == 0) continue;
                    string name = S("name");
                    if (name.StartsWith("zram") || name.StartsWith("loop")) continue;
                    list.Add(new BlockDisk(name, S("model"), size, S("serial"), S("tran"), B("rota"), S("rev"), B("rm")));
                }
                return list;
            }
            catch { list.Clear(); }
        }
        foreach (var b in Sys.Dirs("/sys/block"))
        {
            string name = Path.GetFileName(b);
            if (name.StartsWith("loop") || name.StartsWith("zram") || name.StartsWith("ram") || name.StartsWith("sr") || name.StartsWith("dm-")) continue;
            long size = (Sys.ReadLong(Path.Combine(b, "size")) ?? 0) * 512;
            if (size == 0) continue;
            string real = Sys.RealPath(b);
            string tran = name.StartsWith("nvme") ? "nvme" : real.Contains("/usb") ? "usb" : "";
            list.Add(new BlockDisk(name, Sys.Read(Path.Combine(b, "device", "model")) ?? name, size, Sys.Read(Path.Combine(b, "device", "serial")) ?? "",
                                   tran, Sys.Read(Path.Combine(b, "queue", "rotational")) == "1", Sys.Read(Path.Combine(b, "device", "rev")) ?? "",
                                   Sys.Read(Path.Combine(b, "removable")) == "1"));
        }
        return list;
    }

    internal static string Bus(BlockDisk d) => d.Tran.ToLowerInvariant() switch
    {
        "nvme" => "NVMe", "sata" => "SATA", "usb" => "USB", "sas" => "SAS", "ata" => "ATA", "mmc" => "SD/eMMC", "" => d.Name.StartsWith("nvme") ? "NVMe" : d.Name.StartsWith("mmcblk") ? "SD/eMMC" : "—",
        var t => t.ToUpperInvariant()
    };

    private static void CollectDisks(InfoNode n)
    {
        foreach (var d in Disks())
        {
            var disk = n.Add(d.Model.Length > 0 ? d.Model : "/dev/" + d.Name);
            disk.Add("Dispositivo", "/dev/" + d.Name);
            disk.Add("Capacidade", Sys.Bytes(d.Size));
            disk.Add("Interface", Bus(d));
            disk.Add("Tipo", d.Rota ? "HDD" : Bus(d) == "NVMe" ? "SSD (NVMe)" : "SSD / flash");
            disk.Add("Nº de série", Sys.Or(d.Serial));
            disk.Add("Firmware", Sys.Or(d.Rev));
        }
    }

    // ------------------------------------------------------------------ unidades montadas

    private static readonly HashSet<string> RealFs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "jfs", "reiserfs", "zfs", "bcachefs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "hfsplus",
    };
    private static readonly HashSet<string> NetFs = new(StringComparer.OrdinalIgnoreCase) { "nfs", "nfs4", "cifs", "smb3", "sshfs", "fuse.sshfs" };

    private static string Unescape(string s) => Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    public List<DriveCard> Drives()
    {
        var labels = new Dictionary<string, string>();
        foreach (var f in SafeFiles("/dev/disk/by-label"))
            labels[Sys.RealPath(f)] = Unescape(Path.GetFileName(f).Replace("\\x20", " "));

        var seen = new HashSet<string>();
        var list = new List<DriveCard>();
        var hostSizes = Sys.InFlatpak ? HostSizes() : null;
        foreach (var line in Sys.MountLines())
        {
            var f = line.Split(' ');
            if (f.Length < 3) continue;
            string dev = f[0], mount = Unescape(f[1]), fs = f[2];
            bool net = NetFs.Contains(fs);
            if (!net && (!RealFs.Contains(fs) || !dev.StartsWith("/dev/") || dev.StartsWith("/dev/loop"))) continue;
            if (mount.StartsWith("/boot") || mount == "/efi" || mount.StartsWith("/snap") || mount.StartsWith("/var/snap") ||
                mount.StartsWith("/var/lib/docker") || mount.StartsWith("/proc") || mount.StartsWith("/sys")) continue;
            string realDev = net ? dev : Sys.RealPath(dev);
            if (!seen.Add(realDev)) continue; // mesma partição montada em vários lugares (btrfs com subvolumes, bind): fica a primeira

            try
            {
                long total, free;
                if (hostSizes != null)
                {
                    if (!hostSizes.TryGetValue(mount, out var hs)) continue;
                    (total, free) = hs;
                }
                else
                {
                    var di = new DriveInfo(mount);
                    (total, free) = (di.TotalSize, di.AvailableFreeSpace);
                }
                var (model, usb, removable) = net ? ("", false, false) : DiskOf(realDev);
                string kind = net ? "Rede" : removable && !usb ? "Removível" : usb ? "Externo (USB)" : "Interno";
                list.Add(new DriveCard
                {
                    Letter = mount, Label = labels.GetValueOrDefault(realDev, ""), Kind = kind, FileSystem = fs == "fuseblk" ? "ntfs" : fs,
                    Disk = model, Total = total, Free = free, Removable = removable || usb,
                });
            }
            catch { /* desmontou no meio da leitura */ }
        }
        return list.OrderBy(c => c.Letter == "/" ? "" : c.Letter, StringComparer.Ordinal).ToList();
    }

    /// <summary>Flatpak: tamanho e espaço livre de cada montagem pelo df do sistema (o DriveInfo do sandbox vê outro "/").</summary>
    private static Dictionary<string, (long Total, long Free)> HostSizes()
    {
        var d = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        foreach (var line in (Sys.Run("df", "-B1 --output=size,avail,target") ?? "").Split('\n').Skip(1))
        {
            var f = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries); // o caminho pode ter espaços: fica por último
            if (f.Length == 3 && long.TryParse(f[0], out long size) && long.TryParse(f[1], out long avail)) d[f[2]] = (size, avail);
        }
        return d;
    }

    private static string[] SafeFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }

    /// <summary>/dev/nvme0n1p2 → modelo do disco físico; passa por LUKS/LVM (dm-*) até a partição de verdade.</summary>
    private static (string Model, bool Usb, bool Removable) DiskOf(string dev, int depth = 0)
    {
        string name = Path.GetFileName(dev);
        string cls = $"/sys/class/block/{name}";
        if (!Directory.Exists(cls) || depth > 4) return ("", false, false);
        if (name.StartsWith("dm-") || name.StartsWith("md"))
        {
            var slave = Sys.Dirs(Path.Combine(cls, "slaves")).FirstOrDefault();
            return slave != null ? DiskOf("/dev/" + Path.GetFileName(slave), depth + 1) : ("", false, false);
        }
        string real = Sys.RealPath(cls);
        string disk = File.Exists(Path.Combine(real, "partition")) ? Path.GetFileName(Path.GetDirectoryName(real)!) : name;
        string? model = Sys.Read($"/sys/block/{disk}/device/model");
        bool usb = real.Contains("/usb");
        bool removable = Sys.Read($"/sys/block/{disk}/removable") == "1";
        return (model ?? disk, usb, removable);
    }

    // ------------------------------------------------------------------ bateria e energia

    public List<DiskHealthCard> Battery(IEnumerable<SensorRow>? sensors = null) => LinuxBattery.Read(sensors);

    public string CpuName() => ReadCpuName();

    public long FreePhysicalBytes() => Meminfo().Available;

    public (long Total, long Available) MemoryStatus() => Meminfo();
}

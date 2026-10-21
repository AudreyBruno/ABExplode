using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ABExplode.Platform.Linux;

/// <summary>
/// Sensores em tempo real do Linux, com os mesmos nomes do LibreHardwareMonitor (a UI procura por eles):
/// CPU por /proc/stat, cpufreq, hwmon (k10temp, coretemp, zenpower) e RAPL; GPU pela NVML ou pelo amdgpu/i915;
/// memória por /proc/meminfo; placa-mãe, discos e Wi-Fi pelos outros chips do hwmon; rede por /proc/net/dev.
/// </summary>
internal sealed class LinuxSensors : ISensorSource
{
    private readonly Dictionary<string, SensorRow> _index = new();
    private readonly Dictionary<string, (float Min, float Max)> _range = new();
    private volatile bool _open;

    private string _cpuName = "CPU";
    private string _boardName = "Placa-mãe";
    private long[]? _cpuPrev;                          // total/ocioso por linha de /proc/stat
    private readonly Dictionary<string, (long Uj, long Ticks)> _energyPrev = new(); // RAPL e energy*_input
    private readonly Dictionary<string, (long Rx, long Tx, long Ticks)> _netPrev = new();
    private List<DrmCard> _gpus = new();
    private readonly HashSet<string> _gpuDevices = new();

    private static readonly HashSet<string> CpuChips = new(StringComparer.OrdinalIgnoreCase) { "k10temp", "coretemp", "zenpower", "cpu_thermal", "fam15h_power" };
    private static readonly HashSet<string> GpuChips = new(StringComparer.OrdinalIgnoreCase) { "amdgpu", "radeon", "nouveau", "i915", "xe" };

    public ObservableCollection<SensorRow> Rows { get; } = new();
    public bool IsOpen => _open;
    public bool IsSupported => true;
    public bool IsCpuDriverInstalled => true; // não há driver extra: os módulos de sensores vêm no kernel

    public Task OpenAsync() => Task.Run(() =>
    {
        _cpuName = LinuxSystemInfo.ReadCpuName();
        _boardName = Sys.Dmi("board_name") ?? "Placa-mãe";
        _gpus = LinuxGpus.All();
        foreach (var g in _gpus) _gpuDevices.Add(Sys.RealPath(g.Device));
        _cpuPrev = ReadCpuTicks(); // a primeira carga sai na próxima leitura (precisa de duas amostras)
        _open = true;
    });

    // ------------------------------------------------------------------ atualização

    public void Refresh()
    {
        if (!_open) return;
        try { Cpu(); } catch { }
        try { Memory(); } catch { }
        foreach (var g in _gpus) try { Gpu(g); } catch { }
        try { OtherChips(); } catch { }
        try { Network(); } catch { }
    }

    private void Set(string id, string hardware, HardwareKind hwKind, string name, SensorKind kind, float? value)
    {
        if (!_index.TryGetValue(id, out var row))
        {
            if (value == null) return; // sensor que nunca respondeu não vira linha
            row = new SensorRow
            {
                Id = id, Hardware = hardware, HardwareType = hwKind, Name = name, Type = kind,
                Category = SensorRow.CategoryName(kind),
            };
            _index[id] = row;
            Rows.Add(row);
        }
        if (value is float v && float.IsFinite(v))
        {
            var r = _range.TryGetValue(id, out var old) ? (Math.Min(old.Min, v), Math.Max(old.Max, v)) : (v, v);
            _range[id] = r;
            row.Update(v, r.Item1, r.Item2);
        }
        else if (_range.TryGetValue(id, out var o)) row.Update(null, o.Min, o.Max);
        else row.Update(null, null, null);
    }

    // ------------------------------------------------------------------ CPU

    private static long[]? ReadCpuTicks()
    {
        var list = new List<long>();
        foreach (var line in Sys.Lines("/proc/stat"))
        {
            if (!line.StartsWith("cpu")) break;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long total = 0, idle = 0;
            for (int i = 1; i < f.Length && i <= 8; i++) // user nice system idle iowait irq softirq steal
            {
                long v = long.Parse(f[i]);
                total += v;
                if (i is 4 or 5) idle += v;
            }
            list.Add(total);
            list.Add(idle);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private void Cpu()
    {
        var now = ReadCpuTicks();
        if (now != null && _cpuPrev != null && now.Length == _cpuPrev.Length)
        {
            for (int i = 0; i < now.Length / 2; i++)
            {
                long dt = now[2 * i] - _cpuPrev[2 * i], di = now[2 * i + 1] - _cpuPrev[2 * i + 1];
                float? load = dt > 0 ? Math.Clamp(100f * (dt - di) / dt, 0, 100) : null;
                if (i == 0) Set("cpu/load/total", _cpuName, HardwareKind.Cpu, "CPU Total", SensorKind.Load, load);
                else Set($"cpu/load/{i - 1}", _cpuName, HardwareKind.Cpu, $"CPU Core #{i}", SensorKind.Load, load);
            }
        }
        _cpuPrev = now;

        // Clock de cada núcleo lógico (cpufreq em kHz; sem cpufreq, o "cpu MHz" do /proc/cpuinfo)
        var cpus = Sys.Dirs("/sys/devices/system/cpu", "cpu*").Where(d => Regex.IsMatch(Path.GetFileName(d), @"^cpu\d+$"))
                      .OrderBy(d => int.Parse(Path.GetFileName(d)[3..])).ToList();
        bool any = false;
        foreach (var d in cpus)
        {
            int n = int.Parse(Path.GetFileName(d)[3..]);
            if (Sys.ReadLong(Path.Combine(d, "cpufreq", "scaling_cur_freq")) is long khz && khz > 0)
            {
                Set($"cpu/clock/{n}", _cpuName, HardwareKind.Cpu, $"Core #{n + 1}", SensorKind.Clock, khz / 1000f);
                any = true;
            }
        }
        if (!any)
        {
            int n = 0;
            foreach (var line in Sys.Lines("/proc/cpuinfo"))
                if (line.StartsWith("cpu MHz") && float.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out float mhz))
                    Set($"cpu/clock/{n}", _cpuName, HardwareKind.Cpu, $"Core #{++n}", SensorKind.Clock, mhz);
        }

        // Consumo do pacote (RAPL; Intel e, nos kernels novos, AMD). Desde o kernel 5.10 só root lê o contador.
        foreach (var zone in Sys.Dirs("/sys/class/powercap", "intel-rapl:*").Where(z => Path.GetFileName(z).Count(ch => ch == ':') == 1))
        {
            string? name = Sys.Read(Path.Combine(zone, "name"));
            if (name == null || !name.StartsWith("package")) continue;
            float? w = EnergyWatts("rapl/" + zone, Path.Combine(zone, "energy_uj"), Sys.ReadLong(Path.Combine(zone, "max_energy_range_uj")));
            string suffix = name == "package-0" ? "" : " " + name[8..];
            Set($"cpu/power/{name}", _cpuName, HardwareKind.Cpu, "CPU Package" + suffix, SensorKind.Power, w);
        }

        // Temperatura: os chips de CPU do hwmon (k10temp: Tctl/Tdie/Tccd; coretemp: Package id 0 e Core N)
        foreach (var chip in Hwmons().Where(c => CpuChips.Contains(c.Name)))
            foreach (var (file, label) in Inputs(chip.Dir, "temp"))
            {
                string name = label switch
                {
                    null => "CPU Package",
                    var l when l.StartsWith("Package id") => l == "Package id 0" ? "CPU Package" : "CPU " + l,
                    var l when l.StartsWith("Core ") && int.TryParse(l[5..], out int core) => $"CPU Core #{core + 1}",
                    var l => l,
                };
                Set($"cpu/temp/{chip.Name}/{Path.GetFileName(file)}", _cpuName, HardwareKind.Cpu, name, SensorKind.Temperature, Milli(file));
            }
    }

    /// <summary>Potência pela diferença do contador de energia (µJ) entre duas leituras.</summary>
    private float? EnergyWatts(string key, string file, long? range)
    {
        if (Sys.ReadLong(file) is not long uj) return null;
        long ticks = Stopwatch.GetTimestamp();
        float? w = null;
        if (_energyPrev.TryGetValue(key, out var prev))
        {
            long d = uj - prev.Uj;
            if (d < 0 && range > 0) d += range.Value; // o contador deu a volta
            double secs = (ticks - prev.Ticks) / (double)Stopwatch.Frequency;
            if (d >= 0 && secs > 0.05) w = (float)(d / 1e6 / secs);
        }
        _energyPrev[key] = (uj, ticks);
        return w;
    }

    public float? CpuTemperature
    {
        get
        {
            var temps = Rows.Where(r => r.HardwareType == HardwareKind.Cpu && r.Type == SensorKind.Temperature && r.RawValue is > 0).ToList();
            var main = temps.FirstOrDefault(r => r.Name.Contains("Package") || r.Name.Contains("Tctl") || r.Name.Contains("Tdie"));
            return main?.RawValue ?? temps.Max(r => r.RawValue);
        }
    }

    public float? CpuLoad => Rows.FirstOrDefault(r => r.HardwareType == HardwareKind.Cpu && r.Type == SensorKind.Load && r.Name == "CPU Total")?.RawValue;

    public float? CpuPower => Rows.FirstOrDefault(r => r.HardwareType == HardwareKind.Cpu && r.Type == SensorKind.Power && r.Name.Contains("Package"))?.RawValue;

    public CpuSensorProblem DiagnoseCpuSensors() => CpuTemperature != null ? CpuSensorProblem.None : CpuSensorProblem.ReadFailed;

    public string Explain(CpuSensorProblem p) => p switch
    {
        CpuSensorProblem.ReadFailed =>
            "Nenhum sensor de temperatura da CPU apareceu em /sys/class/hwmon. O módulo do kernel que lê a CPU não está carregado: " +
            "Intel: sudo modprobe coretemp · AMD: sudo modprobe k10temp. Para carregar sempre, instale o lm-sensors e rode " +
            "sudo sensors-detect. Em máquina virtual a CPU não tem sensores. O consumo da CPU (RAPL) só aparece com o app como root.",
        CpuSensorProblem.NotElevated => "O consumo da CPU (RAPL) só pode ser lido como root desde o kernel 5.10.",
        _ => ""
    };

    // ------------------------------------------------------------------ memória

    private void Memory()
    {
        var m = Sys.KeyValues("/proc/meminfo");
        static long Kb(Dictionary<string, string> d, string k) =>
            d.TryGetValue(k, out var v) && long.TryParse(v.Split(' ')[0], out long kb) ? kb : 0;
        long total = Kb(m, "MemTotal"), avail = Kb(m, "MemAvailable");
        if (total <= 0) return;
        const string hw = "Memória";
        Set("mem/load", hw, HardwareKind.Memory, "Memory", SensorKind.Load, 100f * (total - avail) / total);
        Set("mem/used", hw, HardwareKind.Memory, "Memory Used", SensorKind.Data, (total - avail) / 1048576f);
        Set("mem/avail", hw, HardwareKind.Memory, "Memory Available", SensorKind.Data, avail / 1048576f);
        long swapTotal = Kb(m, "SwapTotal"), swapFree = Kb(m, "SwapFree");
        if (swapTotal > 0)
        {
            Set("mem/swap/load", hw, HardwareKind.Memory, "Virtual Memory", SensorKind.Load, 100f * (swapTotal - swapFree) / swapTotal);
            Set("mem/swap/used", hw, HardwareKind.Memory, "Virtual Memory Used", SensorKind.Data, (swapTotal - swapFree) / 1048576f);
        }
    }

    // ------------------------------------------------------------------ GPU

    private void Gpu(DrmCard g)
    {
        string id = "gpu/" + g.Slot + "/";
        var kind = g.VendorId switch { "10DE" => HardwareKind.GpuNvidia, "1002" => HardwareKind.GpuAmd, "8086" => HardwareKind.GpuIntel, _ => HardwareKind.Other };
        void S(string key, string name, SensorKind k, float? v) => Set(id + key, g.Name, kind, name, k, v);

        if (g.NvmlHandle != IntPtr.Zero)
        {
            var h = g.NvmlHandle;
            var u = Nvml.Util(h);
            S("load/core", "GPU Core", SensorKind.Load, u?.Gpu);
            S("load/mem", "GPU Memory Controller", SensorKind.Load, u?.Memory);
            S("temp/core", "GPU Core", SensorKind.Temperature, Nvml.Temperature(h));
            S("clock/core", "GPU Core", SensorKind.Clock, Nvml.Clock(h, Nvml.ClockGraphics));
            S("clock/mem", "GPU Memory", SensorKind.Clock, Nvml.Clock(h, Nvml.ClockMem));
            S("power", "GPU Package", SensorKind.Power, Nvml.PowerUsageMw(h) is uint mw ? mw / 1000f : null);
            S("fan", "GPU Fan", SensorKind.Control, Nvml.FanPercent(h));
            if (Nvml.MemoryInfo(h) is Nvml.Memory m)
            {
                S("mem/used", "GPU Memory Used", SensorKind.SmallData, m.Used / 1048576f);
                S("mem/free", "GPU Memory Free", SensorKind.SmallData, m.Free / 1048576f);
                S("mem/total", "GPU Memory Total", SensorKind.SmallData, m.Total / 1048576f);
            }
            return;
        }

        string d = g.Device;
        S("load/core", "GPU Core", SensorKind.Load, Sys.ReadLong(Path.Combine(d, "gpu_busy_percent")));
        S("load/mem", "GPU Memory Controller", SensorKind.Load, Sys.ReadLong(Path.Combine(d, "mem_busy_percent")));
        if (Sys.ReadLong(Path.Combine(d, "mem_info_vram_total")) is long vt && vt > 0)
        {
            long vu = Sys.ReadLong(Path.Combine(d, "mem_info_vram_used")) ?? 0;
            S("mem/used", "GPU Memory Used", SensorKind.SmallData, vu / 1048576f);
            S("mem/free", "GPU Memory Free", SensorKind.SmallData, (vt - vu) / 1048576f);
            S("mem/total", "GPU Memory Total", SensorKind.SmallData, vt / 1048576f);
        }
        // Intel: frequência atual da GT
        if (Sys.ReadLong(Path.Combine(g.Card, "gt_cur_freq_mhz")) is long gt) S("clock/core", "GPU Core", SensorKind.Clock, gt);

        if (g.Hwmon is not string hw) return;
        foreach (var (file, label) in Inputs(hw, "temp"))
        {
            string name = label?.ToLowerInvariant() switch
            {
                "edge" or null => "GPU Core",
                "junction" => "GPU Hot Spot",
                "mem" or "vram" => "GPU Memory",
                _ => "GPU " + label,
            };
            S("temp/" + Path.GetFileName(file), name, SensorKind.Temperature, Milli(file));
        }
        float? pw = Sys.ReadLong(Path.Combine(hw, "power1_average")) is long pa ? pa / 1e6f
                  : Sys.ReadLong(Path.Combine(hw, "power1_input")) is long pi ? pi / 1e6f
                  : EnergyWatts(id + "energy", Path.Combine(hw, "energy1_input"), null);
        S("power", "GPU Package", SensorKind.Power, pw);
        if (Sys.ReadLong(Path.Combine(hw, "freq1_input")) is long sclk) S("clock/core", "GPU Core", SensorKind.Clock, sclk / 1e6f);
        else if (LinuxGpus.CurDpm(Path.Combine(d, "pp_dpm_sclk")) is uint dpm) S("clock/core", "GPU Core", SensorKind.Clock, dpm);
        if (Sys.ReadLong(Path.Combine(hw, "freq2_input")) is long mclk) S("clock/mem", "GPU Memory", SensorKind.Clock, mclk / 1e6f);
        if (Sys.ReadLong(Path.Combine(hw, "in0_input")) is long mv) S("volt/core", "GPU Core", SensorKind.Voltage, mv / 1000f);
        if (Sys.ReadLong(Path.Combine(hw, "fan1_input")) is long rpm) S("fan/rpm", "GPU Fan", SensorKind.Fan, rpm);
        if (Sys.ReadLong(Path.Combine(hw, "pwm1")) is long pwm) S("fan/pct", "GPU Fan", SensorKind.Control, pwm * 100f / 255);
    }

    // ------------------------------------------------------------------ outros chips (placa-mãe, discos, Wi-Fi...)

    private sealed record Chip(string Dir, string Name);

    private static IEnumerable<Chip> Hwmons() =>
        Sys.Dirs("/sys/class/hwmon", "hwmon*").Select(d => new Chip(d, Sys.Read(Path.Combine(d, "name")) ?? Path.GetFileName(d)));

    /// <summary>Arquivos tipoN_input do chip, com o rótulo (tipoN_label) quando existir.</summary>
    private static IEnumerable<(string File, string? Label)> Inputs(string dir, string type)
    {
        string[] files;
        try { files = Directory.GetFiles(dir, type + "*_input"); } catch { yield break; }
        foreach (var f in files.OrderBy(f => int.TryParse(Regex.Match(Path.GetFileName(f), @"\d+").Value, out int n) ? n : 0))
        {
            string? label = Sys.Read(f[..^"_input".Length] + "_label");
            yield return (f, string.IsNullOrWhiteSpace(label) ? null : label);
        }
    }

    private static float? Milli(string file) => Sys.ReadLong(file) is long v ? v / 1000f : null;

    private void OtherChips()
    {
        foreach (var chip in Hwmons())
        {
            if (CpuChips.Contains(chip.Name)) continue;
            string dev = Sys.RealPath(Path.Combine(chip.Dir, "device"));
            if (GpuChips.Contains(chip.Name) && _gpuDevices.Any(g => dev.StartsWith(g, StringComparison.Ordinal))) continue;

            var (hardware, kind) = Describe(chip, dev);
            string id = "hw/" + Path.GetFileName(chip.Dir) + "/";
            bool storage = kind == HardwareKind.Storage;
            foreach (var (f, l) in Inputs(chip.Dir, "temp"))
            {
                int n = Num(f);
                string name = storage ? (l is null or "Composite" ? "Temperature" : l) : l ?? $"Temperature #{n}";
                Set(id + Path.GetFileName(f), hardware, kind, name, SensorKind.Temperature, Milli(f));
            }
            foreach (var (f, l) in Inputs(chip.Dir, "fan"))
            {
                long? rpm = Sys.ReadLong(f);
                if (rpm is null or 0 && !_index.ContainsKey(id + Path.GetFileName(f))) continue; // conector sem ventoinha
                Set(id + Path.GetFileName(f), hardware, kind, l ?? $"Fan #{Num(f)}", SensorKind.Fan, rpm);
            }
            foreach (var (f, l) in Inputs(chip.Dir, "in"))
                Set(id + Path.GetFileName(f), hardware, kind, l ?? $"Voltage #{Num(f)}", SensorKind.Voltage, Milli(f));
            foreach (var (f, l) in Inputs(chip.Dir, "curr"))
                Set(id + Path.GetFileName(f), hardware, kind, l ?? $"Current #{Num(f)}", SensorKind.Current, Milli(f));
            foreach (var (f, l) in Inputs(chip.Dir, "power"))
                Set(id + Path.GetFileName(f), hardware, kind, l ?? $"Power #{Num(f)}", SensorKind.Power, Sys.ReadLong(f) is long uw ? uw / 1e6f : null);
            if (kind == HardwareKind.SuperIO)
                foreach (var pwm in SafeFiles(chip.Dir, "pwm*").Where(p => Regex.IsMatch(Path.GetFileName(p), @"^pwm\d+$")))
                    Set(id + Path.GetFileName(pwm), hardware, kind, $"Fan Control #{Num(pwm)}", SensorKind.Control,
                        Sys.ReadLong(pwm) is long v ? v * 100f / 255 : null);
        }
    }

    private static int Num(string f) => int.TryParse(Regex.Match(Path.GetFileName(f), @"\d+").Value, out int n) ? n : 0;

    private static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); } catch { return Array.Empty<string>(); }
    }

    /// <summary>Nome e tipo do hardware do chip: o modelo do disco para nvme/drivetemp (a tela de discos casa por ele).</summary>
    private (string Hardware, HardwareKind Kind) Describe(Chip chip, string dev)
    {
        string n = chip.Name.ToLowerInvariant();
        if (n == "nvme" || n == "drivetemp")
        {
            string? model = Sys.Read(Path.Combine(dev, "model")) ?? Sys.Read(Path.Combine(dev, "device", "model"));
            return (model ?? chip.Name, HardwareKind.Storage);
        }
        if (Regex.IsMatch(n, @"^(nct|it8|f71|w83|asus|asuswmi|gigabyte|dell|thinkpad|hp|lenovo)")) return (_boardName, HardwareKind.SuperIO);
        if (n is "acpitz" or "pch_cannonlake" or "pch_cometlake" || n.StartsWith("pch_")) return (_boardName, HardwareKind.Motherboard);
        if (n.StartsWith("bat") || n == "ac") return ("Bateria", HardwareKind.Battery);
        if (n.StartsWith("iwlwifi") || n.StartsWith("mt7") || n.StartsWith("ath") || n.StartsWith("r8169") || n.StartsWith("rtw"))
            return (chip.Name, HardwareKind.Network);
        if (n.StartsWith("spd") || n.StartsWith("jc42")) return ("Memória", HardwareKind.Memory);
        return (chip.Name, HardwareKind.Other);
    }

    // ------------------------------------------------------------------ rede

    private void Network()
    {
        long ticks = Stopwatch.GetTimestamp();
        foreach (var line in Sys.Lines("/proc/net/dev").Skip(2))
        {
            int c = line.IndexOf(':');
            if (c < 0) continue;
            string iface = line[..c].Trim();
            if (iface == "lo" || iface.StartsWith("veth") || iface.StartsWith("docker") || iface.StartsWith("br-") || iface.StartsWith("virbr")) continue;
            if (Sys.Read($"/sys/class/net/{iface}/operstate") is string st && st != "up" && st != "unknown") continue;
            var f = line[(c + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9 || !long.TryParse(f[0], out long rx) || !long.TryParse(f[8], out long tx)) continue;
            string id = "net/" + iface + "/";
            if (_netPrev.TryGetValue(iface, out var p))
            {
                double secs = (ticks - p.Ticks) / (double)Stopwatch.Frequency;
                if (secs > 0.05)
                {
                    Set(id + "up", iface, HardwareKind.Network, "Upload Speed", SensorKind.Throughput, (float)Math.Max(0, (tx - p.Tx) / secs));
                    Set(id + "down", iface, HardwareKind.Network, "Download Speed", SensorKind.Throughput, (float)Math.Max(0, (rx - p.Rx) / secs));
                }
            }
            Set(id + "txgb", iface, HardwareKind.Network, "Data Uploaded", SensorKind.Data, tx / 1073741824f);
            Set(id + "rxgb", iface, HardwareKind.Network, "Data Downloaded", SensorKind.Data, rx / 1073741824f);
            _netPrev[iface] = (rx, tx, ticks);
        }
    }

    public void Dispose() { }
}

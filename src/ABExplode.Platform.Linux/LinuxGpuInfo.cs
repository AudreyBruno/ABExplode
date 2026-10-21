namespace ABExplode.Platform.Linux;

/// <summary>Placas de vídeo no estilo GPU-Z: /sys/class/drm, NVML (NVIDIA), amdgpu (pp_dpm_*, mem_info_*) e o banco de especificações.</summary>
internal sealed class LinuxGpuInfo : IGpuInfo
{
    public bool IsSupported => true;

    public List<GpuInfo> Collect()
    {
        var result = new List<GpuInfo>();
        foreach (var c in LinuxGpus.All())
        {
            try { result.Add(Build(c)); }
            catch (Exception ex)
            {
                var g = new GpuInfo { Name = c.Name, Vendor = c.Vendor };
                var n = new InfoNode { Header = "ERRO" };
                n.Add("Falha ao ler", ex.Message);
                g.Sections.Add(n);
                result.Add(g);
            }
        }
        // Placa dedicada primeiro (mais VRAM).
        return result.OrderByDescending(g => g.VramBytes).ToList();
    }

    public string DriverVersionOf(string adapterName)
    {
        var c = LinuxGpus.All().FirstOrDefault(x => x.Name.Contains(adapterName, StringComparison.OrdinalIgnoreCase) ||
                                                     adapterName.Contains(x.Name, StringComparison.OrdinalIgnoreCase));
        return c == null ? "" : DriverVersion(c);
    }

    private static string DriverVersion(DrmCard c)
    {
        if (c.NvmlHandle != IntPtr.Zero && Nvml.DriverVersion() is string nv) return $"NVIDIA {nv}";
        string kernel = Sys.Read("/proc/sys/kernel/osrelease") ?? "";
        // amdgpu, i915, xe e nouveau vêm no kernel; o que muda de versão para jogos é o Mesa (não dá para ler sem abrir um contexto gráfico).
        return Sys.Read($"/sys/module/{c.Driver}/version") is string mv ? $"{c.Driver} {mv}" : $"{c.Driver} (kernel {kernel})";
    }

    private static InfoNode Section(GpuInfo g, string title)
    {
        var n = new InfoNode { Header = title };
        g.Sections.Add(n);
        return n;
    }

    private static GpuInfo Build(DrmCard c)
    {
        string ven = c.VendorId, dev = c.DeviceId;
        var spec = GpuSpecDb.Find(ven, dev, c.Name);
        IntPtr h = c.NvmlHandle;
        string d = c.Device;
        var gpu = new GpuInfo { Name = c.Name, Vendor = c.Vendor, NvmlHandle = h };

        // ---------- Identificação ----------
        var id = Section(gpu, "IDENTIFICAÇÃO");
        id.Add("Nome", c.Name);
        id.Add("Chip", spec?.Chip ?? "—");
        id.Add("Arquitetura", spec?.Arch ?? "—");
        id.Add("Revisão", c.Revision.Length > 0 ? c.Revision : "—");
        id.Add("Fabricante da placa", c.SubVendor.Length > 0 && c.SubVendor != "0000" ? GpuSpecDb.SubvendorName(c.SubVendor) : "—");
        id.Add("Device ID", $"{ven} {dev} · {c.SubVendor} {c.SubDevice}");
        id.Add("Processo", spec?.Process ?? "—");
        id.Add("Área do die", spec != null ? $"{spec.DieMm2} mm²" : "—");
        id.Add("Transistores", spec != null ? $"{spec.TransistorsBi:0.#} bilhões" : "—");
        id.Add("Lançamento", spec?.Release ?? "—");

        // ---------- Unidades e desempenho teórico ----------
        uint? cores = h != IntPtr.Zero ? Nvml.Cores(h) : null;
        int shaders = (int)(cores ?? (uint)(spec?.Shaders ?? 0));
        var units = Section(gpu, "UNIDADES DE PROCESSAMENTO");
        if (shaders > 0)
        {
            bool nv = ven == "10DE";
            int perUnit = nv ? 128 : 64;
            units.Add(nv ? "CUDA cores" : "Stream processors", $"{shaders:N0}");
            units.Add(nv ? "SMs" : "Compute units", $"{shaders / perUnit}");
            if (nv && spec != null && spec.Arch is "Blackwell" or "Ada Lovelace" or "Ampere")
            {
                units.Add("Tensor cores", $"{shaders / perUnit * 4}");
                units.Add("RT cores", $"{shaders / perUnit}");
            }
            else if (!nv && spec != null && spec.Arch.StartsWith("RDNA"))
                units.Add("Ray accelerators", $"{shaders / perUnit}");
        }
        if (spec != null)
        {
            units.Add("ROPs / TMUs", $"{spec.Rops} / {spec.Tmus}");
            units.Add("Pixel fillrate", $"{spec.Rops * spec.BoostMHz / 1000.0:0.0} GPixel/s");
            units.Add("Texture fillrate", $"{spec.Tmus * spec.BoostMHz / 1000.0:0.0} GTexel/s");
            units.Add("FP32 (teórico)", $"{(shaders > 0 ? shaders : spec.Shaders) * 2.0 * spec.BoostMHz / 1e6:0.00} TFLOPS");
        }
        if (units.Children.Count == 0) units.Add("Sem dados", "GPU fora do banco de especificações");

        // ---------- Memória ----------
        var mem = Section(gpu, "MEMÓRIA");
        ulong vram = 0;
        if (h != IntPtr.Zero && Nvml.MemoryInfo(h) is Nvml.Memory nm) vram = nm.Total;
        if (vram == 0 && Sys.ReadLong(Path.Combine(d, "mem_info_vram_total")) is long amdVram) vram = (ulong)amdVram;
        gpu.VramBytes = vram;
        bool igpu = spec?.MemType.StartsWith("Memória do sistema") == true || (ven == "8086" && vram == 0);
        if (igpu || vram < 1UL << 30) mem.Add(igpu ? "Reservada (dedicada)" : "Tamanho", vram > 0 ? Sys.Bytes(vram, 1) : "—");
        else
        {
            double gib = vram / (double)(1UL << 30);
            mem.Add("Tamanho", $"{Math.Ceiling(gib - 0.15):0} GB ({vram >> 20:N0} MB utilizáveis)");
        }
        if (Sys.ReadLong(Path.Combine(d, "mem_info_gtt_total")) is long gtt && gtt > 0) mem.Add("Compartilhada do sistema (GTT)", Sys.Bytes(gtt, 1));
        mem.Add("Tipo", spec?.MemType ?? "—");

        uint? bus = h != IntPtr.Zero ? Nvml.BusWidth(h) : null;
        uint? memClock = h != IntPtr.Zero ? Nvml.MaxClock(h, Nvml.ClockMem) : LinuxGpus.MaxDpm(Path.Combine(d, "pp_dpm_mclk"));
        if (bus is uint b) mem.Add("Barramento", $"{b} bits");
        if (memClock is uint mc)
        {
            if (h != IntPtr.Zero)
            {
                double gbps = mc * 2 / 1000.0;
                mem.Add("Clock da memória", $"{mc} MHz ({gbps:0.0} Gbps efetivos)");
                if (bus is uint bw) mem.Add("Largura de banda", $"{gbps * bw / 8:0.0} GB/s");
            }
            else mem.Add("Clock da memória (máx.)", $"{mc} MHz");
        }
        if (h != IntPtr.Zero && Nvml.Bar1(h) is Nvml.Memory bar)
            mem.Add("Resizable BAR", bar.Total > 256UL * 1024 * 1024 ? $"Ativado (BAR1 {Sys.Bytes(bar.Total, 1)})" : "Desativado");

        // ---------- Clocks ----------
        var clk = Section(gpu, "CLOCKS");
        if (spec != null)
        {
            clk.Add("Base (referência)", $"{spec.BaseMHz} MHz");
            clk.Add("Boost (referência)", $"{spec.BoostMHz} MHz");
        }
        if (h != IntPtr.Zero)
        {
            if (Nvml.MaxClock(h, Nvml.ClockGraphics) is uint g) clk.Add("Máximo (curva V/F)", $"{g} MHz");
            if (Nvml.MaxClock(h, Nvml.ClockVideo) is uint v) clk.Add("Video engine máx.", $"{v} MHz");
        }
        else if (LinuxGpus.MaxDpm(Path.Combine(d, "pp_dpm_sclk")) is uint sclk) clk.Add("Máximo (driver)", $"{sclk} MHz");
        else if (Sys.ReadLong(Path.Combine(c.Card, "gt_max_freq_mhz")) is long imax) clk.Add("Máximo (driver)", $"{imax} MHz");
        if (clk.Children.Count == 0) clk.Add("Sem dados", "—");

        // ---------- Barramento ----------
        var pcie = Section(gpu, "BARRAMENTO");
        uint? maxGen = h != IntPtr.Zero ? Nvml.MaxPcieGen(h) : null;
        uint? maxW = h != IntPtr.Zero ? Nvml.MaxPcieWidth(h) : null;
        maxGen ??= LinuxGpus.PcieGen(Sys.Read(Path.Combine(d, "max_link_speed")));
        maxW ??= uint.TryParse(Sys.Read(Path.Combine(d, "max_link_width")), out uint mw) ? mw : null;
        uint? curGen = h != IntPtr.Zero ? Nvml.CurPcieGen(h) : LinuxGpus.PcieGen(Sys.Read(Path.Combine(d, "current_link_speed")));
        uint? curW = h != IntPtr.Zero ? Nvml.CurPcieWidth(h) : uint.TryParse(Sys.Read(Path.Combine(d, "current_link_width")), out uint cw0) ? cw0 : null;
        if (maxGen is uint mg && maxW is uint mww && mww > 0) pcie.Add("Interface", $"{GpuInfo.PcieName(mg)} x{mww}");
        if (curGen is uint cg && curW is uint cw && cw > 0)
            pcie.Add("Em uso agora", $"{GpuInfo.PcieName(cg)} x{cw}" + (maxGen > curGen ? " (economia de energia em repouso)" : ""));
        pcie.Add("Localização", $"PCI {c.Slot}");

        // ---------- Energia ----------
        string? hw = c.Hwmon;
        long? capUw = hw != null ? Sys.ReadLong(Path.Combine(hw, "power1_cap")) : null;
        if (h != IntPtr.Zero || spec?.TdpW > 0 || capUw > 0)
        {
            var pwr = Section(gpu, "ENERGIA");
            if (spec?.TdpW > 0) pwr.Add("TDP (referência)", $"{spec.TdpW} W");
            if (h != IntPtr.Zero)
            {
                if (Nvml.PowerLimitMw(h) is uint lim) pwr.Add("Limite atual", $"{lim / 1000.0:0} W");
                if (Nvml.PowerDefaultMw(h) is uint def) pwr.Add("Limite padrão da placa", $"{def / 1000.0:0} W");
                if (Nvml.PowerConstraintsMw(h) is { } pc) pwr.Add("Faixa ajustável", $"{pc.Min / 1000.0:0} – {pc.Max / 1000.0:0} W");
            }
            else if (capUw > 0 && hw != null)
            {
                pwr.Add("Limite atual", $"{capUw / 1e6:0} W");
                if (Sys.ReadLong(Path.Combine(hw, "power1_cap_default")) is long defUw) pwr.Add("Limite padrão da placa", $"{defUw / 1e6:0} W");
                if (Sys.ReadLong(Path.Combine(hw, "power1_cap_min")) is long minUw && Sys.ReadLong(Path.Combine(hw, "power1_cap_max")) is long maxUw)
                    pwr.Add("Faixa ajustável", $"{minUw / 1e6:0} – {maxUw / 1e6:0} W");
            }
        }

        // ---------- Driver e BIOS ----------
        var drv = Section(gpu, "DRIVER E BIOS");
        string? vbios = h != IntPtr.Zero ? Nvml.Vbios(h) : Sys.Read(Path.Combine(d, "vbios_version"));
        drv.Add("BIOS (VBIOS)", vbios ?? "—");
        drv.Add("Driver", c.Driver.Length > 0 ? c.Driver : "nenhum (placa sem driver)");
        drv.Add("Versão do driver", DriverVersion(c));
        if (h != IntPtr.Zero)
        {
            if (Nvml.CudaVersion() is string cuda) drv.Add("Versão CUDA", cuda);
            if (Nvml.ComputeCapability(h) is string cc) drv.Add("Compute capability", cc);
        }
        if (c.Driver == "nouveau") drv.Add("Observação", "Driver nouveau (livre): sem sensores completos e com desempenho bem menor. Para jogos, instale o driver NVIDIA da distribuição.");

        // ---------- Tecnologias ----------
        var tech = Section(gpu, "TECNOLOGIAS");
        tech.Add("DirectX", "Pelo Proton (DXVK / VKD3D-Proton, sobre Vulkan)");
        var apis = new List<string>();
        if (h != IntPtr.Zero && HasLib("libcuda.so.1")) apis.Add("CUDA");
        if (HasLib("libOpenCL.so.1")) apis.Add("OpenCL");
        if (HasLib("libvulkan.so.1")) apis.Add("Vulkan");
        if (HasLib("libGL.so.1") || HasLib("libEGL.so.1")) apis.Add("OpenGL");
        if (c.VendorId == "1002" && Directory.Exists("/opt/rocm")) apis.Add("ROCm");
        tech.Add("APIs", apis.Count > 0 ? string.Join(" · ", apis) : "—");
        if (spec != null && GpuSpecDb.ArchFeatures.TryGetValue(spec.Arch, out var feats)) tech.Add("Recursos da arquitetura", feats);

        string memLabel = spec?.MemType.StartsWith("GDDR") == true ? spec.MemType : "";
        gpu.Subtitle = string.Join("  ·  ", new[]
        {
            spec?.Chip, spec?.Arch, spec?.Process,
            vram >= 1UL << 30 && !igpu ? $"{Math.Ceiling(vram / (double)(1UL << 30) - 0.15):0} GB {memLabel}".Trim() : null,
            c.SubVendor.Length > 0 && c.SubVendor != "0000" ? GpuSpecDb.SubvendorName(c.SubVendor) : null
        }.Where(s => !string.IsNullOrEmpty(s)));
        return gpu;
    }

    private static readonly string[] LibDirs =
    {
        "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib64", "/usr/lib", "/lib64", "/lib",
        "/usr/lib/aarch64-linux-gnu", "/run/host/usr/lib", // Debian/Ubuntu, Fedora/Arch, ARM e Flatpak
    };

    /// <summary>A biblioteca está instalada (só procura o arquivo: carregar libcuda/libGL aqui teria efeitos colaterais).</summary>
    private static bool HasLib(string name) => LibDirs.Any(dir => File.Exists(Path.Combine(dir, name)));
}

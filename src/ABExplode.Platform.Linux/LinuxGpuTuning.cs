namespace ABExplode.Platform.Linux;

/// <summary>
/// RESET OC no Linux: limite de energia e teto de clock da GPU. Ajustar exige root, então vai pelo pkexec: NVIDIA pelo
/// nvidia-smi (-pl e -lgc), AMD pelo power1_cap do hwmon do amdgpu. Como no Windows, vale até reiniciar.
/// </summary>
public sealed class LinuxGpuTuning
{
    public string Vendor { get; private init; } = "";
    public double MinW { get; private init; }
    public double MaxW { get; private init; }
    public double DefaultW { get; private init; }
    public bool CanCapClock => Vendor == "NVIDIA";
    private string _busId = "", _hwmon = "";
    private IntPtr _nvml;

    /// <summary>Limite de energia em uso agora (W).</summary>
    public double? CurrentW => Vendor == "NVIDIA" ? Nvml.PowerLimitMw(_nvml) is uint mw ? mw / 1000.0 : null
                             : Sys.ReadLong(Path.Combine(_hwmon, "power1_cap")) is long uw ? uw / 1e6 : null;

    /// <summary>Ajustes da placa com este nome; null se ela não permitir (Intel, nouveau, AMD sem power1_cap).</summary>
    public static LinuxGpuTuning? For(string gpuName)
    {
        var c = LinuxGpus.All().FirstOrDefault(g => g.Name == gpuName);
        if (c == null) return null;
        if (c.NvmlHandle != IntPtr.Zero && Sys.HasCommand("nvidia-smi"))
        {
            var range = Nvml.PowerConstraintsMw(c.NvmlHandle);
            return new LinuxGpuTuning
            {
                Vendor = "NVIDIA", _nvml = c.NvmlHandle, _busId = c.Slot,
                MinW = (range?.Min ?? 0) / 1000.0, MaxW = (range?.Max ?? 0) / 1000.0,
                DefaultW = (Nvml.PowerDefaultMw(c.NvmlHandle) ?? 0) / 1000.0,
            };
        }
        if (c.VendorId == "1002" && c.Hwmon is string hw && File.Exists(Path.Combine(hw, "power1_cap")))
        {
            double R(string f) => (Sys.ReadLong(Path.Combine(hw, f)) ?? 0) / 1e6;
            return new LinuxGpuTuning
            {
                Vendor = "AMD", _hwmon = hw,
                MinW = R("power1_cap_min"), MaxW = R("power1_cap_max"), DefaultW = R("power1_cap_default"),
            };
        }
        return null;
    }

    /// <summary>Aplica o limite de energia e o teto de clock (null = sem teto). Uma pedida de senha. Devolve o que aconteceu.</summary>
    public async Task<string> ApplyAsync(double watts, uint? capMhz)
    {
        string script = Vendor == "NVIDIA"
            ? $"nvidia-smi -i {_busId} -pl {watts:0} && " + (capMhz is uint cap ? $"nvidia-smi -i {_busId} -lgc 210,{cap}" : $"nvidia-smi -i {_busId} -rgc")
            : $"echo {(long)(watts * 1e6)} > {Path.Combine(_hwmon, "power1_cap")}";
        var (ok, output) = await Sys.RunAsRootAsync(script);
        if (!ok) return "Não aplicado: " + output;
        return $"Energia: {watts:0} W aplicado." + (CanCapClock ? capMhz is uint c ? $" Teto de clock: {c} MHz aplicado." : " Teto de clock: removido." : "");
    }

    /// <summary>Volta ao padrão de fábrica.</summary>
    public async Task<string> ResetAsync()
    {
        string script = Vendor == "NVIDIA"
            ? $"nvidia-smi -i {_busId} -pl {DefaultW:0} && nvidia-smi -i {_busId} -rgc"
            : $"echo {(long)(DefaultW * 1e6)} > {Path.Combine(_hwmon, "power1_cap")}";
        var (ok, output) = await Sys.RunAsRootAsync(script);
        return ok ? "GPU de volta ao padrão de fábrica." : "Restauração parcial: " + output;
    }
}

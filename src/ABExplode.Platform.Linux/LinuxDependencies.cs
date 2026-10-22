using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ABExplode.Platform.Linux;

/// <summary>Algo que falta no sistema para o app rodar ou ler tudo.</summary>
/// <param name="Key">Identificador estável (para "não perguntar de novo").</param>
/// <param name="Packages">Nome do pacote em cada gerenciador (apt, dnf, pacman, zypper).</param>
/// <param name="RootCommand">Ação que não é pacote (carregar módulo do kernel), rodada como root.</param>
public sealed record LinuxDependency(string Key, string Title, string Why, IReadOnlyDictionary<string, string> Packages, string? RootCommand = null);

/// <summary>Gerenciador de pacotes da distro.</summary>
public sealed record PackageManager(string Name, string Install)
{
    /// <summary>Distro imutável (Fedora Silverblue/Kinoite, SteamOS...): não dá para instalar pelo app.</summary>
    public bool Immutable { get; init; }
}

/// <summary>
/// Verifica o que falta (bibliotecas da interface, ferramentas que completam as leituras, módulo de sensores da CPU),
/// diz como instalar em cada distro e, com a permissão do usuário, instala pelo pkexec (a janela de senha do sistema).
/// </summary>
public static class LinuxDependencies
{
    private static Dictionary<string, string> Same(string pkg) => new() { ["apt"] = pkg, ["dnf"] = pkg, ["pacman"] = pkg, ["zypper"] = pkg };

    // ------------------------------------------------------------------ interface (sem isto a janela nem abre)

    /// <summary>Bibliotecas que o Avalonia carrega no Linux (X11, também usadas sob Wayland pelo XWayland) e a do Skia.</summary>
    private static readonly (string Lib, Dictionary<string, string> Pkg)[] GuiLibs =
    {
        ("libX11.so.6", new() { ["apt"] = "libx11-6", ["dnf"] = "libX11", ["pacman"] = "libx11", ["zypper"] = "libX11-6" }),
        ("libICE.so.6", new() { ["apt"] = "libice6", ["dnf"] = "libICE", ["pacman"] = "libice", ["zypper"] = "libICE6" }),
        ("libSM.so.6", new() { ["apt"] = "libsm6", ["dnf"] = "libSM", ["pacman"] = "libsm", ["zypper"] = "libSM6" }),
        ("libXi.so.6", new() { ["apt"] = "libxi6", ["dnf"] = "libXi", ["pacman"] = "libxi", ["zypper"] = "libXi6" }),
        ("libXrandr.so.2", new() { ["apt"] = "libxrandr2", ["dnf"] = "libXrandr", ["pacman"] = "libxrandr", ["zypper"] = "libXrandr2" }),
        ("libXcursor.so.1", new() { ["apt"] = "libxcursor1", ["dnf"] = "libXcursor", ["pacman"] = "libxcursor", ["zypper"] = "libXcursor1" }),
        ("libfontconfig.so.1", new() { ["apt"] = "libfontconfig1", ["dnf"] = "fontconfig", ["pacman"] = "fontconfig", ["zypper"] = "fontconfig" }),
    };

    /// <summary>Bibliotecas da interface que não estão instaladas.</summary>
    public static List<LinuxDependency> MissingGui()
    {
        var list = new List<LinuxDependency>();
        if (Sys.InFlatpak) return list; // o runtime do Flatpak já traz as bibliotecas gráficas
        foreach (var (lib, pkg) in GuiLibs)
        {
            if (NativeLibrary.TryLoad(lib, out var h)) { NativeLibrary.Free(h); continue; }
            list.Add(new LinuxDependency("gui:" + lib, lib, "biblioteca gráfica usada pela janela do app", pkg));
        }
        return list;
    }

    /// <summary>Há um ambiente gráfico para abrir a janela (X11 ou Wayland).</summary>
    public static bool HasDisplay =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    // ------------------------------------------------------------------ ferramentas (o app abre, mas lê menos)

    /// <summary>O que completa as leituras e não está instalado, do mais importante para o menos.</summary>
    public static List<LinuxDependency> MissingTools()
    {
        var list = new List<LinuxDependency>();
        if (!Sys.HasCommand("lspci"))
            list.Add(new("pciutils", "pciutils (lspci)", "nome comercial das placas de vídeo e dos dispositivos PCI", Same("pciutils")));
        if (!Sys.HasCommand("smartctl"))
            list.Add(new("smartmontools", "smartmontools (smartctl)", "saúde dos discos: desgaste, temperatura, setores ruins (tela Discos)", Same("smartmontools")));
        if (!Sys.HasCommand("lsblk"))
            list.Add(new("util-linux", "util-linux (lsblk)", "lista de discos físicos", new Dictionary<string, string> { ["apt"] = "util-linux", ["dnf"] = "util-linux", ["pacman"] = "util-linux", ["zypper"] = "util-linux" }));
        if (Sys.IsRoot && !Sys.HasCommand("dmidecode")) // só é usado como root
            list.Add(new("dmidecode", "dmidecode", "detalhes dos módulos de RAM quando o udev não traz (aberto como root)", Same("dmidecode")));
        if (!Sys.HasCommand("vulkaninfo") && LinuxGpus.All().Any(g => g.NvmlHandle == IntPtr.Zero)) // a NVIDIA já dá o nome pela NVML
            list.Add(new("vulkan-tools", "vulkan-tools (vulkaninfo)", "nome exato da placa de vídeo AMD/Intel e versão do Vulkan",
                         new Dictionary<string, string> { ["apt"] = "vulkan-tools", ["dnf"] = "vulkan-tools", ["pacman"] = "vulkan-tools", ["zypper"] = "vulkan-tools" }));
        if (CpuSensorModule() is string mod)
            list.Add(new("module:" + mod, $"Módulo de sensores da CPU ({mod})", "temperatura da CPU (Visão geral, Sensores, Stress test e Gargalo)",
                         new Dictionary<string, string>(),
                         // carrega agora e em todo boot
                         $"modprobe {mod} && echo {mod} > /etc/modules-load.d/abexplode-sensors.conf"));
        return list;
    }

    /// <summary>Módulo do kernel que lê a temperatura desta CPU, se ele não estiver carregado (null em VM ou se já estiver).</summary>
    private static string? CpuSensorModule()
    {
        var names = Sys.Dirs("/sys/class/hwmon", "hwmon*").Select(d => Sys.Read(Path.Combine(d, "name")) ?? "").ToHashSet();
        if (names.Overlaps(new[] { "coretemp", "k10temp", "zenpower", "cpu_thermal" })) return null;
        string cpuinfo = string.Join('\n', Sys.Lines("/proc/cpuinfo").Take(40));
        if (cpuinfo.Contains("hypervisor")) return null; // máquina virtual: a CPU não tem sensores para ler
        if (cpuinfo.Contains("GenuineIntel")) return "coretemp";
        if (cpuinfo.Contains("AuthenticAMD")) return "k10temp";
        return null;
    }

    // ------------------------------------------------------------------ instalação

    public static PackageManager? Detect()
    {
        bool immutable = File.Exists("/run/ostree-booted") || Sys.Read("/etc/os-release")?.Contains("ID=steamos") == true;
        PackageManager? pm =
            Sys.HasCommand("apt-get") ? new("apt", "apt-get install -y") :
            Sys.HasCommand("dnf") ? new("dnf", "dnf install -y") :
            Sys.HasCommand("pacman") ? new("pacman", "pacman -S --needed --noconfirm") :
            Sys.HasCommand("zypper") ? new("zypper", "zypper --non-interactive install") :
            File.Exists("/run/ostree-booted") && Sys.HasCommand("rpm-ostree") ? new("dnf", "rpm-ostree install") :
            null;
        return pm == null ? null : pm with { Immutable = immutable };
    }

    /// <summary>Comandos para o usuário copiar e rodar no terminal (o que o app rodaria).</summary>
    public static string Instructions(IReadOnlyList<LinuxDependency> deps, PackageManager? pm)
    {
        var lines = new List<string>();
        var pkgs = Packages(deps, pm);
        if (pkgs.Count > 0)
            lines.Add(pm != null ? $"sudo {pm.Install} {string.Join(' ', pkgs)}"
                                 : "Instale pelo gerenciador de pacotes da sua distribuição: " + string.Join(", ", deps.Where(d => d.Packages.Count > 0).Select(d => d.Packages["apt"])));
        foreach (var d in deps.Where(d => d.RootCommand != null))
            lines.Add($"sudo sh -c '{d.RootCommand}'");
        return string.Join("\n", lines);
    }

    private static List<string> Packages(IEnumerable<LinuxDependency> deps, PackageManager? pm) =>
        pm == null ? new() : deps.Where(d => d.Packages.ContainsKey(pm.Name)).Select(d => d.Packages[pm.Name]).Distinct().ToList();

    /// <summary>Dá para instalar pelo app: há gerenciador conhecido, a distro não é imutável e há como pedir a senha (pkexec ou já é root).</summary>
    public static bool CanInstall(PackageManager? pm) => pm is { Immutable: false } && (Sys.IsRoot || Sys.HasCommand("pkexec"));

    /// <summary>
    /// Instala os pacotes e roda as ações como root numa única pedida de senha (pkexec abre a janela do sistema).
    /// Devolve se deu certo e a saída, para mostrar em caso de erro.
    /// </summary>
    public static async Task<(bool Ok, string Output)> InstallAsync(IReadOnlyList<LinuxDependency> deps, PackageManager pm)
    {
        var steps = new List<string>();
        var pkgs = Packages(deps, pm);
        if (pkgs.Count > 0)
        {
            if (pm.Name == "apt") steps.Add("apt-get update");
            steps.Add($"{pm.Install} {string.Join(' ', pkgs)}");
        }
        steps.AddRange(deps.Where(d => d.RootCommand != null).Select(d => d.RootCommand!));
        if (steps.Count == 0) return (true, "");

        var r = await Sys.RunAsRootAsync(string.Join(" && ", steps), "DEBIAN_FRONTEND=noninteractive");
        Sys.ForgetCommands(); // o que acabou de ser instalado passa a ser encontrado
        return r;
    }
}

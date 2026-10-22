using System.Globalization;

namespace ABExplode.Platform.Linux;

/// <summary>
/// Overlay dentro do jogo no Linux pelo MangoHud. O Linux não tem um rastreamento de quadros como o ETW e, no Wayland, uma
/// janela por cima de jogo em tela cheia não aparece; o MangoHud é uma camada do Vulkan/OpenGL dentro do próprio jogo.
/// O app escreve o MangoHud.conf com as mesmas escolhas da tela Overlay (o que mostrar, canto, tamanho, atalho).
/// </summary>
public static class LinuxMangoHud
{
    private const string Marker = "# Gerado pelo ABExplode (tela Overlay). Para editar à mão, apague esta linha: o ABExplode deixa de sobrescrever.";
    private const string FlatpakLayer = "org.freedesktop.Platform.VulkanLayer.MangoHud";
    private const string SteamFlatpak = "com.valvesoftware.Steam";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>MangoHud da distribuição (comando mangohud).</summary>
    public static bool NativeInstalled => Sys.HasCommand("mangohud");

    private static HashSet<string> FlatpakRuntimes() =>
        (Sys.Run("flatpak", "list --runtime --columns=application", 15000) ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// <summary>Steam do Flatpak instalada (o MangoHud dela é outra camada, do Flathub).</summary>
    public static bool SteamFlatpakInstalled =>
        (Sys.Run("flatpak", "list --app --columns=application", 15000) ?? "").Split('\n', StringSplitOptions.TrimEntries).Contains(SteamFlatpak);

    public static bool FlatpakLayerInstalled => FlatpakRuntimes().Contains(FlatpakLayer);

    // O da distribuição serve à Steam nativa e aos outros jogos; a Steam do Flatpak precisa da camada do Flathub.
    private static bool NeedNative => Sys.HasCommand("steam") || !SteamFlatpakInstalled;
    private static bool NeedLayer => SteamFlatpakInstalled;

    /// <summary>Instalado para todos os jogos que vão usar: o da distribuição e, se houver Steam do Flatpak, a camada dela.</summary>
    public static bool Installed => (!NeedNative || NativeInstalled) && (!NeedLayer || FlatpakLayerInstalled);

    public static readonly LinuxDependency NativeDep = new("mangohud", "MangoHud", "FPS e sensores dentro do jogo",
        new Dictionary<string, string> { ["apt"] = "mangohud", ["dnf"] = "mangohud", ["pacman"] = "mangohud", ["zypper"] = "mangohud" });

    /// <summary>Instala o que falta: o da distribuição (pede senha) e a camada do Flathub para a Steam do Flatpak (sem senha).</summary>
    public static async Task<string> InstallAsync()
    {
        var msgs = new List<string>();
        if (NeedNative && !NativeInstalled)
        {
            var pm = LinuxDependencies.Detect();
            if (!LinuxDependencies.CanInstall(pm)) msgs.Add("Instale pelo terminal: " + LinuxDependencies.Instructions(new[] { NativeDep }, pm));
            else
            {
                var (ok, output) = await LinuxDependencies.InstallAsync(new[] { NativeDep }, pm!);
                msgs.Add(ok ? "MangoHud instalado." : "Não foi possível instalar o MangoHud: " + output);
            }
        }
        if (NeedLayer && !FlatpakLayerInstalled)
        {
            // A camada tem de ser da mesma versão do runtime que a Steam do Flatpak usa (ex.: 24.08)
            string? runtime = (Sys.Run("flatpak", $"info {SteamFlatpak}", 15000) ?? "").Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("Runtime:"));
            string branch = runtime?.Split('/').LastOrDefault()?.Trim() ?? "";
            string target = branch.Length > 0 ? $"{FlatpakLayer}//{branch}" : FlatpakLayer;
            var r = await Task.Run(() => Sys.Run("flatpak", $"install --user -y --noninteractive flathub {target}", 600000));
            msgs.Add(r != null ? "MangoHud da Steam do Flatpak instalado." : "Não foi possível instalar o MangoHud da Steam do Flatpak (flatpak install " + target + ").");
        }
        return msgs.Count == 0 ? "O MangoHud já está instalado." : string.Join(" ", msgs);
    }

    // ------------------------------------------------------------------ configuração

    /// <summary>Arquivos de configuração que o MangoHud lê: o do usuário e, com a Steam do Flatpak, o da pasta dela.</summary>
    private static IEnumerable<string> ConfigFiles()
    {
        yield return Path.Combine(Home, ".config", "MangoHud", "MangoHud.conf");
        string steamVar = Path.Combine(Home, ".var", "app", SteamFlatpak);
        if (Directory.Exists(steamVar)) yield return Path.Combine(steamVar, "config", "MangoHud", "MangoHud.conf");
    }

    /// <summary>Existe um MangoHud.conf feito à mão (sem a marca do ABExplode), que não deve ser sobrescrito sem perguntar.</summary>
    public static bool HasUserConfig => ConfigFiles().Any(f => File.Exists(f) && !File.ReadLines(f).Take(3).Any(l => l == Marker));

    /// <summary>Opções do MangoHud para cada item da tela Overlay. Os que o MangoHud já mostra sozinho vão com =0 quando desmarcados.</summary>
    private static readonly (string Key, string Option, bool OnByDefault)[] Map =
    {
        ("fps", "fps", true), ("frametime", "frametime", true), ("graph", "frame_timing", true),
        ("gpu_load", "gpu_stats", true), ("gpu_temp", "gpu_temp", false), ("gpu_hotspot", "gpu_junction_temp", false),
        ("gpu_clock", "gpu_core_clock", false), ("gpu_memclock", "gpu_mem_clock", false), ("gpu_power", "gpu_power", false),
        ("gpu_volt", "gpu_voltage", false), ("gpu_fan", "gpu_fan", false), ("gpu_perfcap", "throttling_status", false),
        ("vram_used", "vram", false), ("vram_temp", "gpu_mem_temp", false),
        ("cpu_load", "cpu_stats", true), ("cpu_maxcore", "core_load", false), ("cpu_temp", "cpu_temp", false),
        ("cpu_clock", "cpu_mhz", false), ("cpu_power", "cpu_power", false),
        ("ram_used", "ram", false), ("game", "exec_name", false), ("clock", "time", false),
    };

    public static string BuildConfig(IReadOnlySet<string> show, string corner, double scale)
    {
        var lines = new List<string> { Marker };
        foreach (var (key, option, onByDefault) in Map)
        {
            bool on = show.Contains(key) || key == "vram_used" && show.Contains("vram_pct") || key == "ram_used" && show.Contains("ram_pct");
            if (on) lines.Add(option);
            else if (onByDefault) lines.Add(option + "=0");
        }
        if (show.Contains("low1")) lines.Add("fps_metrics=avg,0.01"); // média e 1% low
        lines.Add("position=" + corner switch { "TR" => "top-right", "BL" => "bottom-left", "BR" => "bottom-right", _ => "top-left" });
        lines.Add("font_scale=" + scale.ToString("0.##", CultureInfo.InvariantCulture));
        lines.Add("toggle_hud=Control_L+Shift_L+o"); // mesmo atalho do overlay do ABExplode
        lines.Add("background_alpha=0.6");
        lines.Add("round_corners=4");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Grava o MangoHud.conf. overwrite: substitui também um arquivo feito à mão (o usuário confirmou). Devolve quantos gravou.</summary>
    public static int WriteConfig(IReadOnlySet<string> show, string corner, double scale, bool overwrite)
    {
        string text = BuildConfig(show, corner, scale);
        int n = 0;
        foreach (var f in ConfigFiles())
        {
            try
            {
                if (File.Exists(f) && !overwrite && !File.ReadLines(f).Take(3).Any(l => l == Marker)) continue;
                if (File.Exists(f) && !File.ReadLines(f).Take(3).Any(l => l == Marker)) File.Copy(f, f + ".antes-do-abexplode", true);
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, text);
                n++;
            }
            catch { }
        }
        return n;
    }

    /// <summary>Opções de inicialização da Steam para ligar o MangoHud no jogo.</summary>
    public const string SteamPrefix = "mangohud";
}

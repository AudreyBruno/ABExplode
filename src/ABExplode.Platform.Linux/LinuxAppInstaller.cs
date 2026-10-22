using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ABExplode.Platform.Linux;

/// <summary>App do catálogo "Instalar apps" no Linux, instalado pelo Flatpak (Flathub). Mesmas propriedades do modelo do Windows.</summary>
public sealed class LinuxInstallableApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string FlatpakId { get; init; } = "";
    /// <summary>Comandos que indicam que ele já está instalado pela distribuição (fora do Flatpak).</summary>
    public string[] NativeCommands { get; init; } = Array.Empty<string>();
    public bool PreSelect { get; init; } = true;

    private bool _selected, _installed;
    private string _status = "";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool Installed
    {
        get => _installed;
        set { Set(ref _installed, value); OnChanged(nameof(CanSelect)); }
    }
    public bool CanSelect => !Installed;
    public string Status { get => _status; set => Set(ref _status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(p);
    }
}

/// <summary>
/// Catálogo de apps essenciais pelo Flathub: funciona em qualquer distribuição, instala só para o usuário (sem senha)
/// e traz sempre a versão oficial mais recente. O que já existe pela distribuição (ex.: Steam do apt) conta como instalado.
/// </summary>
public static class LinuxAppInstaller
{
    public static List<LinuxInstallableApp> Catalog() => new()
    {
        new() { Name = "Steam", Description = "Loja e biblioteca de jogos da Valve, com o Proton para jogos de Windows.", FlatpakId = "com.valvesoftware.Steam", NativeCommands = new[] { "steam" } },
        new() { Name = "Heroic Games Launcher", Description = "Jogos da Epic Games, GOG e Amazon, com o Proton/Wine já configurado.", FlatpakId = "com.heroicgameslauncher.hgl", NativeCommands = new[] { "heroic" }, PreSelect = false },
        new() { Name = "ProtonUp-Qt", Description = "Instala versões extras do Proton (Proton-GE), que fazem mais jogos rodarem.", FlatpakId = "net.davidotek.pupgui2", NativeCommands = new[] { "protonup-qt" }, PreSelect = false },
        new() { Name = "Google Chrome", Description = "Navegador do Google.", FlatpakId = "com.google.Chrome", NativeCommands = new[] { "google-chrome", "google-chrome-stable" } },
        new() { Name = "Discord", Description = "Chat de voz e texto para jogar com os amigos.", FlatpakId = "com.discordapp.Discord", NativeCommands = new[] { "discord" } },
        new() { Name = "Flameshot", Description = "Captura de tela com anotações (setas, texto, destaque).", FlatpakId = "org.flameshot.Flameshot", NativeCommands = new[] { "flameshot" } },
        new() { Name = "PeaZip", Description = "Compactador que abre e cria zip, rar, 7z e outros (o sistema já abre zip).", FlatpakId = "io.github.peazip.PeaZip", NativeCommands = new[] { "peazip" }, PreSelect = false },
        new() { Name = "VLC", Description = "Player de vídeo e música que abre qualquer formato.", FlatpakId = "org.videolan.VLC", NativeCommands = new[] { "vlc" } },
        new() { Name = "OBS Studio", Description = "Gravação e transmissão de tela (lives, vídeos de gameplay).", FlatpakId = "com.obsproject.Studio", NativeCommands = new[] { "obs" }, PreSelect = false },
    };

    public static bool FlatpakAvailable => Sys.HasCommand("flatpak");

    public static readonly LinuxDependency FlatpakDep = new("flatpak", "Flatpak", "instalar os apps do catálogo pelo Flathub",
        new Dictionary<string, string> { ["apt"] = "flatpak", ["dnf"] = "flatpak", ["pacman"] = "flatpak", ["zypper"] = "flatpak" });

    /// <summary>IDs dos apps Flatpak instalados (do usuário e do sistema).</summary>
    private static HashSet<string> InstalledFlatpaks() =>
        (Sys.Run("flatpak", "list --app --columns=application", 15000) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void RefreshInstalled(IEnumerable<LinuxInstallableApp> apps)
    {
        var flatpaks = FlatpakAvailable ? InstalledFlatpaks() : new HashSet<string>();
        foreach (var a in apps)
        {
            a.Installed = flatpaks.Contains(a.FlatpakId) || a.NativeCommands.Any(Sys.HasCommand);
            if (a.Installed) a.Selected = false;
        }
    }

    /// <summary>Atualiza um app Flatpak (do usuário ou do sistema); devolve a saída do flatpak, ou null se falhou.</summary>
    public static string? UpdateFlatpak(string id) => Sys.Run("flatpak", $"update -y --noninteractive {id}", 600000);

    private static bool _remoteReady;

    /// <summary>Instala um app do Flathub só para este usuário. Devolve (ok, mensagem curta).</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(LinuxInstallableApp app, CancellationToken ct)
    {
        if (!_remoteReady)
        {
            // O remoto do usuário (sem senha); se o do sistema já existir, o flatpak usa qualquer um dos dois
            Sys.Run("flatpak", "remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo", 60000);
            _remoteReady = true;
        }
        var psi = Sys.HostProcess("flatpak", new[] { "install", "--user", "-y", "--noninteractive", "flathub", app.FlatpakId });
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o flatpak.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        string output = (await outTask + "\n" + await errTask).Trim();
        if (p.ExitCode == 0) return (true, output.Contains("already installed", StringComparison.OrdinalIgnoreCase) ? "Já estava instalado." : "Instalado.");
        string last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 3) ?? "";
        if (last.Contains("No remote refs found", StringComparison.OrdinalIgnoreCase) || last.Contains("Nothing matches", StringComparison.OrdinalIgnoreCase))
            return (false, "Não encontrado no Flathub.");
        if (last.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase) || last.Contains("Couldn't resolve", StringComparison.OrdinalIgnoreCase))
            return (false, "Falha no download: confira a internet e tente de novo.");
        return (false, $"Falhou (código {p.ExitCode}). {last}".Trim());
    }
}

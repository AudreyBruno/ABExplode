using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ABExplode.Platform.Linux;

/// <summary>Programa do usuário (agrupado pelo nome do processo) que pode ser fechado. Mesmas propriedades do modelo do Windows.</summary>
public sealed class LinuxApp : INotifyPropertyChanged
{
    public string ProcessName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public long MemoryBytes { get; init; }
    public List<int> Pids { get; init; } = new();
    public bool Suggested { get; init; }
    public string MemoryText => $"{MemoryBytes / 1048576.0:N0} MB";

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record LinuxCleanup(long FreedBytes, bool CacheDropped, string? Error);

/// <summary>
/// RESET OTIMIZA no Linux: fecha programas do usuário e esvazia o cache de páginas (o equivalente da memória em espera
/// do Windows). A lista tem os aplicativos que têm atalho no menu (.desktop) e processos de segundo plano conhecidos.
/// </summary>
public static class LinuxOptimizer
{
    /// <summary>Nunca aparecem: sessão gráfica, áudio, Steam e o que ela usa para jogar, terminais e o próprio app.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemd", "dbus-daemon", "dbus-broker", "gnome-shell", "gnome-session-binary", "plasmashell", "kwin_wayland", "kwin_x11", "Xorg",
        "Xwayland", "mutter", "xfwm4", "xfce4-panel", "xfce4-session", "cinnamon", "muffin", "budgie-wm", "pipewire", "pipewire-pulse",
        "wireplumber", "pulseaudio", "gvfsd", "xdg-desktop-portal", "xdg-document-portal", "xdg-permission-store", "ibus-daemon", "fcitx5",
        "at-spi-bus-launcher", "at-spi2-registryd", "gsd-xsettings", "polkit-gnome-authentication-agent-1", "polkit-kde-authentication-agent-1",
        "steam", "steamwebhelper", "gamemoded", "gamescope", "mangohud", "ABExplode", "bash", "sh", "zsh", "fish", "sudo", "ssh-agent",
        "gnome-terminal-server", "konsole", "kitty", "alacritty", "wezterm-gui", "tilix", "xfce4-terminal", "ptyxis", "nautilus", "dolphin",
        "nemo", "thunar", "gnome-keyring-daemon", "kwalletd5", "kwalletd6", "evolution-data-server", "goa-daemon", "tracker-extract-3",
        "flatpak-portal", "flatpak-session-helper", "bwrap", "dotnet", "code", "systemsettings",
    };

    /// <summary>Segundo plano seguros de fechar durante o jogo (pré-marcados). Indexadores e sincronizadores voltam sozinhos depois.</summary>
    private static readonly Dictionary<string, string> SuggestedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tracker-miner-fs-3"] = "Indexação de arquivos do GNOME (usa disco e CPU)",
        ["localsearch-3"] = "Indexação de arquivos do GNOME (usa disco e CPU)",
        ["baloo_file"] = "Indexação de arquivos do KDE (usa disco e CPU)",
        ["baloo_file_extractor"] = "Indexação de arquivos do KDE (usa disco e CPU)",
        ["dropbox"] = "Dropbox",
        ["onedrive"] = "OneDrive",
        ["megasync"] = "MEGA",
        ["nextcloud"] = "Nextcloud",
        ["insync"] = "Insync (Google Drive / OneDrive)",
        ["teams-for-linux"] = "Microsoft Teams",
        ["skypeforlinux"] = "Skype",
        ["zoom"] = "Zoom",
        ["packagekitd"] = "Atualizações em segundo plano (PackageKit)",
        ["gnome-software"] = "Loja de aplicativos do GNOME (busca atualizações)",
        ["plasma-discover"] = "Loja de aplicativos do KDE (busca atualizações)",
    };

    [DllImport("libc")] private static extern uint getuid();
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);

    /// <summary>Executável → nome do aplicativo, pelos atalhos do menu (.desktop) do sistema, do usuário e do Flatpak.</summary>
    private static Dictionary<string, string> DesktopApps()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // No Flatpak, o /usr do sistema fica em /run/host/usr (permissão host-os)
        string usr = Sys.InFlatpak ? "/run/host/usr" : "/usr";
        foreach (var dir in new[] { usr + "/share/applications", usr + "/local/share/applications", Path.Combine(home, ".local/share/applications"),
                                    "/var/lib/flatpak/exports/share/applications", Path.Combine(home, ".local/share/flatpak/exports/share/applications"),
                                    "/var/lib/snapd/desktop/applications" })
        {
            string[] files;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.desktop") : Array.Empty<string>(); } catch { continue; }
            foreach (var f in files)
            {
                string? name = null, exec = null;
                bool inEntry = false;
                foreach (var line in Sys.Lines(f))
                {
                    if (line.StartsWith('[')) { if (inEntry) break; inEntry = line == "[Desktop Entry]"; continue; }
                    if (!inEntry) continue;
                    if (line.StartsWith("Name=")) name ??= line[5..];
                    else if (line.StartsWith("Exec=")) exec ??= line[5..];
                }
                if (name == null || exec == null) continue;
                // "flatpak run ... org.mozilla.firefox" e "env X=1 /usr/bin/app %U": fica com o executável de verdade
                var parts = exec.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.Contains('=') && !p.StartsWith('%')).ToList();
                if (parts.Count == 0) continue;
                string bin = parts[0] == "env" && parts.Count > 1 ? parts[1] : parts[0];
                if (bin.EndsWith("flatpak"))
                {
                    // "flatpak run --command=firefox org.mozilla.firefox @@u %u @@": o processo é o --command, senão o fim do ID do app
                    string? cmd = exec.Split(' ').FirstOrDefault(p => p.StartsWith("--command="))?[10..];
                    string? appId = parts.SkipWhile(p => p != "run").Skip(1).FirstOrDefault(p => !p.StartsWith('-') && p.Count(c => c == '.') >= 2);
                    if (cmd == null && appId == null) continue;
                    bin = cmd ?? appId!.Split('.').Last();
                }
                map.TryAdd(Path.GetFileName(bin.Trim('"')), name);
            }
        }
        return map;
    }

    public static List<LinuxApp> ListApps()
    {
        uint uid = getuid();
        int self = Environment.ProcessId;
        var apps = DesktopApps();
        var groups = new Dictionary<string, (List<int> Pids, long Mem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, name, rss) in Processes(uid))
        {
            if (pid == self || name.Length == 0 || Protected.Contains(name)) continue;
            if (!groups.TryGetValue(name, out var g)) g = (new List<int>(), 0);
            g.Pids.Add(pid);
            groups[name] = (g.Pids, g.Mem + rss);
        }

        var result = new List<LinuxApp>();
        foreach (var (name, (pids, mem)) in groups)
        {
            bool suggested = SuggestedApps.ContainsKey(name);
            if (!suggested && !apps.ContainsKey(name)) continue; // auxiliares sem atalho no menu ficam de fora
            string detail = suggested ? SuggestedApps[name] : "Programa aberto";
            if (pids.Count > 1) detail += $" · {pids.Count} processos";
            result.Add(new LinuxApp
            {
                ProcessName = name, Title = apps.GetValueOrDefault(name) ?? name, Detail = detail, MemoryBytes = mem,
                Pids = pids, Suggested = suggested, Selected = suggested,
            });
        }
        return result.OrderByDescending(a => a.Suggested).ThenByDescending(a => a.MemoryBytes).ToList();
    }

    /// <summary>
    /// Processos do usuário: (pid, nome do executável, memória em uso). Pelo /proc; no Flatpak o /proc só mostra o
    /// sandbox, então vem do ps do sistema.
    /// </summary>
    private static IEnumerable<(int Pid, string Name, long Rss)> Processes(uint uid)
    {
        if (Sys.InFlatpak)
        {
            foreach (var line in (Sys.Run("ps", $"-u {uid} -o pid=,rss=,args=") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3 || !int.TryParse(f[0], out int pid)) continue;
                long rss = long.TryParse(f[1], out long kb) ? kb * 1024 : 0;
                yield return (pid, Path.GetFileName(f[2].Split(' ')[0]), rss);
            }
            yield break;
        }
        foreach (var d in Sys.Dirs("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(d), out int pid)) continue;
            var st = Sys.KeyValues(Path.Combine(d, "status"));
            if (!st.TryGetValue("Uid", out var u) || u.Split('\t', ' ')[0] != uid.ToString()) continue;
            string name = st.GetValueOrDefault("Name") ?? "";
            // O "Name" do kernel corta em 15 letras: o nome do executável é mais fiel
            string? exe = null;
            try { exe = new FileInfo(Path.Combine(d, "exe")).LinkTarget; } catch { }
            if (exe != null) name = Path.GetFileName(exe).Replace(" (deleted)", "");
            long rss = st.TryGetValue("VmRSS", out var r) && long.TryParse(r.Split(' ')[0], out long kb) ? kb * 1024 : 0;
            yield return (pid, name, rss);
        }
    }

    private static bool Alive(int pid) => Sys.InFlatpak ? Sys.Run("kill", $"-0 {pid}") != null : Directory.Exists($"/proc/{pid}");

    private static void Terminate(int pid)
    {
        if (Sys.InFlatpak) Sys.Run("kill", $"-TERM {pid}");
        else kill(pid, 15); // SIGTERM
    }

    /// <summary>
    /// Fecha os programas: com janela (X11 e wmctrl instalado) pede para fechar como o X da janela, sem forçar;
    /// os outros recebem SIGTERM, o pedido educado de encerrar (não é o SIGKILL do "matar processo").
    /// </summary>
    public static (List<string> Closed, List<string> NotClosed) CloseApps(IEnumerable<LinuxApp> apps)
    {
        var closed = new List<string>();
        var notClosed = new List<string>();
        // Janelas por PID: "0x04a00003  0 12345 host Título"
        var windows = new Dictionary<int, List<string>>();
        if (Sys.HasCommand("wmctrl") && Sys.Run("wmctrl", "-l -p") is string list)
            foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 3 && int.TryParse(f[2], out int pid))
                {
                    if (!windows.TryGetValue(pid, out var w)) windows[pid] = w = new List<string>();
                    w.Add(f[0]);
                }
            }

        foreach (var app in apps)
        {
            foreach (int pid in app.Pids)
            {
                if (windows.TryGetValue(pid, out var ws)) foreach (var w in ws) Sys.Run("wmctrl", $"-i -c {w}");
                else Terminate(pid);
            }
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 4 && app.Pids.Any(Alive)) Thread.Sleep(200);
            (app.Pids.Any(Alive) ? notClosed : closed).Add(app.Title);
        }
        return (closed, notClosed);
    }

    /// <summary>
    /// Esvazia o cache de páginas do kernel (drop_caches = 1), como a lista de espera do Windows. Pede a senha (root).
    /// O kernel libera esse cache sozinho quando precisa; esvaziar ajuda quando a RAM está quase cheia antes de jogar.
    /// </summary>
    public static async Task<LinuxCleanup> CleanMemoryAsync()
    {
        long before = MemFree();
        var (ok, output) = await Sys.RunAsRootAsync("sync && echo 1 > /proc/sys/vm/drop_caches");
        await Task.Delay(300);
        return new LinuxCleanup(Math.Max(0, MemFree() - before), ok, ok ? null : output);
    }

    /// <summary>Memória livre de verdade (MemFree), sem contar o cache que o kernel pode reaproveitar.</summary>
    private static long MemFree() =>
        Sys.KeyValues("/proc/meminfo").TryGetValue("MemFree", out var v) && long.TryParse(v.Split(' ')[0], out long kb) ? kb * 1024 : 0;
}

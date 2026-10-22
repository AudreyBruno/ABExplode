using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ABExplode.Platform.Linux;

/// <summary>
/// Biblioteca no Linux: abre os jogos (Steam e Heroic pelo endereço da loja, executáveis direto, .exe pelo Wine),
/// aplica o perfil enquanto o jogo roda e edita as opções de inicialização da Steam (localconfig.vdf, igual ao Windows).
/// O perfil vira: "Alto desempenho" = perfil de energia Desempenho; "GPU dedicada" = PRIME offload em notebooks com duas
/// placas; prioridade = GameMode (gamemoderun), que dá prioridade ao jogo sem precisar de root.
/// </summary>
public static class LinuxGameLauncher
{
    // ---------------- Steam: opções de inicialização ----------------

    private static string? SteamRoot() => LinuxGameLibrary.SteamRoots().FirstOrDefault();

    /// <summary>A Steam está aberta (nativa ou Flatpak).</summary>
    public static bool SteamRunning => (Sys.Run("pgrep", "-x steam") ?? "").Trim().Length > 0 || (Sys.Run("pgrep", "-f steam.sh") ?? "").Trim().Length > 0;

    private static string? LocalConfig()
    {
        string? steam = SteamRoot();
        if (steam == null) return null;
        string ud = Path.Combine(steam, "userdata");
        if (!Directory.Exists(ud)) return null;
        return Directory.GetDirectories(ud).Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static VdfNode? AppsNode(VdfNode root, bool create)
    {
        VdfNode? n = root.Child("UserLocalConfigStore");
        foreach (var key in new[] { "Software", "Valve", "Steam", "apps" })
        {
            if (n == null) return null;
            n = create ? n.GetOrAddChild(key) : n.Child(key);
        }
        return n;
    }

    public static string? GetLaunchOptions(string appId)
    {
        try
        {
            string? f = LocalConfig();
            return f == null ? null : AppsNode(Vdf.Parse(File.ReadAllText(f)), false)?.Child(appId)?.Get("LaunchOptions");
        }
        catch { return null; }
    }

    /// <summary>Grava as opções de inicialização. A Steam precisa estar fechada (senão sobrescreve ao sair). null = deu certo, ou o erro.</summary>
    public static string? SetLaunchOptions(string appId, string options)
    {
        if (SteamRunning) return "A Steam está aberta. Feche-a para gravar as opções (ela sobrescreve o arquivo ao sair).";
        string? f = LocalConfig();
        if (f == null) return "Não achei o localconfig.vdf da Steam (entre na Steam ao menos uma vez neste computador).";
        try
        {
            var root = Vdf.Parse(File.ReadAllText(f));
            var app = AppsNode(root, true)!.GetOrAddChild(appId);
            if (string.IsNullOrWhiteSpace(options)) app.Remove("LaunchOptions"); else app.Set("LaunchOptions", options.Trim());
            File.Copy(f, f + ".abexplode.bak", true); // cópia de segurança do original
            File.WriteAllText(f, Vdf.Serialize(root), new System.Text.UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) { return "Não consegui gravar: " + ex.Message; }
    }

    /// <summary>Pede à Steam para fechar (steam -shutdown, nativa ou Flatpak) e espera até 25 s.</summary>
    public static async Task<bool> ShutdownSteamAsync()
    {
        bool flatpak = SteamRoot()?.Contains("/.var/app/com.valvesoftware.Steam/") == true;
        try
        {
            if (flatpak) Process.Start(Sys.HostProcess("flatpak", new[] { "run", "com.valvesoftware.Steam", "-shutdown" }));
            else Process.Start(Sys.HostProcess("steam", new[] { "-shutdown" }));
        }
        catch { }
        for (int i = 0; i < 50 && SteamRunning; i++) await Task.Delay(500);
        return !SteamRunning;
    }

    // ---------------- perfil → ambiente e prefixos ----------------

    /// <summary>Notebook com duas placas (integrada + dedicada): o PRIME offload faz o jogo usar a dedicada.</summary>
    private static bool Hybrid => LinuxGpus.All().Count >= 2;

    /// <summary>Variáveis de ambiente do PRIME offload: NVIDIA proprietário ou Mesa (DRI_PRIME).</summary>
    private static Dictionary<string, string> PrimeEnv()
    {
        var env = new Dictionary<string, string>();
        if (!Hybrid) return env;
        if (LinuxGpus.All().Any(g => g.Driver == "nvidia"))
        {
            env["__NV_PRIME_RENDER_OFFLOAD"] = "1";
            env["__GLX_VENDOR_LIBRARY_NAME"] = "nvidia";
            env["__VK_LAYER_NV_optimus"] = "NVIDIA_only";
        }
        else env["DRI_PRIME"] = "1";
        return env;
    }

    private static bool UseGameMode(GameProfile p) => (p.Priority != "normal" || p.HighPerfPlan) && Sys.HasCommand("gamemoderun");

    /// <summary>
    /// Opções de inicialização para a Steam no Linux: prefixos (gamemoderun, PRIME) antes do %command% e os argumentos depois.
    /// Sem prefixo, ficam só os argumentos (a Steam os acrescenta ao comando do jogo).
    /// </summary>
    public static string BuildSteamOptions(GameProfile p)
    {
        var prefix = new List<string>();
        if (p.GpuHigh) prefix.AddRange(PrimeEnv().Select(kv => $"{kv.Key}={kv.Value}"));
        if (UseGameMode(p)) prefix.Add("gamemoderun");
        string args = GameArgs.Build(p);
        return prefix.Count == 0 ? args : $"{string.Join(' ', prefix)} %command% {args}".Trim();
    }

    // ---------------- executável ----------------

    private static readonly string[] IgnoredExe =
        { "unins", "crash", "redist", "vc_", "vcredist", "dxsetup", "dotnet", "easyanticheat", "beservice", "cef", "setup", "uninstall",
          "launcher_helper", "notification", "steam_api", "ue4prereq" };

    /// <summary>Procura o executável principal: binário do Linux (.x86_64, .sh, sem extensão e executável) ou .exe (Proton/Wine).</summary>
    public static string? FindMainExe(string dir, string gameName)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var files = new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .Where(f => !IgnoredExe.Any(i => f.Name.Contains(i, StringComparison.OrdinalIgnoreCase)))
                .Where(f => f.Extension is ".x86_64" or ".x86" or ".sh" or ".exe" or ".AppImage" ||
                            f.Extension.Length == 0 && (f.UnixFileMode & UnixFileMode.UserExecute) != 0 && f.Length > 100_000)
                .ToList();
            if (files.Count == 0) return null;
            string norm = Regex.Replace(gameName.ToLowerInvariant(), "[^a-z0-9]", "");
            bool Native(FileInfo f) => f.Extension != ".exe";
            var match = files.Where(f => { string n = Regex.Replace(Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant(), "[^a-z0-9]", ""); return n.Length >= 3 && (norm.Contains(n) || n.Contains(norm)); })
                             .OrderByDescending(Native).ThenByDescending(f => f.Length).FirstOrDefault();
            return (match ?? files.OrderByDescending(Native).ThenByDescending(f => f.Length).First()).FullName;
        }
        catch { return null; }
    }

    // ---------------- iniciar e acompanhar ----------------

    /// <summary>PID do processo do jogo: o que tem a pasta do jogo na linha de comando (cobre os jogos pelo Proton/Wine).</summary>
    private static int? FindGameProcess(string dir, string? exe)
    {
        if (string.IsNullOrEmpty(dir) && exe == null) return null;
        string key = exe ?? dir;
        if (Sys.InFlatpak)
        {
            foreach (var line in (Sys.Run("ps", "-u " + Environment.UserName + " -o pid=,args=") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Trim().Split(' ', 2);
                if (f.Length == 2 && f[1].Contains(key) && !f[1].Contains("ABExplode") && int.TryParse(f[0], out int pid)) return pid;
            }
            return null;
        }
        int self = Environment.ProcessId;
        foreach (var d in Sys.Dirs("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(d), out int pid) || pid == self) continue;
            string cmd;
            try { cmd = File.ReadAllText(Path.Combine(d, "cmdline")).Replace('\0', ' '); } catch { continue; }
            if (cmd.Contains(key) && !cmd.Contains("ABExplode")) return pid;
        }
        return null;
    }

    private static bool Alive(int pid) => Sys.InFlatpak ? Sys.Run("kill", $"-0 {pid}") != null : Directory.Exists($"/proc/{pid}");

    private static void OpenUri(string uri) => Process.Start(Sys.HostProcess("xdg-open", new[] { uri }));

    /// <summary>
    /// Aplica o perfil, abre o jogo e acompanha até ele fechar; ao terminar (ou cancelar) volta o perfil de energia e fecha os
    /// programas auxiliares marcados. <paramref name="learnedExe"/> é chamado quando o executável real é descoberto.
    /// </summary>
    public static async Task RunAsync(LibGame g, GameProfile p, Action<string> status, Action<string> learnedExe, CancellationToken ct)
    {
        string? oldProfile = null;
        var helpers = new List<(Process Proc, bool Close)>();
        try
        {
            status("Aplicando o perfil…");
            if (p.HighPerfPlan && Sys.HasCommand("powerprofilesctl"))
            {
                oldProfile = Sys.Run("powerprofilesctl", "get")?.Trim();
                if (oldProfile == "performance") oldProfile = null; // já estava: nada a restaurar
                else Sys.Run("powerprofilesctl", "set performance");
            }

            foreach (var h in p.Helpers.Where(h => File.Exists(h.Path)))
            {
                status($"Abrindo {Path.GetFileName(h.Path)}…");
                try
                {
                    var psi = new ProcessStartInfo(h.Path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(h.Path) ?? "" };
                    foreach (var a in h.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
                    if (Process.Start(psi) is Process proc) helpers.Add((proc, h.CloseWithGame));
                }
                catch { }
            }
            if (helpers.Count > 0) await Task.Delay(1500, ct);

            status($"Iniciando {g.Name}…");
            string? exe = p.ExePath ?? g.ExePath;
            if (g.IsSteam) OpenUri($"steam://rungameid/{g.Id}");
            else if (g.LaunchUri != null) OpenUri(g.LaunchUri);
            else
            {
                exe ??= FindMainExe(g.InstallDir, g.Name);
                if (exe == null) throw new InvalidOperationException("Não achei o executável deste jogo. Escolha o executável na seção Desempenho.");
                bool windowsExe = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (windowsExe && !Sys.HasCommand("wine"))
                    throw new InvalidOperationException("É um jogo de Windows (.exe) e o Wine não está instalado. O jeito mais fácil é adicionar na Steam " +
                                                        "como jogo não-Steam e ativar o Proton nas propriedades.");
                var env = p.GpuHigh ? PrimeEnv() : new Dictionary<string, string>();
                // [gamemoderun] [wine] jogo argumentos...
                var cmd = new List<string>();
                if (UseGameMode(p)) cmd.Add("gamemoderun");
                if (windowsExe) cmd.Add("wine");
                cmd.Add(exe);
                cmd.AddRange((g.Args + " " + GameArgs.Build(p)).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var psi = Sys.HostProcess(cmd[0], cmd.Skip(1), env);
                psi.RedirectStandardOutput = psi.RedirectStandardError = false; // o jogo escreve muito; não precisamos da saída
                psi.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
                Process.Start(psi)?.Dispose();
            }

            // Espera o processo do jogo aparecer (até 3 min) e acompanha até fechar
            int? pid = null;
            for (int i = 0; i < 120 && pid == null; i++)
            {
                await Task.Delay(1500, ct);
                pid = FindGameProcess(g.InstallDir, p.ExePath ?? g.ExePath);
            }
            if (pid == null) { status("Não detectei o processo do jogo (ele pode ter demorado a abrir). Os ajustes continuam aplicados."); return; }
            if (!Sys.InFlatpak)
                try { if (new FileInfo($"/proc/{pid}/exe").LinkTarget is string real && !real.Contains("wine") && !real.Contains("proton")) learnedExe(real); } catch { }

            while (pid != null)
            {
                status($"{g.Name} em execução" + (UseGameMode(p) ? " · GameMode ativo" : "") + ".");
                while (Alive(pid.Value)) await Task.Delay(2000, ct);
                pid = null;
                // alguns jogos fecham um launcher e abrem o jogo de verdade: espera mais um pouco
                for (int i = 0; i < 6 && pid == null; i++)
                {
                    await Task.Delay(1000, ct);
                    pid = FindGameProcess(g.InstallDir, p.ExePath ?? g.ExePath);
                }
            }
            status($"{g.Name} encerrado. Ajustes temporários desfeitos.");
        }
        catch (OperationCanceledException) { status("Acompanhamento cancelado."); }
        catch (Exception ex) { status("Falhou: " + ex.Message); }
        finally
        {
            if (oldProfile != null) Sys.Run("powerprofilesctl", $"set {oldProfile}");
            foreach (var (proc, close) in helpers)
            {
                if (!close) continue;
                try { if (!proc.HasExited) { Sys.Run("kill", $"-TERM {proc.Id}"); if (!proc.WaitForExit(1500)) proc.Kill(); } } catch { }
            }
        }
    }
}

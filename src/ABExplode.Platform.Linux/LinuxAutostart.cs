namespace ABExplode.Platform.Linux;

/// <summary>Programa que abre junto com a sessão. Mesmas propriedades do modelo do Windows.</summary>
public sealed class LinuxStartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Source { get; init; } = "";
    public bool Enabled { get; set; }
    public string Detail { get; init; } = "";
    public bool FileMissing { get; init; }
    /// <summary>Nunca precisa de root: o que é do sistema é desligado por um arquivo do usuário com o mesmo nome.</summary>
    public bool Machine => false;
    public string State => FileMissing ? "PROGRAMA NÃO EXISTE" : Enabled ? "ATIVADO" : "DESATIVADO";

    internal string Kind { get; init; } = "";      // user | system | systemd
    internal string Id { get; init; } = "";        // nome do .desktop ou da unidade do systemd
    internal string? FilePath { get; init; }
}

/// <summary>
/// Inicialização no Linux: o padrão XDG (~/.config/autostart e /etc/xdg/autostart), que GNOME, KDE, XFCE e Cinnamon
/// seguem, e os serviços do usuário no systemd. Desativar usa Hidden=true no arquivo do usuário, como as próprias
/// configurações do sistema fazem; para um item do sistema, cria a cópia do usuário que o esconde. Nada pede senha.
/// </summary>
public static class LinuxAutostart
{
    // No Flatpak o XDG_CONFIG_HOME aponta para a pasta do sandbox; a inicialização da sessão é a do ~/.config de verdade.
    private static string UserDir => Path.Combine(
        !Sys.InFlatpak && Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart");
    // No Flatpak, o /etc do sistema fica em /run/host/etc (permissão host-etc)
    private static readonly string SystemDir = Sys.InFlatpak ? "/run/host/etc/xdg/autostart" : "/etc/xdg/autostart";
    public static string BackupFolder => Path.Combine(AppSettings.Folder, "Startup");

    private static Dictionary<string, string> Entry(string file)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inEntry = false;
        foreach (var line in Sys.Lines(file))
        {
            if (line.StartsWith('[')) { if (inEntry) break; inEntry = line.Trim() == "[Desktop Entry]"; continue; }
            int i = line.IndexOf('=');
            if (inEntry && i > 0) d.TryAdd(line[..i].Trim(), line[(i + 1)..].Trim());
        }
        return d;
    }

    private static bool IsEnabled(Dictionary<string, string> e) =>
        !e.GetValueOrDefault("Hidden", "").Equals("true", StringComparison.OrdinalIgnoreCase) &&
        !e.GetValueOrDefault("X-GNOME-Autostart-enabled", "").Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>OnlyShowIn/NotShowIn: itens de outro ambiente gráfico (ex.: só KDE) não rodam aqui e não aparecem.</summary>
    private static bool ForThisDesktop(Dictionary<string, string> e)
    {
        var current = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (current.Length == 0) return true;
        if (e.TryGetValue("OnlyShowIn", out var only) && !only.Split(';', StringSplitOptions.RemoveEmptyEntries).Intersect(current, StringComparer.OrdinalIgnoreCase).Any()) return false;
        if (e.TryGetValue("NotShowIn", out var not) && not.Split(';', StringSplitOptions.RemoveEmptyEntries).Intersect(current, StringComparer.OrdinalIgnoreCase).Any()) return false;
        return true;
    }

    private static bool ProgramExists(Dictionary<string, string> e)
    {
        string? exe = e.GetValueOrDefault("TryExec") ?? e.GetValueOrDefault("Exec")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => !p.Contains('='));
        if (string.IsNullOrEmpty(exe)) return true;
        exe = exe.Trim('"');
        return exe.StartsWith('/') ? File.Exists(exe) : Sys.HasCommand(exe);
    }

    public static List<LinuxStartupEntry> List()
    {
        var list = new List<LinuxStartupEntry>();
        var userFiles = SafeFiles(UserDir).ToDictionary(f => Path.GetFileName(f), f => f);
        // Do sistema: só os que o usuário não sobrescreveu (a cópia do usuário vale no lugar)
        foreach (var f in SafeFiles(SystemDir))
        {
            string id = Path.GetFileName(f);
            var e = Entry(f);
            if (!ForThisDesktop(e)) continue;
            bool overridden = userFiles.TryGetValue(id, out var uf);
            var effective = overridden ? Entry(uf!) : e;
            list.Add(Make(id, overridden ? uf! : f, "system", effective, "Do sistema (todos os usuários)"));
        }
        foreach (var (id, f) in userFiles)
        {
            if (File.Exists(Path.Combine(SystemDir, id))) continue; // já listado acima
            var e = Entry(f);
            if (!ForThisDesktop(e)) continue;
            list.Add(Make(id, f, "user", e, "Pasta de inicialização do usuário"));
        }

        // Serviços do usuário no systemd que alguém ativou (os do próprio sistema, como pipewire, ficam de fora)
        if (Sys.Run("systemctl", "--user list-unit-files --type=service --state=enabled --no-legend --no-pager") is string units)
        {
            string unitDir = Path.Combine(Path.GetDirectoryName(UserDir)!, "systemd", "user");
            foreach (var line in units.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string unit = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (!File.Exists(Path.Combine(unitDir, unit))) continue; // só os instalados pelo usuário (~/.config/systemd/user)
                string desc = Sys.Lines(Path.Combine(unitDir, unit)).FirstOrDefault(l => l.StartsWith("Description="))?[12..] ?? "";
                string exec = Sys.Lines(Path.Combine(unitDir, unit)).FirstOrDefault(l => l.StartsWith("ExecStart="))?[10..] ?? "";
                list.Add(new LinuxStartupEntry
                {
                    Name = unit.Replace(".service", ""), Command = exec, Source = "Serviço do usuário (systemd)", Enabled = true,
                    Detail = desc, Kind = "systemd", Id = unit, FilePath = Path.Combine(unitDir, unit),
                });
            }
        }
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static LinuxStartupEntry Make(string id, string file, string kind, Dictionary<string, string> e, string source) => new()
    {
        Name = e.GetValueOrDefault("Name") ?? Path.GetFileNameWithoutExtension(id), Command = e.GetValueOrDefault("Exec") ?? "",
        Source = source, Enabled = IsEnabled(e), Detail = e.GetValueOrDefault("Comment") ?? "", FileMissing = !ProgramExists(e),
        Kind = kind, Id = id, FilePath = file,
    };

    private static string[] SafeFiles(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.desktop") : Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }

    /// <summary>Grava Hidden e X-GNOME-Autostart-enabled na seção [Desktop Entry] (sem mexer no resto do arquivo).</summary>
    private static void SetHidden(string file, bool hidden)
    {
        var lines = File.ReadAllLines(file).ToList();
        int start = lines.FindIndex(l => l.Trim() == "[Desktop Entry]");
        if (start < 0) { lines.Insert(0, "[Desktop Entry]"); start = 0; }
        int end = lines.FindIndex(start + 1, l => l.StartsWith('['));
        if (end < 0) end = lines.Count;
        void Put(string key, string value)
        {
            int i = lines.FindIndex(start + 1, end - start - 1, l => l.StartsWith(key + "="));
            if (i >= 0) lines[i] = $"{key}={value}";
            else { lines.Insert(end, $"{key}={value}"); end++; }
        }
        Put("Hidden", hidden ? "true" : "false");
        Put("X-GNOME-Autostart-enabled", hidden ? "false" : "true");
        File.WriteAllLines(file, lines);
    }

    public static void SetEnabled(LinuxStartupEntry e, bool on)
    {
        switch (e.Kind)
        {
            case "systemd":
                if (Sys.Run("systemctl", $"--user {(on ? "enable" : "disable")} {e.Id}") == null)
                    throw new InvalidOperationException("o systemctl recusou");
                break;
            default:
                string user = Path.Combine(UserDir, e.Id);
                if (!File.Exists(user))
                {
                    // Item do sistema: a cópia do usuário é que liga/desliga
                    Directory.CreateDirectory(UserDir);
                    File.Copy(Path.Combine(SystemDir, e.Id), user);
                }
                SetHidden(user, !on);
                break;
        }
    }

    /// <summary>
    /// Remove da inicialização, com cópia para "Restaurar removidos". Do sistema não dá para apagar sem root: fica
    /// desligado pela cópia do usuário (o mesmo efeito). Serviço do systemd: desativa.
    /// </summary>
    public static StartupBackup Remove(LinuxStartupEntry e)
    {
        if (e.Kind == "systemd")
        {
            SetEnabled(e, false);
            return new StartupBackup { Kind = "systemd", Name = e.Name, Command = e.Id };
        }
        if (e.Kind == "system")
        {
            SetEnabled(e, false);
            return new StartupBackup { Kind = "system", Name = e.Name, Command = e.Id };
        }
        Directory.CreateDirectory(BackupFolder);
        string backup = Path.Combine(BackupFolder, e.Id);
        File.Copy(e.FilePath!, backup, overwrite: true);
        File.Delete(e.FilePath!);
        return new StartupBackup { Kind = "user", Name = e.Name, Backup = backup, Original = e.FilePath };
    }

    public static void Restore(StartupBackup b)
    {
        switch (b.Kind)
        {
            case "systemd":
                Sys.Run("systemctl", $"--user enable {b.Command}");
                break;
            case "system":
                // Apagar a cópia do usuário volta ao comportamento do sistema
                string user = Path.Combine(UserDir, b.Command ?? "");
                if (File.Exists(user)) File.Delete(user);
                break;
            default:
                if (b.Backup == null || b.Original == null || !File.Exists(b.Backup)) throw new FileNotFoundException("cópia não encontrada");
                Directory.CreateDirectory(Path.GetDirectoryName(b.Original)!);
                File.Move(b.Backup, b.Original, overwrite: true);
                break;
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ABExplode.Platform.Linux;

/// <summary>Leitura de /proc e /sys e execução de comandos do sistema, sempre tolerando arquivo ou programa ausente.</summary>
internal static class Sys
{
    /// <summary>Conteúdo do arquivo sem espaços nas pontas; null se não existir ou não der para ler (permissão).</summary>
    public static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    public static long? ReadLong(string path) => long.TryParse(Read(path), out long v) ? v : null;

    public static string[] Lines(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Arquivos "chave: valor" (/proc/cpuinfo, /proc/meminfo); só o primeiro bloco quando há vários.</summary>
    public static Dictionary<string, string> KeyValues(string path, char sep = ':')
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(path))
        {
            if (line.Length == 0) { if (d.Count > 0) break; continue; }
            int i = line.IndexOf(sep);
            if (i <= 0) continue;
            d.TryAdd(line[..i].Trim(), line[(i + 1)..].Trim());
        }
        return d;
    }

    /// <summary>/etc/os-release e afins: CHAVE="valor".</summary>
    public static Dictionary<string, string> EnvFile(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(path))
        {
            int i = line.IndexOf('=');
            if (i <= 0 || line.StartsWith('#')) continue;
            d[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"', '\'');
        }
        return d;
    }

    /// <summary>
    /// Rodando dentro do Flatpak. Lá as ferramentas do sistema (lspci, smartctl, journalctl, pkexec...) e as montagens
    /// do computador ficam fora do sandbox: os comandos vão pelo flatpak-spawn --host (permissão org.freedesktop.Flatpak).
    /// </summary>
    public static bool InFlatpak { get; } = File.Exists("/.flatpak-info");

    /// <summary>Processo para rodar um comando no sistema (no Flatpak, fora do sandbox).</summary>
    public static ProcessStartInfo HostProcess(string file, IEnumerable<string> args, IDictionary<string, string>? env = null)
    {
        ProcessStartInfo psi;
        if (InFlatpak)
        {
            psi = new ProcessStartInfo("flatpak-spawn");
            psi.ArgumentList.Add("--host");
            foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.ArgumentList.Add($"--env={k}={v}");
            psi.ArgumentList.Add(file);
        }
        else
        {
            psi = new ProcessStartInfo(file);
            foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return psi;
    }

    private static readonly Dictionary<string, string> CLocale = new() { ["LC_ALL"] = "C" }; // saída em inglês, que é o que os parsers esperam

    /// <summary>Roda um programa e devolve a saída padrão; null se ele não existir, falhar ou passar do tempo.</summary>
    public static string? Run(string file, string args, int timeoutMs = 5000, bool acceptNonZero = false) =>
        Run(file, args.Split(' ', StringSplitOptions.RemoveEmptyEntries), timeoutMs, acceptNonZero); // argumentos sem espaços dentro

    public static string? Run(string file, IEnumerable<string> args, int timeoutMs = 5000, bool acceptNonZero = false)
    {
        try
        {
            using var p = Process.Start(HostProcess(file, args, CLocale));
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            // flatpak-spawn devolve 127 quando o programa não existe no sistema
            if (InFlatpak && p.ExitCode == 127) return null;
            return p.ExitCode == 0 || acceptNonZero ? stdout.Result : null;
        }
        catch { return null; }
    }

    private static readonly Dictionary<string, bool> CommandCache = new();

    /// <summary>O programa existe em algum diretório do PATH (no Flatpak, no PATH do sistema).</summary>
    public static bool HasCommand(string name)
    {
        if (!InFlatpak)
            return (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':')
                .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, name)));
        lock (CommandCache)
        {
            if (CommandCache.TryGetValue(name, out bool cached)) return cached;
            bool found = Run("sh", new[] { "-c", "command -v " + name }) != null;
            return CommandCache[name] = found;
        }
    }

    /// <summary>Esquece o que já foi procurado (depois de instalar dependências).</summary>
    public static void ForgetCommands()
    {
        lock (CommandCache) CommandCache.Clear();
    }

    /// <summary>Linhas de /proc/self/mounts do sistema (no Flatpak, as do sandbox não servem).</summary>
    public static string[] MountLines() =>
        InFlatpak ? Run("cat", "/proc/self/mounts")?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>()
                  : Lines("/proc/self/mounts");

    public static bool IsRoot => Environment.IsPrivilegedProcess;

    /// <summary>Nome do link simbólico (ex.: device/driver → "amdgpu").</summary>
    public static string? LinkName(string path)
    {
        try
        {
            var target = new FileInfo(path).LinkTarget;
            return target != null ? Path.GetFileName(target) : null;
        }
        catch { return null; }
    }

    [DllImport("libc", SetLastError = true)] private static extern IntPtr realpath(string path, IntPtr resolved);
    [DllImport("libc")] private static extern void free(IntPtr p);

    /// <summary>
    /// Caminho real, pelo realpath(3). Não dá para resolver como texto: em /sys/class/drm/card0/device o "card0" também
    /// é link, então os ".." do alvo de "device" valem a partir de /sys/devices/..., não de /sys/class/drm.
    /// </summary>
    public static string RealPath(string path)
    {
        try
        {
            IntPtr p = realpath(path, IntPtr.Zero);
            if (p == IntPtr.Zero) return path;
            try { return Marshal.PtrToStringUTF8(p) ?? path; }
            finally { free(p); }
        }
        catch { return path; }
    }

    public static IEnumerable<string> Dirs(string path, string pattern = "*")
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path, pattern).OrderBy(x => x, StringComparer.Ordinal) : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }
    }

    public static string Bytes(double b, int decimals = 2)
    {
        if (b <= 0) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return b.ToString(decimals == 1 ? "0.#" : "0.##") + " " + u[i];
    }

    public static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

    /// <summary>Valores de DMI que os fabricantes deixam sem preencher.</summary>
    public static string? Dmi(string name)
    {
        string? v = Read("/sys/class/dmi/id/" + name);
        if (string.IsNullOrWhiteSpace(v)) return null;
        string l = v.ToLowerInvariant();
        return l is "to be filled by o.e.m." or "default string" or "not applicable" or "none" or "system product name" or "0" or "o.e.m."
            ? null : v;
    }

    [DllImport("libc", SetLastError = true)] private static extern int access(string path, int mode);

    /// <summary>Este usuário pode gravar no diretório.</summary>
    public static bool CanWrite(string dir)
    {
        try { return access(dir, 2) == 0; } // W_OK
        catch { return false; }
    }
}

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

    /// <summary>Roda um programa e devolve a saída padrão; null se ele não existir, falhar ou passar do tempo.</summary>
    public static string? Run(string file, string args, int timeoutMs = 5000, bool acceptNonZero = false)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                Environment = { ["LC_ALL"] = "C" }, // saída em inglês, que é o que os parsers esperam
            });
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 || acceptNonZero ? stdout.Result : null;
        }
        catch { return null; }
    }

    /// <summary>O programa existe em algum diretório do PATH.</summary>
    public static bool HasCommand(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':')
            .Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, name)));

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

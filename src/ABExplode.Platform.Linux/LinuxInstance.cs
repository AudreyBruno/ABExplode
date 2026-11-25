using System.Net.Sockets;
using System.Text;

namespace ABExplode.Platform.Linux;

/// <summary>
/// Instância única no Linux por um socket Unix em $XDG_RUNTIME_DIR (no Flatpak, na pasta do app, que as instâncias dividem).
/// Abrir o app de novo manda "show" para a janela aberta; "ABExplode --toggle-overlay" manda "toggle-overlay", que é o que um
/// atalho de teclado do sistema chama (no Wayland o app não pode capturar teclas globais).
/// </summary>
public sealed class LinuxInstance : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();

    private static string SocketPath
    {
        get
        {
            string run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } x ? x : Path.GetTempPath();
            if (Sys.InFlatpak && Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 } id) run = Path.Combine(run, "app", id);
            return Path.Combine(run, $"abexplode-{Environment.UserName}.sock");
        }
    }

    /// <summary>Manda o comando para a instância aberta. true = havia uma e ela recebeu.</summary>
    public static bool Send(string command)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            s.Send(Encoding.UTF8.GetBytes(command + "\n"));
            return true;
        }
        catch { return false; }
    }

    private LinuxInstance(Socket listener) => _listener = listener;

    /// <summary>Passa a receber os comandos das próximas aberturas (no thread do socket; quem recebe repassa para a janela).</summary>
    public static LinuxInstance? Listen(Action<string> onCommand)
    {
        try
        {
            string path = SocketPath;
            if (File.Exists(path)) File.Delete(path); // sobra de uma instância que fechou sem apagar
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Bind(new UnixDomainSocketEndPoint(path));
            s.Listen(4);
            var inst = new LinuxInstance(s);
            _ = Task.Run(async () =>
            {
                var buf = new byte[256];
                while (!inst._cts.IsCancellationRequested)
                {
                    try
                    {
                        using var c = await s.AcceptAsync(inst._cts.Token);
                        int n = await c.ReceiveAsync(buf, SocketFlags.None, inst._cts.Token);
                        foreach (var cmd in Encoding.UTF8.GetString(buf, 0, n).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            onCommand(cmd);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            });
            return inst;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Dispose(); } catch { }
        try { File.Delete(SocketPath); } catch { }
    }

    // ------------------------------------------------------------------ atalho de teclado do sistema

    /// <summary>Comando que liga e desliga o overlay da instância aberta (Flatpak, AppImage ou executável).</summary>
    public static string ToggleCommand()
    {
        if (Sys.InFlatpak && Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 } id) return $"flatpak run {id} --toggle-overlay";
        string exe = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } ai ? ai : Environment.ProcessPath ?? "ABExplode";
        return $"\"{exe}\" --toggle-overlay";
    }

    private static bool IsGnome =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':').Any(d => d is "GNOME" or "Unity" or "Budgie")
        && Sys.HasCommand("gsettings");

    /// <summary>Ambientes em que o app consegue criar o atalho sozinho (GNOME e derivados, pelo gsettings).</summary>
    public static bool CanCreateShortcut => IsGnome;

    /// <summary>
    /// Cria (ou atualiza) o atalho Ctrl+Shift+O nas configurações de teclado do GNOME, chamando o --toggle-overlay.
    /// Devolve null se deu certo, ou o erro.
    /// </summary>
    public static string? CreateGnomeShortcut()
    {
        const string schema = "org.gnome.settings-daemon.plugins.media-keys";
        const string path = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/abexplode/";
        string list = Sys.Run("gsettings", $"get {schema} custom-keybindings")?.Trim() ?? "@as []";
        if (!list.Contains(path))
        {
            string items = list.Replace("@as", "").Trim().TrimStart('[').TrimEnd(']').Trim();
            string updated = "[" + (items.Length > 0 ? items + ", " : "") + $"'{path}']";
            if (Sys.Run("gsettings", new[] { "set", schema, "custom-keybindings", updated }) == null) return "o gsettings recusou a lista de atalhos";
        }
        string kb = $"{schema}.custom-keybinding:{path}";
        bool ok = Sys.Run("gsettings", new[] { "set", kb, "name", "ABExplode: overlay" }) != null
                & Sys.Run("gsettings", new[] { "set", kb, "command", ToggleCommand() }) != null
                & Sys.Run("gsettings", new[] { "set", kb, "binding", "<Control><Shift>o" }) != null;
        return ok ? null : "o gsettings recusou o atalho";
    }
}

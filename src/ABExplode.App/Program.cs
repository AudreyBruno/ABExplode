using Avalonia;

namespace ABExplode.App;

internal static class Program
{
#if WINDOWS
    // Uma única instância: abrir o ABExplode de novo só traz a janela que já está aberta para a frente.
    private static Mutex? _single;
#endif

    [STAThread]
    public static void Main(string[] args)
    {
        // Relatório de tudo que a plataforma lê, sem abrir janela (ABExplode --diag > diag.txt)
        if (args.Contains("--diag")) { Environment.ExitCode = Diagnostics.Run(App.CreatePlatform()); return; }
#if WINDOWS
        if (!AcquireSingleInstance()) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
#else
        // Ferramentas que completam as leituras: ABExplode --deps confere e instala
        if (args.Contains("--deps")) { Environment.ExitCode = LinuxStartup.CheckTools(); return; }
        // Sem sessão gráfica ou sem as bibliotecas da janela, explica e oferece instalar em vez de fechar com erro
        if (!LinuxStartup.CheckGui()) { Environment.ExitCode = 1; return; }
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or InvalidOperationException)
        {
            Console.Error.WriteLine($"A janela do ABExplode não abriu: {ex.Message}\n" +
                                    "Rode \"ABExplode --deps\" para conferir as dependências, ou \"ABExplode --diag\" para ver as leituras sem janela.");
            Environment.ExitCode = 1;
        }
#endif
    }

    // Usado também pelo designer do Avalonia na IDE
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

#if WINDOWS
    private static bool AcquireSingleInstance()
    {
        bool first;
        try { _single = new Mutex(true, @"Local\ABExplode.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { first = false; } // criado por outra instância com outro nível de permissão
        if (first) return true;

        IntPtr h = FindWindow(null, "ABExplode");
        if (h != IntPtr.Zero)
        {
            if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
            SetForegroundWindow(h);
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
#endif
}

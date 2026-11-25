#if !WINDOWS
using System.Diagnostics;
using ABExplode.Platform.Linux;

namespace ABExplode.App;

/// <summary>
/// Linux, antes de abrir a janela: confere se há ambiente gráfico e as bibliotecas da interface. Se faltar, explica como
/// instalar e oferece instalar (no terminal pergunta ali mesmo; aberto pelo gerenciador de arquivos, pelo zenity/kdialog).
/// Também atende "ABExplode --deps", que faz o mesmo com as ferramentas que completam as leituras.
/// </summary>
internal static class LinuxStartup
{
    /// <summary>true = pode abrir a janela.</summary>
    public static bool CheckGui()
    {
        if (!LinuxDependencies.HasDisplay)
        {
            Tell("O ABExplode precisa de uma sessão gráfica (X11 ou Wayland) para abrir a janela.\n" +
                 "Rode dentro da área de trabalho, ou use \"ABExplode --diag\" para ver as leituras no terminal.");
            return false;
        }
        var missing = LinuxDependencies.MissingGui();
        if (missing.Count == 0) return true;
        return Offer("Faltam bibliotecas gráficas para abrir a janela do ABExplode", missing) && LinuxDependencies.MissingGui().Count == 0;
    }

    /// <summary>ABExplode --deps: ferramentas e módulo de sensores (o app abre sem eles, mas lê menos).</summary>
    public static int CheckTools()
    {
        var missing = LinuxDependencies.MissingTools();
        if (missing.Count == 0) { Tell("Tudo instalado: o ABExplode consegue ler todos os dados deste sistema."); return 0; }
        return Offer("Para o ABExplode ler tudo neste sistema, faltam", missing) ? 0 : 1;
    }

    /// <summary>Mostra o que falta e, se der e o usuário aceitar, instala. true = instalou (ou já não faltava nada).</summary>
    private static bool Offer(string title, IReadOnlyList<LinuxDependency> missing)
    {
        var pm = LinuxDependencies.Detect();
        string list = string.Join("\n", missing.Select(d => $"  • {d.Title}: {d.Why}"));
        string how = LinuxDependencies.Instructions(missing, pm);
        bool can = LinuxDependencies.CanInstall(pm);
        string text = $"{title}:\n\n{list}\n\nPara instalar pelo terminal:\n{how}";
        if (pm?.Immutable == true) text += "\n\nEsta distribuição é imutável: depois de instalar, reinicie o computador.";

        bool yes;
        if (!Console.IsInputRedirected)
        {
            Console.WriteLine(text);
            if (!can) return false;
            Console.Write("\nInstalar agora? [s/N] ");
            yes = Console.ReadLine()?.Trim().ToLowerInvariant() is "s" or "sim" or "y" or "yes";
        }
        else
        {
            if (!can) { Tell(text); return false; }
            yes = AskGraphical(text + "\n\nInstalar agora? O sistema vai pedir sua senha.");
        }
        if (!yes) return false;

        Console.WriteLine("Instalando… (o sistema vai pedir a senha de administrador)");
        var (ok, output) = LinuxDependencies.InstallAsync(missing, pm!).GetAwaiter().GetResult();
        Tell(ok ? "Instalado." : $"A instalação falhou:\n{output}\n\nTente pelo terminal:\n{how}");
        return ok;
    }

    /// <summary>Terminal se houver; senão zenity, kdialog ou, no mínimo, notify-send.</summary>
    private static void Tell(string text)
    {
        Console.Error.WriteLine(text);
        if (!Console.IsInputRedirected) return;
        if (Run("zenity", "--info", "--no-wrap", "--title=ABExplode", "--text=" + text) is not null) return;
        if (Run("kdialog", "--title", "ABExplode", "--msgbox", text) is not null) return;
        Run("notify-send", "ABExplode", text);
    }

    private static bool AskGraphical(string text) =>
        Run("zenity", "--question", "--no-wrap", "--title=ABExplode", "--ok-label=Instalar", "--cancel-label=Agora não", "--text=" + text) is 0
        || Run("kdialog", "--title", "ABExplode", "--yesno", text) is 0;

    /// <summary>Código de saída do programa; null se ele não existir.</summary>
    private static int? Run(string file, params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
            if (p == null) return null;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return null; }
    }
}
#endif

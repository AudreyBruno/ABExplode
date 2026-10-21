using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ABExplode.App;

/// <summary>Caixa de mensagem no visual do app (o Avalonia não tem MessageBox).</summary>
public static class Dialog
{
    /// <summary>Mensagem com OK.</summary>
    public static Task Show(Window owner, string title, string text) => Open(owner, title, text, "OK", null);

    /// <summary>Pergunta com SIM/NÃO; true se a resposta for SIM.</summary>
    public static Task<bool> Ask(Window owner, string title, string text) => Open(owner, title, text, "SIM", "NÃO");

    /// <summary>Pergunta com os rótulos dados (ex.: INSTALAR / AGORA NÃO); true se a resposta for a primeira.</summary>
    public static Task<bool> Ask(Window owner, string title, string text, string yes, string no) => Open(owner, title, text, yes, no);

    private static async Task<bool> Open(Window owner, string title, string text, string yes, string? no)
    {
        var win = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        win.Bind(Window.BackgroundProperty, win.GetResourceObservable("Card"));
        bool result = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        if (no != null)
        {
            var bNo = new Button { Content = no, Padding = new Thickness(20, 8) };
            bNo.Theme = (Avalonia.Styling.ControlTheme?)owner.FindResource("OutlineButton");
            bNo.Click += (_, _) => win.Close();
            buttons.Children.Add(bNo);
        }
        var bYes = new Button { Content = yes, Padding = new Thickness(20, 8), IsDefault = true };
        bYes.Theme = (Avalonia.Styling.ControlTheme?)owner.FindResource("PrimaryButton");
        bYes.Click += (_, _) => { result = true; win.Close(); };
        buttons.Children.Add(bYes);

        // Selecionável: dá para copiar comandos e mensagens de erro.
        var body = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        body.Bind(TextBlock.ForegroundProperty, win.GetResourceObservable("Fg"));
        win.Content = new StackPanel { Margin = new Thickness(24), Children = { body, buttons } };

        await win.ShowDialog(owner);
        return result;
    }
}

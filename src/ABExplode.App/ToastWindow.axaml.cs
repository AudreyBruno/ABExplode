using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ABExplode.App;

/// <summary>Aviso no canto inferior direito da tela (dispositivo conectado/removido). Não rouba o foco.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _afterFade = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ToastWindow()
    {
        InitializeComponent();
#if WINDOWS
        // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: fora do Alt+Tab e sem tirar o foco do jogo/programa em uso
        Win32Properties.AddWindowStylesCallback(this, (style, exStyle) => (style, exStyle | 0x80u | 0x8000000u));
#endif
        SizeChanged += (_, _) => Place();
        _hide.Tick += (_, _) => { _hide.Stop(); Opacity = 0; _afterFade.Start(); };
        _afterFade.Tick += (_, _) => { _afterFade.Stop(); if (Opacity == 0) Hide(); };
    }

    /// <summary>Mostra (ou atualiza, se já visível) o aviso por 5 s. icon: um dos Glyphs (Fluent UI System Icons).</summary>
    public void Notify(string title, string text, string icon)
    {
        TTitle.Text = title;
        TText.Text = text;
        TIcon.Text = icon;
        _afterFade.Stop();
        if (!IsVisible) { Show(); Place(); }
        Opacity = 1;
        _hide.Stop();
        _hide.Start();
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen) return;
        var wa = screen.WorkingArea; // em pixels
        double s = screen.Scaling;
        var size = new PixelSize((int)(Bounds.Width * s), (int)(Bounds.Height * s));
        int margin = (int)(16 * s);
        Position = new PixelPoint(wa.Right - size.Width - margin, wa.Bottom - size.Height - margin);
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MeuHwInfo;

/// <summary>
/// OSD sobre o jogo (estilo RTSS/Afterburner): janela transparente, sempre no topo, que ignora cliques
/// e nunca rouba o foco. Aparece sobre jogos em tela cheia "sem bordas" e na maioria dos DX11/12 em
/// tela cheia (que o Windows apresenta via flip); não aparece em tela cheia exclusiva legada.
/// </summary>
public partial class OverlayWindow : Window
{
    public string Corner { get; set; } = "TL"; // TL, TR, BL, BR

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        SizeChanged += (_, _) => Reposition();
    }

    public void SetScale(double s) { Scale.ScaleX = Scale.ScaleY = s; }

    /// <summary>Cola no canto escolhido do monitor principal (área total, para ficar sobre jogos em tela cheia).</summary>
    public void Reposition()
    {
        const double m = 12;
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Left = Corner.EndsWith("R") ? sw - ActualWidth - m : m;
        Top = Corner.StartsWith("B") ? sh - ActualHeight - m : m;
    }

    public void Update(FrameStats? f, string gpu, string cpu, string ram)
    {
        if (f != null)
        {
            OFps.Text = $"{f.Fps:0}";
            OFrameTime.Text = $"{f.FrameTimeMs:0.0} ms";
            OLow.Text = $"1% low {f.Low1Fps:0}";
            OGame.Text = f.ProcessName;
        }
        else
        {
            OFps.Text = "—";
            OFrameTime.Text = "";
            OLow.Text = "aguardando jogo";
            OGame.Text = "";
        }
        OGpu.Text = gpu;
        OCpu.Text = cpu;
        ORam.Text = ram;
        DrawGraph(f?.RecentFrameTimes);
    }

    /// <summary>Frametimes recentes: picos para cima = engasgos.</summary>
    private void DrawGraph(IReadOnlyList<double>? ft)
    {
        OGraph.Children.Clear();
        double w = OGraph.Width, h = OGraph.Height;
        OGraph.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h - 0.5, Y2 = h - 0.5, Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) });
        if (ft == null || ft.Count < 2) return;

        double max = Math.Max(ft.Max(), ft.Average() * 2); // escala que mostra picos sem achatar o normal
        var line = new Polyline { Stroke = Brushes.White, StrokeThickness = 1 };
        double step = w / (ft.Count - 1);
        for (int i = 0; i < ft.Count; i++)
            line.Points.Add(new Point(i * step, h - 1 - (h - 2) * Math.Min(1, ft[i] / max)));
        OGraph.Children.Add(line);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
}

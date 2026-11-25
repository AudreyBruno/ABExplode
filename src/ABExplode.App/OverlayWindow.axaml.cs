using System.Runtime.InteropServices;
using ABExplode.Core;
using ABExplode.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace ABExplode.App;

/// <summary>
/// OSD sobre o jogo (estilo RTSS/Afterburner): janela transparente, sempre no topo, que ignora cliques
/// e nunca rouba o foco. No Windows aparece sobre jogos em tela cheia "sem bordas" e na maioria dos DX11/12 em
/// tela cheia (que o Windows apresenta via flip); não aparece em tela cheia exclusiva legada.
/// </summary>
public partial class OverlayWindow : Window
{
    public string Corner { get; set; } = "TL"; // TL, TR, BL, BR

    public OverlayWindow()
    {
        InitializeComponent();
#if WINDOWS
        // Ignora cliques (LAYERED + TRANSPARENT), fora do Alt+Tab (TOOLWINDOW) e sem tirar o foco do jogo (NOACTIVATE).
        Win32Properties.AddWindowStylesCallback(this, (style, ex) => (style, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        _onForeground = (_, _, hwnd, _, _, _, _) => { TrackGame(hwnd); KeepOnTop(); };
        Opened += (_, _) =>
        {
            _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            // Com WS_EX_LAYERED o Windows só desenha a janela depois de receber a opacidade dela.
            if (_hwnd != IntPtr.Zero) SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        };
        // Só escuta a troca de janela ativa enquanto o overlay está na tela.
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible && _hook == IntPtr.Zero)
            {
                _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
                TrackGame(GetForegroundWindow());
                KeepOnTop();
            }
            else if (!IsVisible) Unhook();
        };
        Closed += (_, _) => Unhook();
#endif
        SizeChanged += (_, _) => KeepOnTop();
    }

    public void SetScale(double s)
    {
        if (ScaleHost.LayoutTransform is ScaleTransform t) { t.ScaleX = t.ScaleY = s; }
        else ScaleHost.LayoutTransform = new ScaleTransform(s, s);
    }

    public void Reposition() => KeepOnTop();

    /// <summary>
    /// Cola no canto escolhido do monitor do jogo (área total, para ficar sobre jogos em tela cheia) e volta para o topo.
    /// Jogos em janela sem borda (GTA V, Cyberpunk 2077...) também se marcam como "sempre no topo" e, ao serem ativados,
    /// passam na frente; por isso isto roda a cada troca de janela ativa e a cada atualização do overlay.
    /// </summary>
    public void KeepOnTop()
    {
        if (!IsVisible) return;
        var r = GameScreenBounds();
        if (r is not { } area) return;
        double scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
        int w = (int)Math.Ceiling(Bounds.Width * scaling), h = (int)Math.Ceiling(Bounds.Height * scaling);
        int m = (int)Math.Round(12 * scaling);
        int x = Corner.EndsWith('R') ? area.Right - w - m : area.X + m;
        int y = Corner.StartsWith('B') ? area.Bottom - h - m : area.Y + m;
#if WINDOWS
        if (_hwnd != IntPtr.Zero)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            return;
        }
#endif
        Position = new PixelPoint(x, y);
    }

    /// <summary>Área total (em pixels) do monitor do jogo; fora do Windows, o monitor principal.</summary>
    private PixelRect? GameScreenBounds()
    {
#if WINDOWS
        if (_monitor == IntPtr.Zero) _monitor = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(_monitor, ref mi))
        {
            var rc = mi.rcMonitor;
            return new PixelRect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
        }
#endif
        return Screens.Primary?.Bounds;
    }

    /// <summary>Atualiza o overlay. <paramref name="show"/> = itens escolhidos (chaves de OverlayMetrics).</summary>
    public void Update(FrameStats? f, IReadOnlyList<OverlayLine> lines, IReadOnlySet<string> show, string? extra)
    {
        bool fps = show.Contains("fps"), ft = show.Contains("frametime");
        OFpsRow.IsVisible = fps || ft;
        OFps.IsVisible = OFpsUnit.IsVisible = fps;
        OFrameTime.IsVisible = ft;
        OFrameTime.Margin = new Thickness(fps ? 12 : 0, 0, 0, fps ? 4 : 0);
        OLow.IsVisible = show.Contains("low1");
        OGraph.IsVisible = show.Contains("graph");

        if (f != null)
        {
            OFps.Text = $"{f.Fps:0}";
            OFrameTime.Text = $"{f.FrameTimeMs:0.0} ms";
            OLow.Text = $"1% low {f.Low1Fps:0}";
        }
        else
        {
            OFps.Text = "—";
            OFrameTime.Text = fps ? "" : "— ms";
            OLow.Text = "aguardando jogo";
        }
        // Rodapé: nome do jogo e/ou hora.
        var foot = new List<string>();
        if (show.Contains("game") && f != null) foot.Add(f.ProcessName);
        if (extra != null) foot.Add(extra);
        OGame.Text = string.Join("  ·  ", foot);
        OGame.IsVisible = foot.Count > 0;

        ORows.ItemsSource = lines;
        ORows.IsVisible = lines.Count > 0;
        if (OGraph.IsVisible) DrawGraph(f?.RecentFrameTimes);
    }

    private static readonly IBrush GraphBase = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    /// <summary>Frametimes recentes: picos para cima = engasgos.</summary>
    private void DrawGraph(IReadOnlyList<double>? ft)
    {
        OGraph.Children.Clear();
        double w = OGraph.Width, h = OGraph.Height;
        OGraph.Children.Add(new Line { StartPoint = new Point(0, h - 0.5), EndPoint = new Point(w, h - 0.5), Stroke = GraphBase, StrokeThickness = 1 });
        if (ft == null || ft.Count < 2) return;

        double max = Math.Max(ft.Max(), ft.Average() * 2); // escala que mostra picos sem achatar o normal
        double step = w / (ft.Count - 1);
        var points = new List<Point>(ft.Count);
        for (int i = 0; i < ft.Count; i++)
            points.Add(new Point(i * step, h - 1 - (h - 2) * Math.Min(1, ft[i] / max)));
        OGraph.Children.Add(new Polyline { Points = points, Stroke = Brushes.White, StrokeThickness = 1 });
    }

#if WINDOWS
    private IntPtr _hwnd, _hook;
    private IntPtr _monitor;                       // monitor do jogo (o da última janela ativa de outro programa)
    private readonly WinEventProc _onForeground;  // guardado num campo para o GC não coletar o callback

    /// <summary>Segue o monitor da janela ativa, a não ser que seja do próprio ABExplode.</summary>
    private void TrackGame(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        _monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
    }

    private void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private const uint WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const uint LWA_ALPHA = 0x2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
#endif
}

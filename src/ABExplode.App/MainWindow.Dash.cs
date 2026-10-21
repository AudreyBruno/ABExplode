using ABExplode.Core;
using ABExplode.Platform;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
#if WINDOWS
using ABExplode.Platform.Windows;
#endif

namespace ABExplode.App;

public sealed record DashComponent(string Icon, string Name, string Model, string Temp, string Load, string Clock, string Power, string Target);
public sealed record DashEvent(string Icon, string Title, string Detail, string Value, DateTime At)
{
    public string When => At.Date == DateTime.Today ? $"hoje {At:HH:mm}" : $"{At:dd/MM HH:mm}";
}
public sealed record DashRingItem(IBrush Swatch, string Label, string Value);
public sealed record DashCheck(string Title, string Detail, bool Ok)
{
    public string Glyph => Ok ? "" : "";
}

/// <summary>
/// Visão geral: painel de blocos com gráfico de área (CPU × GPU), desempenho, tabela de componentes, anéis de
/// memória/disco, eventos recentes e lista de verificação da saúde do PC.
/// </summary>
public partial class MainWindow
{
    private readonly record struct DashSample(float? CpuT, float? GpuT, float? CpuL, float? GpuL, float? CpuW, float? GpuW);

    private const int DashMax = 900; // 15 min a 1 amostra/s
    private readonly List<DashSample> _dash = new();
    private readonly List<DashEvent> _events = new();
    private DateTime _healthAt = DateTime.MinValue;
    private string _cpuName = "";
#if WINDOWS
    private readonly List<Tweak> _tweaks = WindowsTweaks.All(); // só as definições; o estado é lido na hora
#endif

    // ---------------- leituras da GPU principal ----------------

    /// <summary>Temperatura do núcleo da GPU principal (dedicada).</summary>
    private float? MainGpuTemp()
    {
        if (_gpus.Count == 0) return null;
        var temps = _monitor.Rows.Where(r => r.Hardware == _gpus[0].Name && r.Type == SensorKind.Temperature).ToList();
        return (temps.FirstOrDefault(r => r.Name == "GPU Core") ?? temps.FirstOrDefault())?.RawValue;
    }

    private float? MainGpuLoad() => _gpus.Count == 0 ? null :
        _monitor.Rows.FirstOrDefault(r => r.Hardware == _gpus[0].Name && r.Type == SensorKind.Load && r.Name == "GPU Core")?.RawValue;

    /// <summary>Consumo da GPU principal: NVML quando disponível, senão o sensor da plataforma.</summary>
    private float? MainGpuPower()
    {
        if (_gpus.Count == 0) return null;
        var g = _gpus[0];
        if (g.NvmlHandle != IntPtr.Zero && Nvml.PowerUsageMw(g.NvmlHandle) is uint mw) return mw / 1000f;
        return _monitor.Rows.FirstOrDefault(r => r.Hardware == g.Name && r.Type == SensorKind.Power)?.RawValue;
    }

    private float? CpuClockAvg()
    {
        var c = _monitor.Rows.Where(r => r.HardwareType == HardwareKind.Cpu && r.Type == SensorKind.Clock &&
                                         r.Name.StartsWith("Core #") && r.RawValue > 0).Select(r => r.RawValue!.Value).ToList();
        return c.Count > 0 ? c.Average() : null;
    }

    // ---------------- coleta (a cada segundo, mesmo com a página fechada) ----------------

    private void CollectDashSample()
    {
        _dash.Add(new DashSample(_monitor.CpuTemperature, MainGpuTemp(), _monitor.CpuLoad, MainGpuLoad(), _monitor.CpuPower, MainGpuPower()));
        if (_dash.Count > DashMax) _dash.RemoveAt(0);
        if (PageDash.IsVisible) UpdateDashboard(full: false);
    }

    // ---------------- eventos recentes ----------------

    private void AddEvent(string icon, string title, string detail, string value)
    {
        _events.Insert(0, new DashEvent(icon, title, detail, value, DateTime.Now));
        if (_events.Count > 30) _events.RemoveAt(_events.Count - 1);
        if (PageDash.IsVisible) ShowEvents();
    }

    /// <summary>Eventos já registrados antes desta sessão (resultados de benchmark e verificações salvos).</summary>
    private IEnumerable<DashEvent> SavedEvents()
    {
        foreach (var r in _settings.BenchRuns)
        {
            if (r.Multi is double m) yield return new DashEvent("", "RESET BENCH", "Render · multi-core", $"{m:N0} pts", r.Date);
            else if (r.Single is double s) yield return new DashEvent("", "RESET BENCH", "Render · single-core", $"{s:N0} pts", r.Date);
        }
        foreach (var r in _settings.BenchRecords)
        {
            var (icon, title) = r.Test switch
            {
                "gpu" => ("", "Cena de jogo (GPU)"),
                "sim" => ("", "Simulação de jogo"),
                "mem" => ("", "Memória e carregamento"),
                "oc" => ("", "Verificação de estabilidade"),
                "disk" => ("", "Benchmark de disco"),
                _ => ("", r.Test)
            };
            string first = r.Summary.Split('·')[0].Trim();
            string rest = r.Summary.Contains('·') ? r.Summary[(r.Summary.IndexOf('·') + 1)..].Trim() : "";
            yield return new DashEvent(icon, title, rest, first, r.Date);
        }
    }

    private void ShowEvents()
    {
        var list = _events.Concat(SavedEvents()).OrderByDescending(e => e.At).Take(4).ToList();
        DashEvents.ItemsSource = list;
        DashEventsEmpty.IsVisible = list.Count == 0;
    }

    // ---------------- navegação a partir do painel ----------------

    private void DashOption_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true } && DashChart != null) DrawDashChart();
    }

    private void DashChart_SizeChanged(object? sender, SizeChangedEventArgs e) => DrawDashChart();
    private void DashBench_Click(object? sender, RoutedEventArgs e) => Navigate("PageBench");
    private void DashOptimize_Click(object? sender, RoutedEventArgs e) => Navigate("PageOptimize");
    private void DashGo_Click(object? sender, RoutedEventArgs e) { if (sender is Control { Tag: string page }) Navigate(page); }

    /// <summary>Marca no menu o item da página (o que também abre a página e a categoria dela).</summary>
    private void Navigate(string page)
    {
        if (Sidebar.GetLogicalDescendants().OfType<RadioButton>().FirstOrDefault(r => r.Tag as string == page) is { } nav)
            nav.IsChecked = true;
    }

    private static string? CheckedTag(Panel panel)
        => panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    // ---------------- atualização ----------------

    private void UpdateDashboard(bool full = true)
    {
        DrawDashChart();
        UpdateDashComponents();
        UpdateDashRings();
        if (full)
        {
            UpdateDashScores();
            ShowEvents();
        }
        if (full || (DateTime.Now - _healthAt).TotalSeconds > 60) UpdateDashHealth();
    }

    private static string F(float? v, string fmt, string unit) => v is float x ? x.ToString(fmt) + unit : "—";

    private void UpdateDashComponents()
    {
        var rows = new List<DashComponent>
        {
            new("", "Processador", GameCatalog.ShortCpuName(_cpuName),
                F(_monitor.CpuTemperature, "0", " °C"), F(_monitor.CpuLoad, "0", " %"), F(CpuClockAvg(), "0", " MHz"),
                F(_monitor.CpuPower, "0", " W"), "PageSensors"),
        };
        if (_gpus.Count > 0)
        {
            uint? clk = _gpus[0].NvmlHandle != IntPtr.Zero ? Nvml.Clock(_gpus[0].NvmlHandle, Nvml.ClockGraphics) : null;
            rows.Add(new("", "Placa de vídeo", GameCatalog.ShortGpuName(_gpus[0].Name),
                         F(MainGpuTemp(), "0", " °C"), F(MainGpuLoad(), "0", " %"), clk is uint c ? $"{c} MHz" : "—",
                         F(MainGpuPower(), "0", " W"), "PageGpu"));
        }
        var (total, avail) = _platform.SystemInfo.MemoryStatus();
        if (total > 0)
        {
            double used = total - avail;
            rows.Add(new("", "Memória RAM", $"{total / 1073741824.0:0} GB instalados", "—", $"{used / total:P0}", "—",
                         $"{used / 1073741824.0:0.0} GB", "PageSystem"));
        }
        if (_drives.FirstOrDefault(d => d.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase)) is { } c0)
            rows.Add(new("", $"Disco {c0.Letter}", c0.Disk.Length > 0 ? c0.Disk : c0.Kind, "—", c0.UsedPercent, "—",
                         $"{DriveCard.Fmt(c0.Free)} livres", "PageDisks"));
        DashComponents.ItemsSource = rows;
    }

    private void UpdateDashScores()
    {
        var runs = _settings.BenchRuns.Where(r => r.Multi != null).ToList();
        if (runs.Count > 0)
        {
            DashScore.Text = $"{runs[0].Multi:N0}";
            DashScoreSub.Text = $"pts · render multi-core · {runs[0].Date:dd/MM HH:mm}";
            if (runs.Count > 1)
            {
                double d = runs[0].Multi!.Value - runs[1].Multi!.Value;
                DashScoreDelta.Text = d >= 0 ? $"▲ {d:N0}" : $"▼ {-d:N0}";
            }
            else DashScoreDelta.Text = "";
        }
        else { DashScore.Text = "—"; DashScoreDelta.Text = ""; DashScoreSub.Text = "rode o RESET BENCH para ter uma nota"; }

        string Last(string test) => _settings.BenchRecords.FirstOrDefault(r => r.Test == test)?.Summary.Split('·')[0].Trim() ?? "—";
        var single = _settings.BenchRuns.FirstOrDefault(r => r.Single != null)?.Single;
        DashScores.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("Render single-core", single is double s ? $"{s:N0} pts" : "—"),
            new KeyValuePair<string, string>("Cena de jogo (GPU)", Last("gpu")),
            new KeyValuePair<string, string>("Simulação de jogo", Last("sim")),
        };
        DashCpuName.Text = GameCatalog.ShortCpuName(_cpuName);
        DashGpuName.Text = _gpus.Count > 0 ? GameCatalog.ShortGpuName(_gpus[0].Name) : "—";
    }

    private IBrush Res(string key) => (IBrush)this.FindResource(key)!;

    // ---------------- gráfico de área ----------------

    private void DrawDashChart()
    {
        DashChart.Children.Clear();
        double w = DashChart.Bounds.Width, h = DashChart.Bounds.Height;
        if (w < 80 || h < 60) return;

        int range = int.Parse(CheckedTag(DashRangePanel) ?? "300");
        string metric = CheckedTag(DashMetricPanel) ?? "temp";
        Func<DashSample, float?> cpu = metric switch { "load" => s => s.CpuL, "power" => s => s.CpuW, _ => s => s.CpuT };
        Func<DashSample, float?> gpu = metric switch { "load" => s => s.GpuL, "power" => s => s.GpuW, _ => s => s.GpuT };
        string unit = metric switch { "load" => " %", "power" => " W", _ => " °C" };
        string name = metric switch { "load" => "CARGA", "power" => "CONSUMO", _ => "TEMPERATURA" };
        var data = _dash.Skip(Math.Max(0, _dash.Count - range)).ToList();

        // Cabeçalho: valor atual grande + variação no período.
        float? cNow = data.Count > 0 ? cpu(data[^1]) : null, gNow = data.Count > 0 ? gpu(data[^1]) : null;
        DashChartLabel.Text = $"CPU · {name}";
        DashChartValue.Text = cNow is float cv ? $"{cv:0.#}{unit}" : "—";
        DashChartValue2.Text = gNow is float gv ? $"{gv:0.#}{unit}" : "—";
        var firstC = data.Select(cpu).FirstOrDefault(v => v != null);
        DashChartDelta.Text = cNow is float a && firstC is float b
            ? $"{(a - b >= 0 ? "+" : "")}{a - b:0.#}{unit} em {(range >= 60 ? $"{range / 60} min" : $"{range} s")}"
            : cNow == null && metric != "load" ? "sem leitura da CPU (veja o aviso no menu)" : "coletando…";

        var all = data.SelectMany(s => new[] { cpu(s), gpu(s) }).Where(v => v != null).Select(v => (double)v!.Value).ToList();
        double yMin = metric == "temp" ? 20 : 0;
        double yMax = metric switch
        {
            "load" => 100,
            "power" => Math.Max(50, all.Count > 0 ? all.Max() * 1.2 : 50),
            _ => Math.Max(90, all.Count > 0 ? all.Max() + 5 : 90)
        };
        const double left = 48, bottom = 22, top = 8;
        double plotW = w - left, plotH = h - top - bottom;
        double X(int i) => left + plotW * i / Math.Max(1, range - 1) + plotW * (range - data.Count) / Math.Max(1, range - 1);
        double Y(double v) => top + plotH * (1 - (Math.Clamp(v, yMin, yMax) - yMin) / (yMax - yMin));

        var fg = (ISolidColorBrush)Res("Fg");
        var faint = Res("Faint");
        var mono = (FontFamily)this.FindResource("Mono")!;

        // Grade tracejada horizontal + rótulos
        for (int k = 0; k <= 2; k++)
        {
            double v = yMin + (yMax - yMin) * k / 2, y = Y(v);
            DashChart.Children.Add(new Line { StartPoint = new Point(left, y), EndPoint = new Point(w, y), Stroke = Res("Line"),
                                              StrokeDashArray = new AvaloniaList<double> { 5, 5 } });
            var lbl = new TextBlock { Text = $"{v:0}{unit.Trim()}", FontSize = 11, Foreground = faint, FontFamily = mono };
            Canvas.SetLeft(lbl, 0); Canvas.SetTop(lbl, y - 8);
            DashChart.Children.Add(lbl);
        }
        // Eixo de tempo
        string[] xs = { range >= 60 ? $"-{range / 60} min" : $"-{range} s", range >= 120 ? $"-{range / 120} min" : $"-{range / 2} s", "agora" };
        for (int k = 0; k < 3; k++)
        {
            var t = new TextBlock { Text = xs[k], FontSize = 11, Foreground = faint };
            t.Measure(Size.Infinity);
            Canvas.SetLeft(t, Math.Clamp(left + plotW * k / 2 - t.DesiredSize.Width / 2, left, Math.Max(left, w - t.DesiredSize.Width)));
            Canvas.SetTop(t, h - 16);
            DashChart.Children.Add(t);
        }

        if (data.Count < 2) return;

        // GPU: linha tracejada discreta
        var gPts = new List<Point>();
        for (int i = 0; i < data.Count; i++) if (gpu(data[i]) is float g) gPts.Add(new Point(X(i), Y(g)));
        DashChart.Children.Add(new Polyline { Points = gPts, Stroke = Res("Muted"), StrokeThickness = 1.5,
                                              StrokeDashArray = new AvaloniaList<double> { 3, 2 }, StrokeJoin = PenLineJoin.Round });

        // CPU: área com degradê monocromático + linha
        var pts = new List<Point>();
        for (int i = 0; i < data.Count; i++) if (cpu(data[i]) is float c) pts.Add(new Point(X(i), Y(c)));
        if (pts.Count < 2) return;
        var col = fg.Color;
        var areaPts = new List<Point>(pts) { new(pts[^1].X, top + plotH), new(pts[0].X, top + plotH) };
        DashChart.Children.Add(new Polygon
        {
            Points = areaPts,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0x55, col.R, col.G, col.B), 0), new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1) },
            },
        });
        DashChart.Children.Add(new Polyline { Points = pts, Stroke = fg, StrokeThickness = 2, StrokeJoin = PenLineJoin.Round });

        // Marcador do valor atual com balão
        var last = pts[^1];
        DashChart.Children.Add(new Line { StartPoint = new Point(last.X, top), EndPoint = new Point(last.X, top + plotH), Stroke = faint,
                                          StrokeDashArray = new AvaloniaList<double> { 3, 3 } });
        var dot = new Ellipse { Width = 11, Height = 11, Stroke = fg, StrokeThickness = 2, Fill = Res("Card") };
        Canvas.SetLeft(dot, last.X - 5.5); Canvas.SetTop(dot, last.Y - 5.5);
        DashChart.Children.Add(dot);
        var bubble = new Border
        {
            Background = Res("Hover"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5),
            Child = new TextBlock { Text = DashChartValue.Text, FontFamily = mono, FontWeight = FontWeight.SemiBold, Foreground = fg }
        };
        bubble.Measure(Size.Infinity);
        Canvas.SetLeft(bubble, Math.Max(left, last.X - bubble.DesiredSize.Width - 10));
        Canvas.SetTop(bubble, Math.Clamp(last.Y - bubble.DesiredSize.Height - 10, 0, Math.Max(0, h - bubble.DesiredSize.Height)));
        DashChart.Children.Add(bubble);
    }

    // ---------------- anéis concêntricos ----------------

    private void UpdateDashRings()
    {
        var (total, avail) = _platform.SystemInfo.MemoryStatus();
        double ram = total > 0 ? (double)(total - avail) / total : 0;
        double? vram = null;
        if (_gpus.Count > 0 && _gpus[0].NvmlHandle != IntPtr.Zero && Nvml.MemoryInfo(_gpus[0].NvmlHandle) is Nvml.Memory m && m.Total > 0)
            vram = (double)m.Used / m.Total;
        var c0 = _drives.FirstOrDefault(d => d.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase));

        DashMemHero.Text = total > 0 ? $"{(total - avail) / 1073741824.0:0.0} GB" : "—";
        DashMemSub.Text = total > 0 ? $"de RAM em uso · {total / 1073741824.0:0} GB instalados" : "";

        IBrush fg = Res("Fg"), muted = Res("Muted"), faint = Res("Faint");
        var rings = new List<(double Frac, IBrush Brush, string Label)> { (ram, fg, "RAM") };
        if (vram is double v) rings.Add((v, muted, "VRAM"));
        if (c0 != null) rings.Add((c0.UsedFraction, faint, $"Disco {c0.Letter}"));

        DashRings.Children.Clear();
        double cx = DashRings.Width / 2, cy = DashRings.Height / 2;
        for (int i = 0; i < rings.Count; i++)
        {
            double r = 86 - i * 22;
            var track = new Ellipse { Width = r * 2, Height = r * 2, Stroke = Res("Line"), StrokeThickness = 12 };
            Canvas.SetLeft(track, cx - r); Canvas.SetTop(track, cy - r);
            DashRings.Children.Add(track);
            if (RingArc(rings[i].Frac, cx, cy, r) is { } arc)
                DashRings.Children.Add(new Avalonia.Controls.Shapes.Path { Data = arc, Stroke = rings[i].Brush, StrokeThickness = 12, StrokeLineCap = PenLineCap.Round });
        }
        var center = new TextBlock { Text = $"{ram:P0}", FontFamily = (FontFamily)this.FindResource("Mono")!, FontSize = 16,
                                     FontWeight = FontWeight.SemiBold, Foreground = fg };
        center.Measure(Size.Infinity);
        Canvas.SetLeft(center, cx - center.DesiredSize.Width / 2); Canvas.SetTop(center, cy - center.DesiredSize.Height / 2);
        DashRings.Children.Add(center);

        DashRingLegend.ItemsSource = rings.Select(r => new DashRingItem(r.Brush, r.Label, $"{r.Frac:P0}")).ToList();
    }

    private static Geometry? RingArc(double frac, double cx, double cy, double r)
    {
        frac = Math.Clamp(frac, 0, 0.9999);
        if (frac <= 0.001) return null;
        double a = frac * 2 * Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(cx, cy - r), false);
            ctx.ArcTo(new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)), new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return g;
    }

    // ---------------- saúde do PC ----------------

    private void UpdateDashHealth()
    {
        _healthAt = DateTime.Now;
        DashCheck Item(bool ok, string title, string okText, string badText) => new(title, ok ? okText : badText, ok);

        var list = new List<DashCheck>();
        if (_monitor.IsOpen) // enquanto detecta o hardware, ainda não dá para saber
        {
            var sensors = _monitor.DiagnoseCpuSensors();
            list.Add(Item(sensors == CpuSensorProblem.None, "Sensores da CPU",
                          "Temperatura e consumo disponíveis.", _monitor.Explain(sensors).Split('.')[0] + "."));
        }

        var errs = _platform.HardwareErrors.ErrorsSince(DateTime.Now.AddDays(-7));
        list.Add(Item(errs.Error == null && errs.Total == 0, "Sem erros de hardware",
                      "Nenhum WHEA ou reinício do driver de vídeo em 7 dias.",
                      errs.Error != null ? "Não foi possível ler o registro do sistema."
                                         : $"{errs.Whea} WHEA · {errs.GpuResets} reinícios do driver em 7 dias."));

#if WINDOWS
        var gm = _tweaks.FirstOrDefault(t => t.Id == "gamemode");
        var dvr = _tweaks.FirstOrDefault(t => t.Id == "gamedvr");
        try { gm?.Refresh(); dvr?.Refresh(); } catch { }
        if (gm != null) list.Add(Item(gm.IsOn, "Modo de Jogo ligado", "O Windows prioriza o jogo.", "Desligado: ligue em RESET OTIMIZA."));
        if (dvr != null) list.Add(Item(dvr.IsOn, "Gravação em segundo plano desligada", "O Game Bar não grava o tempo todo.",
                                       "O Game DVR está gravando: custa FPS."));

        try
        {
            var (reserved, inUse) = UsbCleaner.ComStatus();
            int stuck = reserved.Except(inUse).Count();
            list.Add(Item(stuck == 0, "Portas COM livres", "Nenhum número COM preso.",
                          $"{stuck} números presos por dispositivos antigos."));
        }
        catch { }
#endif

        DashHealth.ItemsSource = list;
    }
}

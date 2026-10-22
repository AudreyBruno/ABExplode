using System.Globalization;
using ABExplode.Core;
using ABExplode.Platform;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
#if WINDOWS
using ABExplode.Platform.Windows;
#endif

namespace ABExplode.App;

/// <summary>Stress test: CPU (todas as threads), RAM (grava e verifica) e GPU (estilo FurMark), com corte por temperatura.</summary>
public partial class MainWindow
{
    private StressTester _stress = null!; // criado no construtor (precisa da plataforma para ler a memória livre)
    private readonly List<double> _tempHistory = new();
    private readonly List<double> _gpuTempHistory = new();
    private const int HistoryLength = 300; // 5 min a 1 amostra/s
    private DateTime _stressStart;
    private TimeSpan _stressDuration;
    private double _tempLimit = 90, _gpuTempLimit = 85;
    private float _stressMaxTemp, _gpuMaxTemp;
#if WINDOWS
    private bool _gpuBurnOn;
    private readonly GpuBurn _burn = new();
#else
#pragma warning disable CS0649 // fica false até o stress de GPU em Vulkan (Fase 6)
    private bool _gpuBurnOn;
#pragma warning restore CS0649
#endif

    private bool TestRunning => _stress.Running || _gpuBurnOn;

    private void InitStress()
    {
        _stress = new StressTester(_platform.SystemInfo.FreePhysicalBytes);
#if !WINDOWS
        // TODO(Fase 6): stress de GPU em Vulkan.
        ChkGpu.IsEnabled = false;
        ToolTip.SetTip(ChkGpu, "Ainda não disponível neste sistema.");
#endif
    }

    private void BtnStress_Click(object? sender, RoutedEventArgs e)
    {
        if (TestRunning) StopStress("Parado pelo usuário");
        else StartStress();
    }

    private void ChkRam_Click(object? sender, RoutedEventArgs e) => RamFractionPanel.IsEnabled = ChkRam.IsChecked == true;

    private void ChkGpu_Click(object? sender, RoutedEventArgs e)
    {
        GpuOptions.IsEnabled = GpuTempLimit.IsEnabled = ChkGpu.IsChecked == true;
        DrawChart(); // mostra/oculta a linha de limite da GPU
    }

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private async void StartStress()
    {
        StressHint.Text = "";
        bool cpu = ChkCpu.IsChecked == true, ram = ChkRam.IsChecked == true, gpu = ChkGpu.IsChecked == true;
        if (!cpu && !ram && !gpu) { StressHint.Text = "Ative CPU, RAM ou GPU."; return; }
        if (AnyBenchRunning) { StressHint.Text = "Termine o benchmark antes."; return; }

        if (!TryNumber(TempLimit.Text, out _tempLimit) || _tempLimit < 40 || _tempLimit > 110)
        {
            StressHint.Text = "Limite da CPU deve estar entre 40 e 110 °C.";
            return;
        }
        if (gpu && (!TryNumber(GpuTempLimit.Text, out _gpuTempLimit) || _gpuTempLimit < 40 || _gpuTempLimit > 100))
        {
            StressHint.Text = "Limite da GPU deve estar entre 40 e 100 °C.";
            return;
        }
        if (!TryNumber(TxtDuration.Text, out double minutes) || minutes < 0)
        {
            StressHint.Text = "Duração inválida.";
            return;
        }
        if ((cpu || ram) && _monitor.CpuTemperature == null)
        {
            string why = _monitor.IsCpuDriverInstalled
                ? ""
                : "\n\nCausa provável: o driver PawnIO não está instalado. Instale com:\n" +
                  "winget install namazso.PawnIO\ne reabra o ABExplode.";
            if (!await Dialog.Ask(this, "ABExplode", "Nenhum sensor de temperatura da CPU foi encontrado, então o corte automático " +
                                                     "por temperatura da CPU NÃO funcionará. Continuar mesmo assim?" + why)) return;
        }
        if (gpu && MainGpuTemp() == null)
        {
            if (!await Dialog.Ask(this, "ABExplode", "Nenhum sensor de temperatura da GPU foi encontrado, então o corte automático " +
                                                     "por temperatura da GPU NÃO funcionará. Continuar mesmo assim?")) return;
        }

        double fraction = double.Parse(CheckedTag(RamFractionPanel) ?? "0.6", CultureInfo.InvariantCulture);

        _stressDuration = TimeSpan.FromMinutes(minutes);
        _stressStart = DateTime.Now;
        _stressMaxTemp = 0;
        _gpuMaxTemp = 0;
        _tempHistory.Clear();
        _gpuTempHistory.Clear();

        if (cpu || ram) _stress.Start(cpu, ram, fraction);

        string gpuDesc = "não";
#if WINDOWS
        if (gpu)
        {
            string res = CheckedTag(GpuResPanel) ?? "1920x1080";
            int msaa = int.Parse(CheckedTag(GpuMsaaPanel) ?? "4");
            var intensity = (CheckedTag(GpuIntensityPanel) ?? "128,48").Split(',').Select(int.Parse).ToArray();
            bool full = res == "full";
            var size = full ? new[] { 0, 0 } : res.Split('x').Select(int.Parse).ToArray();
            _burn.Start(new GpuBurnSettings
            {
                Fullscreen = full, Width = size[0], Height = size[1], Msaa = msaa,
                Shells = intensity[0], Iterations = intensity[1],
                AdapterName = _gpus.Count > 0 ? _gpus[0].Name : ""
            });
            _gpuBurnOn = true;
            gpuDesc = $"sim ({(full ? "tela cheia" : res)}, {(msaa > 1 ? $"MSAA {msaa}x" : "sem MSAA")}, " +
                      $"{GpuIntensityPanel.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Content}, limite {_gpuTempLimit:0} °C)";
        }
#endif

        SetStressControls(running: true);
        SetStatus("Rodando", alert: false);
        Log($"Iniciado · CPU {(cpu ? $"sim (limite {_tempLimit:0} °C)" : "não")} · RAM {(ram ? $"sim ({fraction:P0} da livre)" : "não")} · " +
            $"GPU {gpuDesc} · {(minutes > 0 ? $"{minutes:0.##} min" : "sem limite de tempo")}");
    }

    private void StopStress(string reason)
    {
        if (!TestRunning) return;
        AddEvent(Glyphs.Flash, "Stress test", reason, reason.StartsWith("ABORTADO") ? "abortado" : "ok");
        var errors = _stress.MemoryErrors;
        var passes = _stress.MemoryPasses;
        bool hadRam = _stress.MemoryAllocatedBytes > 0;
        bool hadCpu = _stress.Running;
        _stress.Stop();

        string gpuSummary = "";
#if WINDOWS
        if (_gpuBurnOn)
        {
            _burn.Stop();
            _gpuBurnOn = false;
            // "Pontuação" no estilo FurMark: frames renderizados e FPS médio.
            gpuSummary = $" · GPU: {_burn.Frames:N0} frames, média {_burn.AverageFps:0} FPS, temp. máx. {_gpuMaxTemp:0.#} °C";
        }
#endif
        SetStressControls(running: false);

        bool aborted = reason.StartsWith("ABORTADO");
        SetStatus(aborted ? "Abortado" : "Parado", alert: aborted);
        Log($"{reason} · duração {DateTime.Now - _stressStart:hh\\:mm\\:ss}" +
            (hadCpu ? $" · CPU temp. máx. {_stressMaxTemp:0.#} °C" : "") +
            (hadRam ? $" · RAM {passes} passes, {errors} erros" : "") + gpuSummary);
    }

    private void SetStressControls(bool running)
    {
        SetRunningButton(BtnStress, running, running ? "PARAR" : "INICIAR");
        ChkCpu.IsEnabled = ChkRam.IsEnabled = !running;
#if WINDOWS
        ChkGpu.IsEnabled = !running;
#endif
        RamFractionPanel.IsEnabled = !running && ChkRam.IsChecked == true;
        GpuOptions.IsEnabled = GpuTempLimit.IsEnabled = !running && ChkGpu.IsChecked == true;
        TempLimit.IsEnabled = TxtDuration.IsEnabled = !running;
        SideStress.Text = running ? "rodando" : "parado";
    }

    /// <summary>Alerta = cartão invertido (claro com texto escuro no modo escuro).</summary>
    private void SetStatus(string text, bool alert)
    {
        StStatus.Text = text;
        StatusCard.Background = Res(alert ? "Fg" : "Card");
        StStatus.Foreground = Res(alert ? "Ink" : "Fg");
        StStatusLabel.Foreground = Res(alert ? "Ink" : "Muted");
    }

    private string MainGpuPerfCap()
    {
        if (_gpus.Count == 0) return "";
        return _gpus[0].LiveExtras().FirstOrDefault(kv => kv.Key.StartsWith("PerfCap")).Value ?? "";
    }

    /// <summary>A cada segundo (com a página aberta ou não): histórico, leituras e corte automático.</summary>
    private void UpdateStressPanel()
    {
        float? temp = _monitor.CpuTemperature;
        float? load = _monitor.CpuLoad;
        float? power = _monitor.CpuPower;
        float? gTemp = MainGpuTemp();
        float? gPower = MainGpuPower();

        if (temp is float t)
        {
            _tempHistory.Add(t);
            if (_tempHistory.Count > HistoryLength) _tempHistory.RemoveAt(0);
            if (_stress.Running && t > _stressMaxTemp) _stressMaxTemp = t;
        }
        if (gTemp is float gt)
        {
            _gpuTempHistory.Add(gt);
            if (_gpuTempHistory.Count > HistoryLength) _gpuTempHistory.RemoveAt(0);
            if (_gpuBurnOn && gt > _gpuMaxTemp) _gpuMaxTemp = gt;
        }

        StTemp.Text = temp == null ? "—" : $"{temp:0.#} °C";
        StTempMax.Text = _stressMaxTemp > 0 ? $"máx. {_stressMaxTemp:0.#} °C" : "máx. —";
        StLoad.Text = load == null ? "carga —" : $"carga {load:0} %";
        StPower.Text = power == null ? "—" : $"{power:0.#} W";

        StGpuTemp.Text = gTemp == null ? "—" : $"{gTemp:0.#} °C";
        StGpuTempMax.Text = _gpuMaxTemp > 0 ? $"máx. {_gpuMaxTemp:0.#} °C" : "máx. —";
        StGpuPower.Text = gPower == null ? "—" : $"{gPower:0.#} W";
        string cap = MainGpuPerfCap();
        StGpuCap.Text = cap.Length > 0 ? $"limitador: {cap}" : "—";
#if WINDOWS
        StFps.Text = _gpuBurnOn && _burn.Fps > 0 ? $"{_burn.Fps:0}" : "—";
        StFpsAvg.Text = _gpuBurnOn && _burn.AverageFps > 0 ? $"média {_burn.AverageFps:0}" : "média —";
#endif

        if (TestRunning)
        {
            var elapsed = DateTime.Now - _stressStart;
            StElapsed.Text = elapsed.ToString(@"hh\:mm\:ss");
            StRam.Text = _stress.MemoryAllocatedBytes > 0 ? $"{_stress.MemoryPasses} / {_stress.MemoryErrors}" : "—";

            if (_stress.Running && temp >= _tempLimit)
                StopStress($"ABORTADO: CPU a {temp:0.#} °C atingiu o limite de {_tempLimit:0} °C");
            else if (_gpuBurnOn && gTemp >= _gpuTempLimit)
                StopStress($"ABORTADO: GPU a {gTemp:0.#} °C atingiu o limite de {_gpuTempLimit:0} °C");
            else if (_stress.MemoryErrors > 0)
                StopStress($"ABORTADO: {_stress.MemoryErrors} erros de memória detectados");
#if WINDOWS
            else if (_gpuBurnOn && !_burn.Running)
                StopStress(_burn.Error != null ? $"ABORTADO: falha na GPU ({_burn.Error})" : "Janela da GPU fechada pelo usuário");
#endif
            else if (_stressDuration > TimeSpan.Zero && elapsed >= _stressDuration)
                StopStress("Concluído (tempo esgotado)");
        }

        if (PageStress.IsVisible) DrawChart();
    }

    private void Log(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {msg}";
        StressLog.Items.Add(line);
        StressLog.ScrollIntoView(line);
    }

    private void TempChart_SizeChanged(object? sender, SizeChangedEventArgs e) => DrawChart();

    /// <summary>Desenha o histórico de temperatura da CPU (claro) e da GPU (cinza) com as linhas de limite.</summary>
    private void DrawChart()
    {
        TempChart.Children.Clear();
        double w = TempChart.Bounds.Width, h = TempChart.Bounds.Height;
        if (w < 50 || h < 40) return;

        const double left = 36, top = 6, bottom = 6;
        double plotW = w - left, plotH = h - top - bottom;
        double yMin = 20, yMax = Math.Max(110, Math.Max(_tempLimit, _gpuTempLimit) + 10);
        double Y(double v) => top + plotH * (1 - (Math.Clamp(v, yMin, yMax) - yMin) / (yMax - yMin));

        var mono = (FontFamily)this.FindResource("Mono")!;
        for (double v = yMin; v <= yMax; v += 20)
        {
            TempChart.Children.Add(new Line { StartPoint = new Point(left, Y(v)), EndPoint = new Point(w, Y(v)), Stroke = Res("Line") });
            var lbl = new TextBlock { Text = $"{v:0}°", FontSize = 10, Foreground = Res("Faint"), FontFamily = mono };
            Canvas.SetLeft(lbl, 0); Canvas.SetTop(lbl, Y(v) - 7);
            TempChart.Children.Add(lbl);
        }

        void LimitLine(double v, IBrush b) => TempChart.Children.Add(new Line
        {
            StartPoint = new Point(left, Y(v)), EndPoint = new Point(w, Y(v)),
            Stroke = b, StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 4, 3 }
        });
        LimitLine(_tempLimit, Res("Muted"));
        if (ChkGpu.IsChecked == true) LimitLine(_gpuTempLimit, Res("Faint"));

        double step = plotW / (HistoryLength - 1);
        void Series(List<double> data, IBrush stroke, bool fill)
        {
            if (data.Count < 2) return;
            int offset = HistoryLength - data.Count; // alinha à direita
            var pts = data.Select((v, i) => new Point(left + (offset + i) * step, Y(v))).ToList();
            if (fill && stroke is ISolidColorBrush sc)
            {
                var c = sc.Color;
                var area = new List<Point>(pts) { new(pts[^1].X, top + plotH), new(pts[0].X, top + plotH) };
                TempChart.Children.Add(new Polygon { Points = area, Fill = new SolidColorBrush(Color.FromArgb(0x16, c.R, c.G, c.B)) });
            }
            TempChart.Children.Add(new Polyline { Points = pts, Stroke = stroke, StrokeThickness = 1.6, StrokeJoin = PenLineJoin.Round });
        }
        Series(_gpuTempHistory, Res("Muted"), fill: false);
        Series(_tempHistory, Res("Fg"), fill: true);
    }
}

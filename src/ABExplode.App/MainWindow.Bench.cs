using System.Runtime.InteropServices;
using ABExplode.Core;
using ABExplode.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
#if WINDOWS
using ABExplode.Platform.Windows;
#else
using ABExplode.Vulkan; // cena de jogo e ray tracing em Vulkan, com a mesma interface do Direct3D e do DXR
#endif

namespace ABExplode.App;

/// <summary>Linha de medidor (rótulo, valor e barra 0–1): históricos de benchmark.</summary>
public sealed record MeterItem(string Label, string Value, double Fraction);

/// <summary>Resultado com explicação (Memória e carregamento, Disco).</summary>
public sealed record MemRow(string Label, string Value, string Hint);

/// <summary>
/// RESET BENCH: render de CPU (estilo Cinebench) e os benchmarks de jogo (cena de GPU, ray tracing, simulação de CPU,
/// memória e carregamento, disco), com histórico de cada um.
/// </summary>
public partial class MainWindow
{
    // ---------------- Render (CPU) ----------------

    private readonly BenchRenderer _bench = new();
    private WriteableBitmap? _benchBitmap;
    private CancellationTokenSource? _benchCts;
    private DispatcherTimer? _benchTimer;
    private DateTime _benchStart;
    private string _benchPhase = "";
    private DateTime _benchPhaseStart;

    // Benchmarks de jogo
    private bool _gameBenchRunning;
    private bool _gpuBenchActive; // sensores pausados: consultas ao driver geravam engasgos no 1% low
    private CancellationTokenSource? _gameBenchCts;
    private WriteableBitmap? _simBitmap;
    private int[] _simPixels = Array.Empty<int>();
    private const int SimView = 440;

    private bool BenchRunning => _benchCts != null;
    private bool AnyBenchRunning => BenchRunning || _gameBenchRunning;

    private void InitBench()
    {
        _benchBitmap = NewBitmap(BenchRenderer.Width, BenchRenderer.Height);
        BenchImage.Source = _benchBitmap;
        UpdateBenchHistory();
        UpdateGameBenchHistory();
        UpdateBenchLive();
    }

    private static WriteableBitmap NewBitmap(int w, int h) =>
        new(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    /// <summary>Copia pixels BGRA (um int por pixel, como no WPF) para o bitmap, linha a linha.</summary>
    private static void Blit(WriteableBitmap bmp, int[] pixels, int w, int h)
    {
        using var fb = bmp.Lock();
        for (int y = 0; y < h; y++)
            Marshal.Copy(pixels, y * w, fb.Address + y * fb.RowBytes, w);
    }

    private async void BtnBench_Click(object? sender, RoutedEventArgs e)
    {
        if (BenchRunning) { _benchCts!.Cancel(); return; }
        if (TestRunning || _gameBenchRunning) { BenchHint.Text = "Pare o outro teste antes de rodar o benchmark."; return; }

        string mode = CheckedTag(BenchModePanel) ?? "both";
        bool sustained = CheckedTag(BenchDurationPanel) == "10";
        var minDuration = sustained ? TimeSpan.FromMinutes(10) : TimeSpan.Zero;
        int threads = Environment.ProcessorCount;

        _benchCts = new CancellationTokenSource();
        var ct = _benchCts.Token;
        SetRunningButton(BtnBench, true, "PARAR");
        BenchModePanel.IsEnabled = BenchDurationPanel.IsEnabled = BenchTestPanel.IsEnabled = false;
        BenchHint.Text = "";
        BenchIdle.IsVisible = false;
        BenchMt.Text = BenchSt.Text = "—";
        BenchRatio.Text = "";
        _benchStart = DateTime.Now;
        if (_benchTimer == null)
        {
            _benchTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
            _benchTimer.Tick += (_, _) => UpdateBenchView();
        }
        _benchTimer.Start();

        double? mt = null, st = null;
        try
        {
            if (mode is "mt" or "both")
            {
                _benchPhase = $"Multi-core · {threads} threads";
                _benchPhaseStart = DateTime.Now;
                var r = await Task.Run(() => _bench.Run(threads, minDuration, ct), ct);
                if (!ct.IsCancellationRequested) { mt = r.Score; BenchMt.Text = $"{r.Score:N0}"; }
            }
            if (!ct.IsCancellationRequested && mode is "st" or "both")
            {
                _benchPhase = "Single-core · 1 thread";
                _benchPhaseStart = DateTime.Now;
                var r = await Task.Run(() => _bench.Run(1, minDuration, ct), ct);
                if (!ct.IsCancellationRequested) { st = r.Score; BenchSt.Text = $"{r.Score:N0}"; }
            }
        }
        catch (OperationCanceledException) { }

        _benchTimer.Stop();
        UpdateBenchView();
        bool cancelled = ct.IsCancellationRequested;
        _benchCts = null;
        SetRunningButton(BtnBench, false, "INICIAR");
        BenchModePanel.IsEnabled = BenchDurationPanel.IsEnabled = BenchTestPanel.IsEnabled = true;
        BenchBuckets.Children.Clear();

        var total = DateTime.Now - _benchStart;
        if (cancelled)
        {
            BenchStatus.Text = "Cancelado";
            return;
        }
        BenchStatus.Text = $"Concluído em {total:m\\:ss}";
        if (mt is double m && st is double s)
            BenchRatio.Text = $"Multi ÷ single: {m / s:0.0}x com {threads} threads " +
                              $"({Environment.ProcessorCount / 2} núcleos com SMT costumam render 8–12x).";

        _settings.BenchRuns.Insert(0, new BenchRun(DateTime.Now, mt, st, sustained, GameCatalog.ShortCpuName(_cpuName)));
        AddEvent(Glyphs.Trophy, "Benchmark", "Render de CPU", mt is double mte ? $"{mte:N0} pts" : st is double ste ? $"{ste:N0} pts" : "");
        if (_settings.BenchRuns.Count > 10) _settings.BenchRuns.RemoveRange(10, _settings.BenchRuns.Count - 10);
        _settings.Save();
        UpdateBenchHistory();
    }

    /// <summary>Copia a imagem parcial e desenha o contorno dos blocos que cada thread está renderizando.</summary>
    private void UpdateBenchView()
    {
        Blit(_benchBitmap!, _bench.Pixels, BenchRenderer.Width, BenchRenderer.Height);
        BenchImage.InvalidateVisual();

        BenchBuckets.Children.Clear();
        foreach (int b in _bench.Active)
        {
            if (b < 0) continue;
            var (x, y, w, h) = _bench.Buckets[b];
            var rect = new Rectangle { Width = w, Height = h, Stroke = Brushes.White, StrokeThickness = 1.5 };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            BenchBuckets.Children.Add(rect);
        }

        if (BenchRunning)
        {
            double frac = _bench.BucketsTotal > 0 ? (double)_bench.BucketsDone / _bench.BucketsTotal : 0;
            BenchProgress.Value = frac;
            // Tempo restante da passada atual, pela velocidade até agora (só depois de 3% para não oscilar).
            var phaseElapsed = DateTime.Now - _benchPhaseStart;
            string eta = frac >= 0.03 ? $" · faltam ~{TimeSpan.FromSeconds(phaseElapsed.TotalSeconds * (1 - frac) / frac):m\\:ss}" : "";
            BenchStatus.Text = $"{_benchPhase} · {frac:P0}{eta}";
            BenchElapsed.Text = (DateTime.Now - _benchStart).ToString(@"mm\:ss");
        }
        else BenchProgress.Value = 0;
    }

    /// <summary>Temperatura, clock médio dos núcleos e consumo da CPU (atualizado junto com os sensores).</summary>
    private void UpdateBenchLive()
    {
        BenchLive.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("CPU", GameCatalog.ShortCpuName(_cpuName)),
            new KeyValuePair<string, string>("Threads", $"{Environment.ProcessorCount}"),
            new KeyValuePair<string, string>("Temperatura", _monitor.CpuTemperature is float t ? $"{t:0.#} °C" : "—"),
            new KeyValuePair<string, string>("Clock médio", CpuClockAvg() is float c ? $"{c:0} MHz" : "—"),
            new KeyValuePair<string, string>("Consumo", _monitor.CpuPower is float p ? $"{p:0.#} W" : "—"),
            new KeyValuePair<string, string>("Carga", _monitor.CpuLoad is float l ? $"{l:0} %" : "—"),
        };
    }

    private void UpdateBenchHistory()
    {
        var runs = _settings.BenchRuns;
        BenchHistoryEmpty.IsVisible = runs.Count == 0;
        double bestMt = runs.Max(r => r.Multi) ?? 1, bestSt = runs.Max(r => r.Single) ?? 1;
        BenchHistory.ItemsSource = runs.Select(r =>
        {
            string label = $"{r.Date:dd/MM HH:mm}{(r.Sustained ? " · 10 min" : "")}";
            string value = $"{(r.Multi is double m ? $"{m:N0}" : "—")} / {(r.Single is double s ? $"{s:N0}" : "—")}";
            double frac = r.Multi is double mm ? mm / bestMt : r.Single is double ss ? ss / bestSt : 0;
            return new MeterItem(label, value, frac);
        }).ToList();
    }

    // ---------------- Benchmarks de jogo: comum ----------------

    private void BenchTest_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } chip || BenchPanelRender == null) return;
        string test = (string)chip.Tag!;
        BenchPanelRender.IsVisible = test == "render";
        BenchPanelGpu.IsVisible = test == "gpu";
        BenchPanelRt.IsVisible = test == "rt";
        BenchPanelSim.IsVisible = test == "sim";
        BenchPanelMem.IsVisible = test == "mem";
        BenchPanelDisk.IsVisible = test == "disk";
        if (test == "disk") InitDiskBench();
        BenchSub.Text = test switch
        {
            "gpu" => "Cena de jogo em Direct3D 11 para medir a placa de vídeo, no estilo 3DMark / Unigine",
            "rt" => "Ray tracing por hardware (DXR 1.1, Direct3D 12): usa os núcleos de RT da placa, com 3 passadas e nota pela mediana",
            "sim" => "Quanto FPS a CPU aguenta numa simulação de jogo com milhares de unidades",
            "mem" => "Latência e banda da RAM e velocidade de carregamento do disco",
            "disk" => "Velocidade de leitura e escrita do disco: sequencial e acessos aleatórios de 4 KB, no estilo CrystalDiskMark",
            _ => "Benchmark de CPU por renderização 3D (path tracing), no mesmo princípio do Cinebench",
        };
        UpdateGameBenchHistory();
    }

    private void SaveBenchRecord(string test, double score, string summary)
    {
        AddEvent(Glyphs.Trophy, "Benchmark concluído", summary, "");
        _settings.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, test, score, summary));
        // Mantém os 10 mais recentes de cada teste.
        _settings.BenchRecords = _settings.BenchRecords.GroupBy(r => r.Test).SelectMany(g => g.Take(10))
                                          .OrderByDescending(r => r.Date).ToList();
        _settings.Save();
        UpdateGameBenchHistory();
    }

    private void UpdateGameBenchHistory()
    {
        List<MeterItem> For(string test)
        {
            var list = _settings.BenchRecords.Where(r => r.Test == test).ToList();
            double best = list.Count > 0 ? list.Max(r => r.Score) : 1;
            return list.Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, best > 0 ? r.Score / best : 0)).ToList();
        }
        GbHistory.ItemsSource = For("gpu");
        RtHistory.ItemsSource = RtHistoryItems();
        SimHistory.ItemsSource = For("sim");
        MemHistory.ItemsSource = For("mem");
        DiskHistory.ItemsSource = For("disk");
    }

    /// <summary>Ray tracing: cada barra compara com o melhor resultado do MESMO preset e resolução (só isso é comparável).</summary>
    private List<MeterItem> RtHistoryItems()
    {
        var list = _settings.BenchRecords.Where(r => r.Test.StartsWith("rt:")).OrderByDescending(r => r.Date).ToList();
        var best = list.GroupBy(r => r.Test).ToDictionary(g => g.Key, g => g.Max(r => r.Score));
        return list.Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, best[r.Test] > 0 ? r.Score / best[r.Test] : 0)).ToList();
    }

    private void SetRunningButton(Button btn, bool running, string text)
    {
        btn.Content = text;
        btn.Theme = (Avalonia.Styling.ControlTheme?)this.FindResource(running ? "OutlineButton" : "PrimaryButton");
    }

    private void SetGameBenchRunning(Button btn, bool running)
    {
        _gameBenchRunning = running;
        SetRunningButton(btn, running, running ? "CANCELAR" : "INICIAR");
        BenchTestPanel.IsEnabled = !running;
    }

    private static DispatcherTimer Every(int ms, Action tick)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => tick();
        t.Start();
        return t;
    }

    // ---------------- Cena de jogo (Direct3D 11 no Windows, Vulkan no Linux) e ray tracing (DXR no Windows, Vulkan no Linux) ----------------

    private GpuGameBench? _gpuBench;

    private async void BtnGpuBench_Click(object? sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gpuBench?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { GbStatus.Text = "Termine o outro teste antes."; return; }

        var size = (CheckedTag(GbResPanel) ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        int secs = int.Parse(CheckedTag(GbDurPanel) ?? "60");
        _gpuBench = new GpuGameBench
        {
            Width = size[0], Height = size[1], Duration = TimeSpan.FromSeconds(secs),
            AdapterName = _gpus.Count > 0 ? _gpus[0].Name : ""
        };
        SetGameBenchRunning(BtnGpuBench, true);
        GbResult.ItemsSource = null;
        var bench = _gpuBench;
        var timer = Every(250, () =>
        {
            GbFpsNow.Text = bench.CurrentFps > 0 ? $"{bench.CurrentFps:0}" : "—";
            GbProgress.Value = bench.Progress;
            GbStatus.Text = $"Rodando · {size[0]}x{size[1]} · {bench.Progress:P0}";
        });
        _gpuBenchActive = true;
        GbGpuNow.Text = "sensores pausados";
        bool overlayWasOn = PauseOverlay(); // o overlay também consulta a GPU

        // Thread dedicada: a cena roda num loop próprio com prioridade alta.
        var r = await Task.Factory.StartNew(bench.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Stop();
        _gpuBenchActive = false;
        ResumeOverlay(overlayWasOn);
        GbGpuNow.Text = "—";
        SetGameBenchRunning(BtnGpuBench, false);
        GbProgress.Value = 0;
        GbFpsNow.Text = "—";

        if (r == null)
        {
            GbStatus.Text = bench.Cancelled ? "Cancelado" : $"Falhou: {bench.Error}";
            return;
        }
        GbStatus.Text = $"Concluído · {r.Adapter}";
        GbResult.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("Nota", $"{r.Score:N0} pts"),
            new KeyValuePair<string, string>("FPS médio", $"{r.AvgFps:0.0}"),
            new KeyValuePair<string, string>("1% low", $"{r.Low1Fps:0.0}"),
            new KeyValuePair<string, string>("Resolução", $"{r.Width}x{r.Height}"),
            new KeyValuePair<string, string>("Frames", $"{r.Frames:N0}"),
        };
        // Notas de Vulkan e Direct3D não se comparam: no Linux o histórico diz qual foi
        SaveBenchRecord("gpu", r.Score, $"{r.Score:N0} pts · {r.AvgFps:0} FPS · {r.Height}p" + (OperatingSystem.IsWindows() ? "" : " · Vulkan"));
    }

    private GpuRtBench? _rtBench;

    private async void BtnRtBench_Click(object? sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _rtBench?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { RtStatus.Text = "Termine o outro teste antes."; return; }

        string adapterName = _gpus.Count > 0 ? _gpus[0].Name : "";
        RtStatus.Text = "Verificando suporte a ray tracing…";
        string? why = await Task.Run(() => GpuRtBench.CheckSupport(adapterName));
        if (why != null) { RtStatus.Text = why; return; }

        var size = (CheckedTag(RtResPanel) ?? "2560x1440").Split('x').Select(int.Parse).ToArray();
        string preset = CheckedTag(RtPresetPanel) ?? "alta";
        bool quick = CheckedTag(RtModePanel) == "quick";
        _rtBench = new GpuRtBench
        {
            Width = size[0], Height = size[1], Preset = preset, AdapterName = adapterName, Passes = 3,
            Warmup = TimeSpan.FromSeconds(quick ? 5 : 20), PassDuration = TimeSpan.FromSeconds(quick ? 10 : 30),
        };
        var bench = _rtBench;
        SetGameBenchRunning(BtnRtBench, true);
        RtResult.ItemsSource = null;
        RtVerdict.Text = "";
        var timer = Every(250, () =>
        {
            RtFpsNow.Text = bench.CurrentFps > 0 ? $"{bench.CurrentFps:0}" : "—";
            RtPhase.Text = bench.Phase.Length > 0 ? bench.Phase : "—";
            RtProgress.Value = bench.Progress;
            RtStatus.Text = $"Rodando · {size[0]}x{size[1]} · {PresetName(preset)} · {bench.Progress:P0}";
        });

        // Igual à cena de jogo: sensores e overlay pausados (consultas ao driver geram engasgos no frametime).
        _gpuBenchActive = true;
        bool overlayWasOn = PauseOverlay();
        var r = await Task.Factory.StartNew(bench.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Stop();
        _gpuBenchActive = false;
        ResumeOverlay(overlayWasOn);
        SetGameBenchRunning(BtnRtBench, false);
        RtProgress.Value = 0;
        RtFpsNow.Text = "—";
        RtPhase.Text = "—";

        if (r == null)
        {
            RtStatus.Text = bench.Cancelled ? "Cancelado" : $"Falhou: {bench.Error}";
            return;
        }
        string driver = await Task.Run(() => _platform.Gpu.DriverVersionOf(r.Adapter));
        RtStatus.Text = $"Concluído · {r.Adapter}" + (driver.Length > 0 ? $" · driver {driver}" : "");
        RtVerdict.Text = r.Reliable
            ? $"✔ Resultado confiável: as 3 passadas variaram só {r.VariationPct:0.0}%."
            : $"⚠ Variação de {r.VariationPct:0.0}% entre as passadas (acima de 3%). Algo atrapalhou: programa em segundo plano, " +
              "temperatura ou limite de energia. Feche outros programas e rode de novo antes de comparar.";
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Nota", $"{r.Score:N0} pts"),
            new("FPS (mediana)", $"{r.MedianFps:0.0}"),
            new("1% low (mediana)", $"{r.MedianLow1:0.0}"),
            new("Variação entre passadas", $"{r.VariationPct:0.0}%"),
            new("Raios por segundo (estimado)", $"{r.GigaRaysPerSec:0.0} bilhões"),
            new("Preset · resolução", $"{PresetName(r.Preset)} · {r.Width}x{r.Height}"),
        };
        for (int i = 0; i < r.Passes.Count; i++)
            rows.Add(new($"Passada {i + 1}", $"{r.Passes[i].AvgFps:0.0} FPS · 1% low {r.Passes[i].Low1Fps:0.0}"));
        RtResult.ItemsSource = rows;
        SaveBenchRecord($"rt:{r.Preset}:{r.Height}",
                        r.Score, $"{r.Score:N0} pts · {r.MedianFps:0} FPS · {PresetName(r.Preset)} {r.Height}p" + (r.Reliable ? "" : " · instável") + (OperatingSystem.IsWindows() ? "" : " · Vulkan"));
    }

    private static string PresetName(string p) => p switch { "normal" => "Normal", "extrema" => "Extrema", _ => "Alta" };

    // ---------------- Simulação de jogo (CPU) ----------------

    private async void BtnSimBench_Click(object? sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { SimStatus.Text = "Termine o outro teste antes."; return; }

        int secs = int.Parse(CheckedTag(SimDurPanel) ?? "20");
        _simBitmap ??= NewBitmap(SimView, SimView);
        if (_simPixels.Length == 0) _simPixels = new int[SimView * SimView];
        SimImage.Source = _simBitmap;
        SimIdle.IsVisible = false;

        var sim = new GameSimBench();
        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnSimBench, true);
        SimResult.ItemsSource = null;
        SimStatus.Text = "Aquecendo (2 s)…";
        var timer = Every(100, () =>
        {
            DrawSim(sim);
            SimProgress.Value = sim.Progress;
            if (sim.FramesDone > 0) SimStatus.Text = $"Simulando · {sim.Progress:P0}";
            SimFpsNow.Text = sim.CurrentFps > 0 ? $"{sim.CurrentFps:0} FPS" : "";
        });

        var r = await Task.Run(() => sim.Run(TimeSpan.FromSeconds(secs), ct));
        timer.Stop();
        bool cancelled = ct.IsCancellationRequested;
        SetGameBenchRunning(BtnSimBench, false);
        SimProgress.Value = 0;
        SimFpsNow.Text = "";
        if (cancelled) { SimStatus.Text = "Cancelado"; return; }

        SimStatus.Text = $"Concluído · {r.Frames:N0} frames em {r.Seconds:0} s";
        SimResult.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("FPS médio (nota)", $"{r.AvgFps:0}"),
            new KeyValuePair<string, string>("1% low", $"{r.Low1Fps:0}"),
            new KeyValuePair<string, string>("Thread principal", $"{r.MainThreadMs:0.00} ms/frame"),
            new KeyValuePair<string, string>("Física + IA (paralelo)", $"{r.ParallelMs:0.00} ms/frame"),
            new KeyValuePair<string, string>("Unidades", $"{GameSimBench.Entities:N0}"),
        };
        SaveBenchRecord("sim", r.Score, $"{r.AvgFps:0} FPS · 1% low {r.Low1Fps:0}");
    }

    /// <summary>Vista de cima da batalha: time A em branco, time B em cinza.</summary>
    private void DrawSim(GameSimBench sim)
    {
        Array.Fill(_simPixels, unchecked((int)0xFF050505));
        float[] xs = sim.ViewX, ys = sim.ViewY; byte[] team = sim.ViewTeam;
        float k = SimView / GameSimBench.World;
        for (int i = 0; i < GameSimBench.Entities; i++)
        {
            int px = (int)(xs[i] * k), py = (int)(ys[i] * k);
            if ((uint)px < SimView && (uint)py < SimView)
                _simPixels[py * SimView + px] = team[i] == 0 ? unchecked((int)0xFFF5F5F5) : unchecked((int)0xFF5A5A5A);
        }
        Blit(_simBitmap!, _simPixels, SimView, SimView);
        SimImage.InvalidateVisual();
    }

    // ---------------- Memória e carregamento ----------------

    private async void BtnMemBench_Click(object? sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { MemStatus.Text = "Termine o outro teste antes."; return; }

        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnMemBench, true);
        var rows = new List<MemRow>();
        void Show() => MemResult.ItemsSource = rows.ToList();
        (string Phase, double Frac) progress = ("", 0);
        var timer = Every(150, () =>
        {
            MemStatus.Text = progress.Phase;
            MemProgress.Value = progress.Frac;
        });

        try
        {
            progress = ("Medindo latência da RAM…", 0.05);
            double lat = await Task.Run(() => MemoryBench.MeasureLatencyNs(ct));
            ct.ThrowIfCancellationRequested();
            rows.Add(new MemRow("Latência da RAM", $"{lat:0.0} ns", "Acesso aleatório. Menor é melhor; pesa muito em jogos competitivos."));
            Show();

            progress = ("Medindo banda da RAM…", 0.2);
            var bw = await Task.Run(() => MemoryBench.MeasureBandwidth(ct));
            ct.ThrowIfCancellationRequested();
            rows.Add(new MemRow("Banda de leitura da RAM", $"{bw.Read:0.0} GB/s", "Todas as threads lendo em sequência."));
            rows.Add(new MemRow("Banda de cópia da RAM", $"{bw.Copy:0.0} GB/s", "Leitura + escrita."));
            Show();

            var load = await Task.Run(() => MemoryBench.MeasureLoading((phase, f) => progress = (phase, 0.35 + f * 0.6), ct));
            rows.Add(new MemRow($"SSD {load.Drive} · leitura sequencial", $"{load.SeqReadMBs:N0} MB/s", "Pacote de 512 MB, sem cache do sistema."));
            rows.Add(new MemRow("Assets pequenos", $"{load.FilesPerSec:N0} arq/s",
                                $"Mil arquivos de 64 KB com 8 threads ({load.SmallFilesMBs:N0} MB/s), como engines modernas."));
            rows.Add(new MemRow("Carregamento do mapa", $"{load.MapLoadSeconds:0.00} s", "Pacote + assets: quanto menor, mais rápido o jogo carrega."));
            Show();

            MemStatus.Text = "Concluído · arquivos de teste apagados";
            // Nota para a barra do histórico: mais rápido = maior (inverso da latência × velocidade de carga).
            SaveBenchRecord("mem", 1000 / lat + 10 / load.MapLoadSeconds,
                            $"{lat:0} ns · {bw.Read:0} GB/s · mapa {load.MapLoadSeconds:0.00} s");
        }
        catch (OperationCanceledException) { MemStatus.Text = "Cancelado (arquivos de teste apagados)"; }
        catch (Exception ex) { MemStatus.Text = $"Falhou: {ex.Message}"; }
        finally
        {
            timer.Stop();
            MemProgress.Value = 0;
            SetGameBenchRunning(BtnMemBench, false);
        }
    }

    // ---------------- Disco ----------------

    private void InitDiskBench()
    {
        if (_gameBenchRunning) return; // não mexe nos botões durante um teste
        string? keep = CheckedTag(DiskDrivePanel);
        DiskDrivePanel.Children.Clear();
        var drives = DiskBench.Drives();
        if (drives.Count == 0)
        {
            DiskStatus.Text = "Nenhuma unidade com espaço livre suficiente (1,5 GB).";
            BtnDiskBench.IsEnabled = false;
            return;
        }
        BtnDiskBench.IsEnabled = true;
        string pick = keep != null && drives.Any(d => d.Root == keep) ? keep
                    : drives.FirstOrDefault(d => d.Root.StartsWith("C", StringComparison.OrdinalIgnoreCase)).Root ?? drives[0].Root;
        foreach (var (root, text) in drives)
            DiskDrivePanel.Children.Add(new RadioButton
            {
                Content = text, Tag = root, GroupName = "DiskDrive", IsChecked = root == pick,
                Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("Chip"),
            });
    }

    private async void BtnDiskBench_Click(object? sender, RoutedEventArgs e)
    {
        if (_gameBenchRunning) { _gameBenchCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { DiskStatus.Text = "Termine o outro teste antes."; return; }

        string root = CheckedTag(DiskDrivePanel) ?? "";
        if (root.Length == 0) { DiskStatus.Text = "Escolha uma unidade."; return; }
        long fileMb = long.Parse(CheckedTag(DiskSizePanel) ?? "1024");

        _gameBenchCts = new CancellationTokenSource();
        var ct = _gameBenchCts.Token;
        SetGameBenchRunning(BtnDiskBench, true);
        DiskDrivePanel.IsEnabled = DiskSizePanel.IsEnabled = false;
        DiskResult.ItemsSource = null;
        (string Phase, double Frac) progress = ("Preparando…", 0);
        var timer = Every(150, () =>
        {
            DiskStatus.Text = progress.Phase;
            DiskProgress.Value = progress.Frac;
        });

        try
        {
            // Os sensores consultam o driver do disco: pausa durante o teste para não atrapalhar a medição.
            _gpuBenchActive = true;
            var r = await Task.Run(() => DiskBench.Run(root, fileMb, (phase, f) => progress = (phase, f), ct), ct);
            ct.ThrowIfCancellationRequested();

            DiskResult.ItemsSource = new List<MemRow>
            {
                new("Leitura sequencial", $"{r.SeqReadMBs:N0} MB/s", "Blocos de 1 MB. Mede copiar arquivos grandes e carregar jogos."),
                new("Escrita sequencial", $"{r.SeqWriteMBs:N0} MB/s", "Blocos de 1 MB, direto no disco. SSDs com cache SLC caem depois de gravar dezenas de GB."),
                new("Leitura 4 KB aleatória (1 por vez)", $"{r.ReadIopsQ1:N0} IOPS", $"Latência média de {r.ReadLatencyUs:0.0} µs. É o que define a sensação de rapidez do sistema e dos programas."),
                new("Escrita 4 KB aleatória (1 por vez)", $"{r.WriteIopsQ1:N0} IOPS", $"Latência média de {r.WriteLatencyUs:0.0} µs."),
                new("Leitura 4 KB aleatória (8 em paralelo)", $"{r.ReadIopsQ8:N0} IOPS", "Várias leituras ao mesmo tempo, como ao abrir muitos programas."),
            };
            DiskStatus.Text = $"Concluído em {r.Drive} · arquivo de teste ({r.FileMb} MB) apagado";
            SaveBenchRecord("disk", r.Score,
                $"{r.Drive} · {r.SeqReadMBs:N0}/{r.SeqWriteMBs:N0} MB/s · 4K {r.ReadIopsQ1 / 1000:0.#}k IOPS");
        }
        catch (OperationCanceledException) { DiskStatus.Text = "Cancelado (arquivo de teste apagado)"; }
        catch (Exception ex)
        {
            var inner = (ex as AggregateException)?.InnerException ?? ex;
            DiskStatus.Text = $"Falhou: {inner.Message}";
        }
        finally
        {
            _gpuBenchActive = false;
            timer.Stop();
            DiskProgress.Value = 0;
            DiskDrivePanel.IsEnabled = DiskSizePanel.IsEnabled = true;
            SetGameBenchRunning(BtnDiskBench, false);
        }
    }
}

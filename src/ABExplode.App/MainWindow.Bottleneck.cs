using ABExplode.Core;
using ABExplode.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ABExplode.App;

/// <summary>Detector de gargalo: equilíbrio teórico GPU × CPU, leitura ao vivo com veredito e análise de sessão.</summary>
public partial class MainWindow
{
    private IFrameSource? _frames;
    private BottleneckSession? _bnSession;
    private double _balanceRatio = 1;

    /// <summary>Chamado quando a GPU e a CPU já foram identificadas (depois do InitGames, que calcula os índices).</summary>
    private void InitBottleneck()
    {
        _frames = _platform.CreateFrameSource();
        _frames.Start();

        var (ratio, text) = BottleneckAnalyzer.Balance(_gpuIdx, _cpuIdx);
        _balanceRatio = ratio;
        BnCpuName.Text = $"CPU · {GameCatalog.ShortCpuName(_cpuName)}";
        BnGpuName.Text = $"GPU · {GameCatalog.ShortGpuName(_gpuName)}";
        BnBalanceText.Text = text;
        PlaceBalanceDot();
    }

    private void BnBalanceCanvas_SizeChanged(object? sender, SizeChangedEventArgs e) => PlaceBalanceDot();

    /// <summary>Posição do marcador: escala logarítmica da razão GPU/CPU (0,4× … 2,5×), centro = equilibrado.</summary>
    private void PlaceBalanceDot()
    {
        double w = BnBalanceCanvas.Bounds.Width;
        double t = Math.Clamp((Math.Log(_balanceRatio) - Math.Log(0.4)) / (Math.Log(2.5) - Math.Log(0.4)), 0, 1);
        Canvas.SetLeft(BnBalanceDot, t * Math.Max(0, w - BnBalanceDot.Width));
    }

    private static MeterItem Meter(string label, double? pct, string? suffix = null)
        => new(label, pct == null ? "—" : $"{pct:0} %{suffix}", Math.Clamp((pct ?? 0) / 100, 0, 1));

    /// <summary>A cada segundo: veredito ao vivo (com a página aberta) e coleta da sessão em andamento.</summary>
    private void UpdateBottleneck()
    {
        if (_frames == null || (!PageBottleneck.IsVisible && _bnSession == null)) return;
        var frames = _frames.GetForegroundStats();
        var s = BottleneckSampler.Sample(_monitor, _gpus.FirstOrDefault(), frames);
        var lim = s.Classify();

        BnLiveTitle.Text = BottleneckAnalyzer.Title(lim);
        BnLiveAdvice.Text = BottleneckAnalyzer.Advice(lim);
        BnLiveGame.Text = frames != null
            ? $"{frames.ProcessName} · {frames.Fps:0} FPS · 1% low {frames.Low1Fps:0}"
            : "Nenhum jogo em primeiro plano (o FPS aparece quando o jogo estiver na tela)";
        bool alert = lim is Limiter.Cpu or Limiter.Vram or Limiter.Ram or Limiter.Thermal;
        BnLiveCard.Background = Res(alert ? "Fg" : "Card");
        BnLiveTitle.Foreground = Res(alert ? "Ink" : "Fg");
        BnLiveLabel.Foreground = BnLiveGame.Foreground = BnLiveAdvice.Foreground = Res(alert ? "Ink" : "Muted");

        BnMeters.ItemsSource = new[]
        {
            Meter("Carga da GPU", s.GpuLoad),
            Meter("Carga total da CPU", s.CpuTotal),
            Meter(s.BusiestCore != null ? $"Núcleo mais ocupado ({s.BusiestCore})" : "Núcleo mais ocupado", s.CpuMaxCore),
            Meter("VRAM em uso", s.VramPct),
            Meter("RAM em uso", s.RamPct),
            new MeterItem("Temperaturas (GPU / CPU)",
                          $"{(s.GpuTemp is float g ? $"{g:0} °C" : "—")} / {(s.CpuTemp is float c ? $"{c:0} °C" : "—")}",
                          Math.Clamp(Math.Max(s.GpuTemp ?? 0, s.CpuTemp ?? 0) / 100.0, 0, 1)),
        };

        if (_bnSession != null)
        {
            _bnSession.Add(s);
            var el = DateTime.Now - _bnSession.Start;
            BnSessionStatus.Text = $"Coletando… {el:m\\:ss} de {_bnSession.Duration:m\\:ss} · {_bnSession.Count} amostras · " +
                                   (frames != null ? $"jogo detectado: {frames.ProcessName}" : "abra o jogo agora");
            if (_bnSession.Done) FinishBottleneckSession();
        }
    }

    private void BtnBnSession_Click(object? sender, RoutedEventArgs e)
    {
        if (_bnSession != null)
        {
            _bnSession = null;
            SetRunningButton(BtnBnSession, false, "INICIAR ANÁLISE");
            BnSessionStatus.Text = "Análise cancelada.";
            return;
        }
        int minutes = int.Parse(CheckedTag(BnDurationPanel) ?? "1");
        _bnSession = new BottleneckSession { Duration = TimeSpan.FromMinutes(minutes) };
        BnResult.IsVisible = false;
        SetRunningButton(BtnBnSession, true, "CANCELAR");
        BnSessionStatus.Text = "Coletando… abra o jogo agora.";
    }

    private void FinishBottleneckSession()
    {
        var sess = _bnSession!;
        _bnSession = null;
        SetRunningButton(BtnBnSession, false, "INICIAR ANÁLISE");
        BnSessionStatus.Text = $"Análise concluída às {DateTime.Now:HH:mm} ({sess.Count} amostras).";

        var main = sess.Main ?? Limiter.Capped;
        BnResultTitle.Text = $"Resultado: {BottleneckAnalyzer.Title(main)}";
        var parts = new List<string>();
        if (sess.AvgFps is double fps) parts.Add($"FPS médio {fps:0}");
        if (sess.Avg(x => x.GpuLoad) is double g) parts.Add($"GPU média {g:0} %");
        if (sess.Avg(x => x.CpuTotal) is double c) parts.Add($"CPU média {c:0} %");
        if (sess.Avg(x => x.CpuMaxCore) is double k) parts.Add($"núcleo mais ocupado {k:0} %");
        if (sess.Max(x => x.VramPct) is double v) parts.Add($"VRAM máx. {v:0} %");
        if (sess.Max(x => x.GpuTemp) is double gt) parts.Add($"GPU máx. {gt:0} °C");
        BnResultSummary.Text = string.Join(" · ", parts);
        BnDistribution.ItemsSource = sess.Distribution()
            .Select(d => new MeterItem($"{BottleneckAnalyzer.Title(d.Limiter)} — % do tempo", $"{d.Pct:0} %", d.Pct / 100)).ToList();
        BnResultAdvice.Text = BottleneckAnalyzer.Advice(main);
        BnResult.IsVisible = true;
    }
}

using System.Diagnostics;
using ABExplode.Core;
using ABExplode.Platform;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
#if WINDOWS
using ABExplode.Platform.Windows;
#endif

namespace ABExplode.App;

/// <summary>Passo do guia de overclock na BIOS.</summary>
public sealed record GuideStep(string Number, string Title, string Where, string Text);

/// <summary>RESET OC: ajustes da GPU NVIDIA (NVML), verificador de estabilidade e guia de overclock na BIOS.</summary>
public partial class MainWindow
{
    private uint? _ocClockCap;     // teto aplicado (null = sem teto)
    private uint? _ocPendingCap;   // teto escolhido nos chips, ainda não aplicado
    private CancellationTokenSource? _ocCts;
    private bool _ocRunning, _ocInited;

    private IntPtr OcGpu => _gpus.Count > 0 ? _gpus[0].NvmlHandle : IntPtr.Zero;

    /// <summary>Chamado quando a GPU e a CPU já foram identificadas.</summary>
    private void InitOc()
    {
        if (_ocInited) return;
        _ocInited = true;
        var h = OcGpu;
        OcGpuTitle.Text = _gpus.Count > 0 ? "GPU · " + GameCatalog.ShortGpuName(_gpus[0].Name).ToUpperInvariant() : "GPU";
        if (h == IntPtr.Zero)
        {
            OcGpuState.Text = "Ajustes disponíveis só para placas NVIDIA (via driver oficial).";
            OcPower.IsEnabled = BtnOcApply.IsEnabled = BtnOcReset.IsEnabled = false;
        }
        else
        {
            if (Nvml.PowerConstraintsMw(h) is var (min, max))
            {
                OcPower.Minimum = min / 1000.0;
                OcPower.Maximum = max / 1000.0;
            }
            if (Nvml.PowerLimitMw(h) is uint limit) OcPower.Value = limit / 1000.0;
            uint maxClock = Nvml.MaxClock(h, Nvml.ClockGraphics) ?? 3000;
            Chip("Sem teto", null, on: true);
            Chip($"Leve · {maxClock - 300} MHz", maxClock - 300, on: false);
            Chip($"Equilibrado · {maxClock - 500} MHz", maxClock - 500, on: false);
            Chip($"Econômico · {maxClock - 800} MHz", maxClock - 800, on: false);
            UpdateOcGpuState();
        }
        OcPowerValue.Text = $"{OcPower.Value:0} W";
#if !WINDOWS
        // TODO(Fase 6): verificação de GPU (cena + stress) em Vulkan.
        foreach (var rb in OcTargetPanel.Children.OfType<RadioButton>().Where(r => (string?)r.Tag != "cpu"))
        {
            rb.IsEnabled = false;
            ToolTip.SetTip(rb, "Ainda não disponível neste sistema.");
        }
#endif
        BuildOcGuide();
        UpdateOcHistory();
        UpdateRecentErrors();

        void Chip(string label, uint? cap, bool on)
        {
            var rb = new RadioButton
            {
                Content = label, GroupName = "OcClock", IsChecked = on,
                Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("Chip"),
            };
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) _ocPendingCap = cap; };
            OcClockPanel.Children.Add(rb);
        }
    }

    private void UpdateOcGpuState()
    {
        var h = OcGpu;
        if (h == IntPtr.Zero) return;
        string W(uint? mw) => mw is uint v ? $"{v / 1000.0:0} W" : "—";
        OcGpuState.Text = $"Limite de energia atual {W(Nvml.PowerLimitMw(h))} (padrão {W(Nvml.PowerDefaultMw(h))}) · teto de clock: " +
                          (_ocClockCap is uint cap ? $"{cap} MHz" : "nenhum") +
                          " · clock máximo atual: " + (Nvml.Clock(h, Nvml.ClockGraphics) is uint c ? $"{c} MHz" : "—");
    }

    private void OcPower_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (OcPowerValue != null) OcPowerValue.Text = $"{OcPower.Value:0} W";
    }

    private async Task<bool> RequireAdmin()
    {
        if (_platform.IsElevated) return true;
        await Dialog.Show(this, "ABExplode", "Esta ação precisa do ABExplode aberto como administrador.");
        return false;
    }

    private async void BtnOcApply_Click(object? sender, RoutedEventArgs e)
    {
        var h = OcGpu;
        if (h == IntPtr.Zero || !await RequireAdmin()) return;
        var parts = new List<string>();
        string? powerErr = Nvml.Explain(Nvml.SetPowerLimitMw(h, (uint)(OcPower.Value * 1000)));
        parts.Add(powerErr != null ? $"Energia: não aplicado ({powerErr})." : $"Energia: {OcPower.Value:0} W aplicado.");

        int clockCode = _ocPendingCap is uint cap ? Nvml.LockGpuClocks(h, 210, cap) : Nvml.ResetGpuClocks(h);
        if (Nvml.Explain(clockCode) is string clockErr) parts.Add($"Teto de clock: não aplicado ({clockErr}).");
        else
        {
            _ocClockCap = _ocPendingCap;
            parts.Add(_ocClockCap is uint applied ? $"Teto de clock: {applied} MHz aplicado." : "Teto de clock: removido.");
        }
        OcGpuResult.Text = string.Join(" ", parts) + " Rode o verificador para confirmar a estabilidade.";
        UpdateOcGpuState();
    }

    private async void BtnOcReset_Click(object? sender, RoutedEventArgs e)
    {
        var h = OcGpu;
        if (h == IntPtr.Zero || !await RequireAdmin()) return;
        int powerCode = Nvml.PowerDefaultMw(h) is uint def ? Nvml.SetPowerLimitMw(h, def) : 3; // 3 = não suportado
        int clockCode = Nvml.ResetGpuClocks(h);
        if (Nvml.PowerDefaultMw(h) is uint d) OcPower.Value = d / 1000.0;
        foreach (var rb in OcClockPanel.Children.OfType<RadioButton>())
            rb.IsChecked = rb.Content as string == "Sem teto";
        if (clockCode == 0) _ocClockCap = null;
        string? err = Nvml.Explain(powerCode) ?? Nvml.Explain(clockCode);
        OcGpuResult.Text = err != null ? $"Restauração parcial: {err}." : "GPU de volta ao padrão de fábrica.";
        UpdateOcGpuState();
    }

    private void UpdateRecentErrors()
    {
        var errs = _platform.HardwareErrors.ErrorsSince(DateTime.Now.AddDays(-7));
        OcRecentErrors.Text = errs.Error != null
            ? "Não foi possível ler o registro: " + errs.Error
            : errs.Total == 0
                ? "Nenhum erro de hardware nem reinício do driver de vídeo nos últimos 7 dias."
                : $"Últimos 7 dias: {errs.Whea} erros WHEA · {errs.GpuResets} reinícios do driver de vídeo.\nÚltimo: {errs.LastDetail}";
    }

    private void UpdateOcHistory() =>
        OcHistory.ItemsSource = _settings.BenchRecords.Where(r => r.Test == "oc")
                                         .Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, r.Score)).ToList();

    private async void BtnOcCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (_ocRunning) { _ocCts?.Cancel(); return; }
        if (TestRunning || AnyBenchRunning) { OcStatus.Text = "Termine o outro teste antes."; return; }

        string target = CheckedTag(OcTargetPanel) ?? "cpu";
        bool cpu = target is "cpu" or "both", gpu = target is "gpu" or "both";
        var duration = TimeSpan.FromMinutes(int.Parse(CheckedTag(OcDurPanel) ?? "15"));
        string label = OcLabel.Text?.Trim() is { Length: > 0 } l ? l : "sem descrição";

        _ocCts = new CancellationTokenSource();
        var ct = _ocCts.Token;
        _ocRunning = true;
        SetRunningButton(BtnOcCheck, true, "CANCELAR");
        OcVerdictCard.IsVisible = false;
        OcTargetPanel.IsEnabled = OcDurPanel.IsEnabled = false;

        var start = DateTime.Now;
        double? cpuScore = null, gpuScore = null;
        float maxCpu = 0, maxGpu = 0;
        double sumCpuW = 0, sumGpuW = 0;
        int nCpuW = 0, nGpuW = 0;
        string? fail = null;
        var stress = new StressTester(_platform.SystemInfo.FreePhysicalBytes);
#if WINDOWS
        var burn = new GpuBurn();
#endif
        try
        {
            if (cpu)
            {
                OcStatus.Text = "Medindo a nota da CPU (render multi-core)…";
                var r = await Task.Run(() => _bench.Run(Environment.ProcessorCount, TimeSpan.Zero, ct), ct);
                if (!ct.IsCancellationRequested) cpuScore = r.Score;
            }
#if WINDOWS
            if (gpu && !ct.IsCancellationRequested)
            {
                OcStatus.Text = "Medindo a nota da GPU (cena de jogo, 30 s)…";
                var gb = new GpuGameBench { Duration = TimeSpan.FromSeconds(30), AdapterName = _gpus.Count > 0 ? _gpus[0].Name : "" };
                _gpuBenchActive = true;
                using (ct.Register(gb.Cancel))
                {
                    var gr = await Task.Factory.StartNew(gb.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    if (gr != null) gpuScore = gr.Score;
                    else if (!gb.Cancelled) fail = $"a cena de GPU falhou ({gb.Error})";
                }
                _gpuBenchActive = false;
            }
#endif
            if (fail == null && !ct.IsCancellationRequested)
            {
                if (cpu) stress.Start(cpu: true, ram: true);
#if WINDOWS
                if (gpu)
                    burn.Start(new GpuBurnSettings
                    {
                        Width = 1920, Height = 1080, Msaa = 4, Shells = 128, Iterations = 48,
                        AdapterName = _gpus.Count > 0 ? _gpus[0].Name : ""
                    });
#endif
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < duration && !ct.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                    float? ct1 = _monitor.CpuTemperature, gt1 = MainGpuTemp();
                    if (ct1 is float c1) maxCpu = Math.Max(maxCpu, c1);
                    if (gt1 is float g1) maxGpu = Math.Max(maxGpu, g1);
                    if (cpu && _monitor.CpuPower is float cw) { sumCpuW += cw; nCpuW++; }
                    if (gpu && MainGpuPower() is float gw) { sumGpuW += gw; nGpuW++; }
                    OcProgress.Value = sw.Elapsed / duration;
                    OcStatus.Text = $"Stress {(cpu && gpu ? "CPU + RAM + GPU" : cpu ? "CPU + RAM" : "GPU")} · {sw.Elapsed:mm\\:ss} de {duration:mm\\:ss} · " +
                                    $"máx. CPU {(maxCpu > 0 ? $"{maxCpu:0} °C" : "—")} · máx. GPU {(maxGpu > 0 ? $"{maxGpu:0} °C" : "—")}";

                    if (cpu && ct1 >= 95) { fail = $"CPU chegou a {ct1:0} °C (limite de segurança 95 °C)"; break; }
                    if (gpu && gt1 >= 88) { fail = $"GPU chegou a {gt1:0} °C (limite de segurança 88 °C)"; break; }
                    if (cpu && stress.MemoryErrors > 0) { fail = $"{stress.MemoryErrors} erros de memória (RAM/EXPO instável)"; break; }
#if WINDOWS
                    if (gpu && !burn.Running)
                    {
                        fail = burn.Error != null ? $"a GPU falhou ({burn.Error})" : "a janela do teste de GPU foi fechada";
                        break;
                    }
#endif
                    if ((int)sw.Elapsed.TotalSeconds % 5 == 0 && _platform.HardwareErrors.ErrorsSince(start) is { Total: > 0 } he)
                    {
                        fail = $"o sistema registrou {he.Whea} erros WHEA e {he.GpuResets} reinícios do driver de vídeo";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            stress.Stop();
#if WINDOWS
            burn.Stop();
#endif
            _gpuBenchActive = false;
        }

        _ocRunning = false;
        SetRunningButton(BtnOcCheck, false, "INICIAR VERIFICAÇÃO");
        OcTargetPanel.IsEnabled = OcDurPanel.IsEnabled = true;
        OcProgress.Value = 0;
        if (ct.IsCancellationRequested && fail == null) { OcStatus.Text = "Verificação cancelada."; return; }

        var errs = _platform.HardwareErrors.ErrorsSince(start);
        if (fail == null && errs.Total > 0)
            fail = $"o sistema registrou {errs.Whea} erros WHEA e {errs.GpuResets} reinícios do driver de vídeo";
        bool stable = fail == null;

        OcStatus.Text = $"Concluído em {DateTime.Now - start:mm\\:ss}.";
        OcVerdict.Text = stable ? "ESTÁVEL" : "INSTÁVEL";
        OcVerdictWhy.Text = stable
            ? $"Nenhum erro em {duration.TotalMinutes:0} min de stress. Para Curve Optimizer, use o PC normalmente por um dia e confira os erros recentes."
            : $"Motivo: {fail}. Volte um passo no ajuste e teste de novo.";
        OcVerdictCard.Background = Res(stable ? "Card" : "Fg");
        OcVerdict.Foreground = OcVerdictWhy.Foreground = OcResult.Foreground = Res(stable ? "Fg" : "Ink");

        var rows = new List<KeyValuePair<string, string>> { new("Configuração", label) };
        if (cpuScore is double cs) rows.Add(new("Nota CPU (render)", $"{cs:N0} pts"));
        if (gpuScore is double gs) rows.Add(new("Nota GPU (cena)", $"{gs:N0} pts"));
        if (maxCpu > 0) rows.Add(new("Temp. máx. CPU", $"{maxCpu:0} °C"));
        if (maxGpu > 0) rows.Add(new("Temp. máx. GPU", $"{maxGpu:0} °C"));
        if (nCpuW > 0) rows.Add(new("Consumo médio CPU", $"{sumCpuW / nCpuW:0} W"));
        if (nGpuW > 0) rows.Add(new("Consumo médio GPU", $"{sumGpuW / nGpuW:0} W"));
        rows.Add(new("Erros WHEA / resets de vídeo", $"{errs.Whea} / {errs.GpuResets}"));
        OcResult.ItemsSource = rows;
        OcVerdictCard.IsVisible = true;

        string summary = (stable ? "estável" : "INSTÁVEL") + " · " + label +
                         (cpuScore is double c2 ? $" · CPU {c2:N0}" : "") + (gpuScore is double g2 ? $" · GPU {g2:N0}" : "");
        _settings.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, "oc", stable ? 1.0 : 0.15, summary));
        _settings.Save();
        UpdateOcHistory();
        UpdateRecentErrors();
    }

    private void BuildOcGuide()
    {
        var (maker, model) = _platform.HardwareErrors.Motherboard();
        bool gigabyte = maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase);
        bool asus = maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase);
        bool msi = maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || maker.Contains("MSI", StringComparison.OrdinalIgnoreCase);
        string cpuShort = GameCatalog.ShortCpuName(_cpuName);
        bool ryzen = _cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        string board = ((gigabyte ? "Gigabyte" : maker.Split(' ').FirstOrDefault() ?? "") + " " + model).Trim();

        OcGuideTitle.Text = "GUIA: OVERCLOCK NA BIOS · " + board.ToUpperInvariant() + " · " + cpuShort.ToUpperInvariant();
        OcGuideIntro.Text = "Faça uma mudança por vez e rode o verificador depois de cada uma. Os menus podem mudar um pouco com a versão da BIOS. " +
                            "Valores são pontos de partida seguros, não garantia: cada chip é diferente.";
        string pbo = gigabyte ? "Tweaker → Advanced CPU Settings → Precision Boost Overdrive  (ou Settings → AMD Overclocking)"
                   : asus ? "Advanced → AMD Overclocking → Precision Boost Overdrive  (ou AI Tweaker → PBO)"
                   : msi ? "OC → Advanced CPU Configuration → AMD Overclocking → Precision Boost Overdrive"
                   : "Advanced → AMD Overclocking → Precision Boost Overdrive";
        string expo = gigabyte ? "Tweaker → Extreme Memory Profile (X.M.P.) → EXPO1"
                    : asus ? "AI Tweaker → Ai Overclock Tuner → EXPO I"
                    : msi ? "OC → A-XMP/EXPO → Profile 1"
                    : "procure EXPO / XMP no menu de memória";

        var steps = new List<GuideStep>
        {
            new("0", "Antes de começar", gigabyte ? "Save & Exit → Save Profiles" : "Save & Exit → Save Profile",
                "Salve o perfil atual da BIOS. Se o PC não ligar depois de um ajuste, espere 3 tentativas (muitas placas voltam sozinhas) " +
                "ou limpe a CMOS (jumper CLR_CMOS na placa ou tirando a bateria por 1 minuto)."),
            new("1", "Ative o perfil da memória (EXPO)", expo,
                "É o maior ganho em jogos para a maioria dos PCs: a RAM sai do padrão lento e vai para a velocidade da etiqueta. " +
                "Teste: verificador \"CPU + RAM\" por 15 min (o teste de RAM acusa erro se o perfil não for estável)."),
        };
        if (ryzen)
        {
            steps.Add(new("2", "Precision Boost Overdrive (PBO)", pbo,
                "Precision Boost Overdrive → Advanced. PBO Limits → Motherboard. Isso libera os limites de energia para o boost durar mais. Teste com o verificador."));
            steps.Add(new("3", "Curve Optimizer (undervolt da CPU)", pbo + " → Curve Optimizer",
                "All Cores → Negative → 10. Teste. Se passar, tente 15, 20, 25… até falhar; volte 5 a partir de onde falhou. No " + cpuShort +
                " costuma ficar entre -15 e -30. Instabilidade de Curve Optimizer aparece muito em uso leve: use o PC normalmente por um dia e veja " +
                "\"Erros de hardware recentes\" aqui — erro WHEA = volte um passo."));
            steps.Add(new("4", "Max CPU Boost Clock Override (opcional)", pbo,
                "Depois do Curve Optimizer estável: +100 MHz, teste; depois +200 MHz. Ganho pequeno; pode exigir reduzir o Curve Optimizer."));
            steps.Add(new("5", "Alternativa fria: Eco Mode", pbo + " → Eco Mode",
                "Em vez de mais desempenho: limita a CPU a 65 W (ou 105 W). Perde ~5–10% em tarefas pesadas multi-core, quase nada em jogos, e roda bem mais frio e silencioso."));
        }
        else
        {
            steps.Add(new("2", "Processador Intel", "Tweaker / Ai Tweaker / OC → CPU",
                "Para Intel, prefira undervolt (offset negativo de tensão) e limites de energia (PL1/PL2) em vez de aumentar multiplicadores. Teste cada passo com o verificador."));
        }
        OcGuide.ItemsSource = steps;
    }
}

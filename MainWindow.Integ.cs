using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Item da lista de dispositivos de iluminação (caixa de seleção + nome).</summary>
public sealed class LightDeviceVm
{
    public CorsairDevice Device { get; init; } = null!;
    public string Name => string.IsNullOrWhiteSpace(Device.Model) ? Device.TypeName : Device.Model;
    public string Detail => $"{Device.TypeName} · {Device.LedCount} LEDs";
    public bool Selected { get; set; } = true;
}

/// <summary>Aba RESET INTEGRAÇÃO: controle de iluminação e efeitos (por enquanto Corsair iCUE / iCUE LINK).</summary>
public partial class MainWindow
{
    private CorsairSdk? _cue;
    private List<LightDeviceVm> _lightVms = new();
    private DispatcherTimer? _lightTimer;
    private bool _lightRunning, _lightBusy, _lightInited;
    private string _effect = "static";
    private (byte R, byte G, byte B) _lightColor = (255, 255, 255);
    private readonly Stopwatch _lightClock = Stopwatch.StartNew();
    private readonly Dictionary<string, ((byte, byte, byte)[] Colors, long At)> _lightLast = new();
    private float _smoothValue = float.NaN;
    private int _lastLightError;
    private readonly Dictionary<string, int> _lightErrByDevice = new();
    private readonly Queue<string> _diag = new();

    /// <summary>Linha do painel de diagnóstico da aba (últimas 80 linhas).</summary>
    private void Diag(string line)
    {
        _diag.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_diag.Count > 80) _diag.Dequeue();
        if (LightDiag != null) { LightDiag.Text = string.Join("\n", _diag); LightDiag.ScrollToEnd(); }
    }

    private static readonly (string Name, byte R, byte G, byte B)[] Swatches =
    {
        ("Branco", 255, 255, 255), ("Vermelho", 255, 0, 0), ("Laranja", 255, 110, 0), ("Amarelo", 255, 210, 0),
        ("Verde", 0, 255, 60), ("Ciano", 0, 220, 255), ("Azul", 0, 80, 255), ("Roxo", 150, 0, 255), ("Rosa", 255, 0, 160),
    };

    private void InitInteg()
    {
        if (!_lightInited)
        {
            _lightInited = true;
            foreach (var (name, r, g, b) in Swatches)
            {
                var btn = new Button
                {
                    Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = name, BorderBrush = (Brush)FindResource("LineStrong"), BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)),
                    Tag = (r, g, b),
                    Template = SwatchTemplate(),
                };
                btn.Click += (_, _) => { var (rr, gg, bb) = ((byte, byte, byte))btn.Tag; LightR.Value = rr; LightG.Value = gg; LightB.Value = bb; };
                LightSwatches.Children.Add(btn);
            }
            _lightTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _lightTimer.Tick += LightTick;
            LightColor_Changed(null, null!);
            _ = ConnectCueAsync(); // primeira abertura da aba: tenta conectar sozinho
        }
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private async Task<bool> ConnectCueAsync()
    {
        if (_lightBusy) return false;
        _lightBusy = true;
        BtnLightConnect.IsEnabled = false;
        try
        {
            if (_cue == null)
            {
                _cue = new CorsairSdk();
                _cue.StateChanged += s => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (s != 6 && _lightRunning) { StopLighting(false); LightStatus.Text = "O iCUE desconectou (" + CorsairSdk.StateText(s) + "). Clique em CONECTAR."; }
                }));
            }
            string? dllProblem = _cue.LoadDll();
            BtnLightSdk.Visibility = dllProblem != null ? Visibility.Visible : Visibility.Collapsed;
            if (dllProblem != null)
            {
                LightStatus.Text = "O SDK da Corsair (iCUESDK.x64_2019.dll) ainda não está instalado. Clique em BAIXAR O SDK DA CORSAIR: "
                                   + "o programa baixa só essa DLL do repositório oficial da Corsair e a instala para você.";
                return false;
            }

            if (_cue.State != 6)
            {
                LightStatus.Text = "Conectando ao iCUE…";
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnState(int s) { if (s == 6 || s == 3 || s == 4) tcs.TrySetResult(s); }
                _cue.StateChanged += OnState;
                int err = _cue.Connect();
                if (err != 0 || _cue.State == 6) tcs.TrySetResult(_cue.State);
                await Task.WhenAny(tcs.Task, Task.Delay(6000));
                _cue.StateChanged -= OnState;
                if (_cue.State != 6)
                {
                    LightStatus.Text = "Não consegui conectar ao iCUE (" + (err != 0 ? CorsairSdk.ErrorText(err) : CorsairSdk.StateText(_cue.State)) + "). " +
                        "Abra o iCUE, vá em Configurações e ative o SDK (Enable SDK). Se o iCUE estiver em outra conta do Windows ou com permissão maior que a do ResetExplode, a conexão também falha.";
                    return false;
                }
            }

            string? prob = null;
            var devs = await Task.Run(() => _cue.ReadDevices(out prob));
            if (prob != null) { LightStatus.Text = prob; return false; }

            var keep = _lightVms.Where(v => !v.Selected).Select(v => v.Device.Id).ToHashSet();
            _lightVms = devs.Select(d => new LightDeviceVm { Device = d, Selected = !keep.Contains(d.Id) }).ToList();
            LightDevices.ItemsSource = _lightVms;
            Diag($"DLL: {_cue.LoadedFrom} · sessão: {CorsairSdk.StateText(_cue.State)} · {devs.Count} dispositivo(s)");
            foreach (var d in devs)
            {
                string idShort = d.Id.Length > 14 ? d.Id[..14] + "…" : d.Id;
                string first = d.LedCount > 0 ? $"LED[0]=#{d.LedIds[0]} x={d.LedX[0]:0.00}" : "sem LEDs lidos";
                first += " · " + d.LedReadInfo;
                Diag($"  {d.TypeName} \"{d.Model}\" id={idShort} leds(ficha)={d.InfoLedCount} leds(posições)={d.LedCount} canais={d.ChannelCount} {first}");
            }
            LightStatus.Text = devs.Count == 0
                ? "Conectado ao iCUE, mas nenhum dispositivo foi listado. Confira se o iCUE LINK System Hub aparece no iCUE."
                : $"Conectado ao iCUE · {devs.Count} dispositivo(s), {devs.Sum(d => d.LedCount)} LEDs. Escolha o efeito e clique em ATIVAR CONTROLE.";
            return devs.Count > 0;
        }
        catch (Exception ex) { LightStatus.Text = "Falhou: " + ex.Message; return false; }
        finally { _lightBusy = false; BtnLightConnect.IsEnabled = true; }
    }

    private async void BtnLightSdk_Click(object sender, RoutedEventArgs e)
    {
        if (_lightBusy) return;
        var ok = MessageBox.Show(this,
            "O RESET EXPLODE vai baixar o SDK oficial da Corsair (github.com/CorsairOfficial/cue-sdk), extrair somente o arquivo " +
            "iCUESDK.x64_2019.dll para a pasta\n" + CorsairSdkInstaller.TargetPath + "\ne conferir a assinatura digital dele.\n\nContinuar?",
            "RESET EXPLODE · SDK da Corsair", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        _lightBusy = true;
        BtnLightSdk.IsEnabled = false;
        BtnLightConnect.IsEnabled = false;
        try
        {
            string msg = await CorsairSdkInstaller.InstallAsync(t => Dispatcher.BeginInvoke(new Action(() => LightStatus.Text = t)));
            LightStatus.Text = msg;
            BtnLightSdk.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { LightStatus.Text = "Não foi possível instalar o SDK: " + ex.Message; return; }
        finally
        {
            _lightBusy = false;
            BtnLightSdk.IsEnabled = true;
            BtnLightConnect.IsEnabled = true;
        }
        await ConnectCueAsync();
    }

    private async void BtnLightConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_lightRunning) StopLighting(true);
        await ConnectCueAsync();
    }

    private void BtnLightRun_Click(object sender, RoutedEventArgs e)
    {
        if (_lightRunning) { StopLighting(true); return; }
        if (_cue == null || _cue.State != 6) { LightStatus.Text = "Conecte ao iCUE primeiro."; return; }
        var sel = _lightVms.Where(v => v.Selected && v.Device.LedCount > 0).ToList();
        if (sel.Count == 0) { LightStatus.Text = "Marque pelo menos um dispositivo com LEDs."; return; }

        var problems = new List<string>();
        int level = LightExclusive.IsChecked == true ? 1 : 0;
        int pr = _cue.SetLayerPriority(255);
        Diag($"Prioridade da camada 255: {(pr == -1 ? "função ausente nesta versão do SDK" : CorsairSdk.ErrorText(pr))} · modo {(level == 1 ? "exclusivo" : "compartilhado")}");
        _lightErrByDevice.Clear();
        foreach (var v in sel)
        {
            int err = _cue.RequestControl(v.Device, level);
            Diag($"Pedido de controle de \"{v.Name}\": {CorsairSdk.ErrorText(err)}");
            if (err != 0) problems.Add($"{v.Name}: {CorsairSdk.ErrorText(err)}");
        }
        _lightLast.Clear();
        _smoothValue = float.NaN;
        _lightRunning = true;
        BtnLightRun.Content = "DEVOLVER AO iCUE";
        LightStatus.Text = problems.Count == 0
            ? $"Controlando {sel.Count} dispositivo(s). Feche ou devolva para o iCUE reassumir as luzes."
            : "Controle ativo, mas com avisos: " + string.Join("; ", problems);
        _lightTimer!.Start();
    }

    private void StopLighting(bool release)
    {
        _lightTimer?.Stop();
        _lightRunning = false;
        BtnLightRun.Content = "ATIVAR CONTROLE";
        LightLive.Text = "";
        if (release && _cue != null && _cue.State == 6)
        {
            foreach (var v in _lightVms) { try { _cue.ReleaseControl(v.Device); } catch { } }
            LightStatus.Text = "Controle devolvido ao iCUE.";
        }
    }

    private void ShutdownInteg()
    {
        try { StopLighting(true); } catch { }
        try { _cue?.Dispose(); } catch { }
    }

    private void LightFx_Checked(object sender, RoutedEventArgs e)
    {
        if (LightLive == null) return;
        _effect = (string)((FrameworkElement)sender).Tag;
        _smoothValue = float.NaN;
        _lightLast.Clear();
    }

    private void LightColor_Changed(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LightLive == null) return;
        _lightColor = ((byte)LightR.Value, (byte)LightG.Value, (byte)LightB.Value);
        LightPreview.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(_lightColor.R, _lightColor.G, _lightColor.B));
        LightBrightText.Text = $"{LightBright.Value:0}%";
        LightSpeedText.Text = $"{LightSpeed.Value:0}";
        _lightLast.Clear();
    }

    // ---------------- Efeitos ----------------

    private static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private float? GpuTemperature()
    {
        var r = _monitor.Rows.Where(x => (x.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                                         && x.Type == SensorType.Temperature && x.RawValue is > 0).ToList();
        return (r.FirstOrDefault(x => x.Name.Contains("Core")) ?? r.FirstOrDefault())?.RawValue;
    }

    /// <summary>Valor ao vivo que dirige o efeito (temperatura ou carga), já suavizado. Null se o sensor não responde.</summary>
    private float? LiveValue()
    {
        float? raw = _effect switch
        {
            "cpu" => _monitor.CpuTemperature,
            "gpu" => GpuTemperature(),
            "load" => _monitor.CpuLoad,
            _ => null
        };
        if (raw == null) return null;
        _smoothValue = float.IsNaN(_smoothValue) ? raw.Value : _smoothValue + (raw.Value - _smoothValue) * 0.08f;
        return _smoothValue;
    }

    private void LightTick(object? sender, EventArgs e)
    {
        if (_cue == null || _cue.State != 6) return;
        double t = _lightClock.Elapsed.TotalSeconds;
        double bright = LightBright.Value / 100.0;
        double speed = LightSpeed.Value;
        var (cr, cg, cb) = _lightColor;

        // cor única ao vivo (temperatura/carga) calculada uma vez por quadro
        (byte, byte, byte)? live = null;
        if (_effect is "cpu" or "gpu" or "load")
        {
            float? v = LiveValue();
            if (v == null) { LightLive.Text = "Sensor indisponível."; live = (0, 0, 40); }
            else
            {
                double hue;
                if (_effect == "load") { hue = 120 * (1 - Math.Clamp(v.Value / 100.0, 0, 1)); LightLive.Text = $"CPU {v:0}% de carga"; }
                else { hue = 240 * (1 - Math.Clamp((v.Value - 35) / 50.0, 0, 1)); LightLive.Text = $"{(_effect == "cpu" ? "CPU" : "GPU")} {v:0} °C"; }
                live = Hsv(hue, 1, 1);
            }
        }
        else LightLive.Text = "";

        long now = Environment.TickCount64;
        foreach (var vm in _lightVms)
        {
            if (!vm.Selected || vm.Device.LedCount == 0) continue;
            var d = vm.Device;
            var cols = new (byte, byte, byte)[d.LedCount];
            for (int i = 0; i < cols.Length; i++)
            {
                (byte R, byte G, byte B) c;
                switch (_effect)
                {
                    case "breath":
                        double k = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (0.1 * speed) * t);
                        k = 0.04 + 0.96 * k;
                        c = ((byte)(cr * k), (byte)(cg * k), (byte)(cb * k));
                        break;
                    case "rainbow":
                        c = Hsv(d.LedX[i] * 360 + t * speed * 36, 1, 1);
                        break;
                    case "cpu": case "gpu": case "load":
                        c = live ?? (0, 0, 0);
                        break;
                    case "off":
                        c = (0, 0, 0);
                        break;
                    default:
                        c = (cr, cg, cb);
                        break;
                }
                cols[i] = ((byte)(c.R * bright), (byte)(c.G * bright), (byte)(c.B * bright));
            }

            // só reenvia se mudou (ou a cada 2 s, para o iCUE não retomar as luzes)
            if (_lightLast.TryGetValue(d.Id, out var last) && now - last.At < 2000 && last.Colors.AsSpan().SequenceEqual(cols)) continue;
            int err = _cue.SetColors(d, cols);
            _lightLast[d.Id] = (cols, now);
            if (!_lightErrByDevice.TryGetValue(d.Id, out int prevErr) || prevErr != err)
            {
                _lightErrByDevice[d.Id] = err;
                Diag($"Cores enviadas a \"{vm.Name}\" ({cols.Length} LEDs, primeiro = {cols[0].Item1},{cols[0].Item2},{cols[0].Item3}): {CorsairSdk.ErrorText(err)}");
            }
            if (err != 0 && err != _lastLightError)
            {
                LightStatus.Text = $"{vm.Name}: {CorsairSdk.ErrorText(err)}";
            }
            _lastLightError = err;
        }
    }
}

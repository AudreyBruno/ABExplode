using System.Globalization;
using System.Runtime.InteropServices;
using ABExplode.Core;
using ABExplode.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ABExplode.App;

/// <summary>
/// Overlay: preferências (canto, tamanho, o que mostrar), a janela sobre o jogo e o atalho global Ctrl+Shift+O.
/// O atalho usa RegisterHotKey, só no Windows; no Linux (Wayland não tem atalho global para apps) liga pela tela.
/// </summary>
public partial class MainWindow
{
    private OverlayWindow? _overlay;
    private DispatcherTimer? _overlayTimer;
    private bool _overlayReady;
    private HashSet<string>? _ovShow;
    private HashSet<string> OvShow => _ovShow ??= _settings.OverlayItems is { } l ? l.ToHashSet() : OverlayMetrics.Defaults();

    /// <summary>Preferências salvas nos botões da tela e o atalho global.</summary>
    private void InitOverlay()
    {
        foreach (var rb in OvCornerPanel.Children.OfType<RadioButton>())
            rb.IsChecked = (string?)rb.Tag == _settings.OverlayCorner;
        foreach (var rb in OvScalePanel.Children.OfType<RadioButton>())
            rb.IsChecked = Math.Abs(double.Parse((string)rb.Tag!, CultureInfo.InvariantCulture) - _settings.OverlayScale) < 0.01;
        BuildOverlayPicker();
        _overlayReady = true;
#if WINDOWS
        Opened += (_, _) =>
        {
            // Ctrl + Shift + O, sem repetição ao segurar
            if (TryGetPlatformHandle()?.Handle is IntPtr h) RegisterHotKey(h, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x4F);
        };
        Closing += (_, _) =>
        {
            if (TryGetPlatformHandle()?.Handle is IntPtr h) UnregisterHotKey(h, HotkeyId);
        };
#else
        // No Wayland o app não pode capturar teclas: o atalho é do sistema e chama "ABExplode --toggle-overlay" (instância única).
        // TODO(Fase 6): FPS pelo MangoHud.
        string cmd = ABExplode.Platform.Linux.LinuxInstance.ToggleCommand();
        bool auto = ABExplode.Platform.Linux.LinuxInstance.CanCreateShortcut;
        OvHotkeyText.Text = auto
            ? "Atalho Ctrl + Shift + O: o Linux não deixa um app pegar teclas globais, então o atalho fica nas configurações de teclado " +
              "do sistema. O botão abaixo cria nelas."
            : "Atalho: o Linux não deixa um app pegar teclas globais. Crie um atalho personalizado nas configurações de teclado do " +
              $"sistema (por exemplo Ctrl + Shift + O) com o comando:\n{cmd}";
        BtnOvShortcut.IsVisible = auto;
        OvFpsHelp.Text = "A detecção de FPS deste sistema chega numa próxima versão.";
#endif
        UpdateOverlayStatus(null);
    }

#if WINDOWS
    private void BtnOvShortcut_Click(object? sender, RoutedEventArgs e) { } // o atalho global do Windows é registrado sozinho
#else
    private async void BtnOvShortcut_Click(object? sender, RoutedEventArgs e)
    {
        string? err = ABExplode.Platform.Linux.LinuxInstance.CreateGnomeShortcut();
        await Dialog.Show(this, "Overlay", err == null
            ? "Atalho criado: Ctrl + Shift + O liga e desliga o overlay, com o ABExplode aberto. Ele aparece em Configurações › Teclado › Atalhos personalizados."
            : $"Não foi possível criar o atalho ({err}). Crie à mão em Configurações › Teclado › Atalhos personalizados, com o comando:\n\n" +
              ABExplode.Platform.Linux.LinuxInstance.ToggleCommand());
    }
#endif

    private void ToggleOverlay() => ChkOverlay.IsChecked = ChkOverlay.IsChecked != true;

    /// <summary>Desliga o overlay durante os testes de GPU (ele também consulta o driver); devolve se estava ligado.</summary>
    private bool PauseOverlay()
    {
        bool on = ChkOverlay.IsChecked == true;
        if (on) ChkOverlay.IsChecked = false;
        return on;
    }

    private void ResumeOverlay(bool wasOn)
    {
        if (wasOn) ChkOverlay.IsChecked = true;
    }

    private void ChkOverlay_Changed(object? sender, RoutedEventArgs e)
    {
        if (ChkOverlay.IsChecked == true)
        {
            _overlay ??= new OverlayWindow();
            ApplyOverlayOptions();
            _overlay.Show();
            if (_overlayTimer == null)
            {
                _overlayTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
                _overlayTimer.Tick += (_, _) => UpdateOverlay();
            }
            _overlayTimer.Start();
            UpdateOverlay();
        }
        else
        {
            _overlayTimer?.Stop();
            _overlay?.Hide();
        }
    }

    private void CloseOverlay()
    {
        _overlayTimer?.Stop();
        _overlay?.Close();
        _overlay = null;
    }

    private void OvOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_overlayReady || sender is RadioButton { IsChecked: not true }) return;
        _settings.OverlayCorner = CheckedTag(OvCornerPanel) ?? "TL";
        _settings.OverlayScale = double.Parse(CheckedTag(OvScalePanel) ?? "1", CultureInfo.InvariantCulture);
        _settings.Save();
        ApplyOverlayOptions();
    }

    private void ApplyOverlayOptions()
    {
        if (_overlay == null) return;
        _overlay.Corner = _settings.OverlayCorner;
        _overlay.SetScale(_settings.OverlayScale);
        _overlay.Reposition();
    }

    private void UpdateOverlay()
    {
        var f = _frames?.GetForegroundStats();
        if (_overlay is { IsVisible: true })
        {
            _overlay.Update(f, BuildOverlayLines(f), OvShow, OvShow.Contains("clock") ? DateTime.Now.ToString("HH:mm") : null);
            _overlay.KeepOnTop(); // o jogo pode ter passado na frente (janela sem borda "sempre no topo")
        }
        UpdateOverlayStatus(f);
    }

    private void UpdateOverlayStatus(FrameStats? f)
    {
        if (_frames?.Error is { } err) OvStatus.Text = $"Detecção de FPS indisponível: {err}";
        else if (_frames == null) OvStatus.Text = "Iniciando a detecção de FPS…";
        else if (f != null) OvStatus.Text = $"Ativa · {f.ProcessName}: {f.Fps:0} FPS ({f.FrameTimeMs:0.0} ms) · 1% low {f.Low1Fps:0}";
        else OvStatus.Text = "Ativa · aguardando um jogo em primeiro plano";
    }

    // ------------------------------------------------------------------ o que mostrar

    /// <summary>Cria as caixas de seleção da tela Overlay, agrupadas por componente.</summary>
    private void BuildOverlayPicker()
    {
        OvItemsPanel.Children.Clear();
        foreach (var group in OverlayMetrics.All.GroupBy(m => m.Group))
        {
            var header = new TextBlock { Text = group.Key.ToUpperInvariant(), Margin = new Thickness(0, 14, 0, 6) };
            header.Classes.Add("label");
            OvItemsPanel.Children.Add(header);
            var wrap = new WrapPanel();
            foreach (var m in group)
            {
                var cb = new CheckBox { Content = m.Label, Tag = m.Key, IsChecked = OvShow.Contains(m.Key), Margin = new Thickness(0, 0, 18, 6) };
                cb.Click += (_, _) =>
                {
                    if (cb.IsChecked == true) OvShow.Add(m.Key); else OvShow.Remove(m.Key);
                    SaveOverlayItems();
                };
                wrap.Children.Add(cb);
            }
            OvItemsPanel.Children.Add(wrap);
        }
    }

    private void SaveOverlayItems()
    {
        _settings.OverlayItems = OverlayMetrics.All.Select(m => m.Key).Where(OvShow.Contains).ToList();
        _settings.Save();
        UpdateOverlay();
    }

    private void OvPreset_Click(object? sender, RoutedEventArgs e)
    {
        _ovShow = ((sender as Control)?.Tag as string) switch
        {
            "all" => OverlayMetrics.All.Select(m => m.Key).ToHashSet(),
            "min" => new HashSet<string> { "fps", "frametime", "low1" },
            _ => OverlayMetrics.Defaults(),
        };
        SaveOverlayItems();
        BuildOverlayPicker();
    }

    // ------------------------------------------------------------------ leitura dos sensores

    private SensorRow? GpuRow(SensorKind type, string name, bool startsWith = false) =>
        _gpus.Count == 0 ? null : _monitor.Rows.FirstOrDefault(r => r.Hardware == _gpus[0].Name && r.Type == type &&
                                                                   (startsWith ? r.Name.StartsWith(name) : r.Name == name) && r.RawValue != null);

    private uint? MainGpuClock(int nvmlType, string lhmName)
    {
        if (_gpus.Count > 0 && _gpus[0].NvmlHandle != IntPtr.Zero && Nvml.Clock(_gpus[0].NvmlHandle, nvmlType) is uint c) return c;
        return GpuRow(SensorKind.Clock, lhmName)?.RawValue is float v && v > 0 ? (uint)v : null;
    }

    /// <summary>VRAM usada e total em GB: NVML quando disponível, senão os sensores (MB).</summary>
    private (double Used, double Total)? MainGpuVram()
    {
        if (_gpus.Count == 0) return null;
        if (_gpus[0].NvmlHandle != IntPtr.Zero && Nvml.MemoryInfo(_gpus[0].NvmlHandle) is Nvml.Memory m && m.Total > 0)
            return (m.Used / 1073741824.0, m.Total / 1073741824.0);
        if (GpuRow(SensorKind.SmallData, "GPU Memory Used")?.RawValue is float u && GpuRow(SensorKind.SmallData, "GPU Memory Total")?.RawValue is float t && t > 0)
            return (u / 1024.0, t / 1024.0);
        return null;
    }

    private (float? Avg, float? Max) CpuClocks()
    {
        var cores = _monitor.Rows.Where(r => r.HardwareType == HardwareKind.Cpu && r.Type == SensorKind.Clock &&
                                             r.Name.StartsWith("Core #") && !r.Name.Contains("Effective") && r.RawValue > 0)
                                 .Select(r => r.RawValue!.Value).ToList();
        return cores.Count > 0 ? (cores.Average(), cores.Max()) : (null, null);
    }

    /// <summary>Monta as linhas GPU / VRAM / CPU / RAM só com o que foi escolhido; linha vazia não aparece.</summary>
    private List<OverlayLine> BuildOverlayLines(FrameStats? f)
    {
        var show = OvShow;
        var s = BottleneckSampler.Sample(_monitor, _gpus.FirstOrDefault(), f);
        var lines = new List<OverlayLine>();
        string T(float? v) => v is float x ? $"{x:0} °C" : "—";
        string P(double? v) => v is double x ? $"{x:0}%" : "—";
        void Line(string label, List<string> parts) { if (parts.Count > 0) lines.Add(new OverlayLine(label, string.Join("  ", parts))); }

        var gpu = new List<string>();
        if (show.Contains("gpu_load")) gpu.Add(P(s.GpuLoad));
        if (show.Contains("gpu_temp")) gpu.Add(T(s.GpuTemp));
        if (show.Contains("gpu_hotspot")) gpu.Add("HS " + T(GpuRow(SensorKind.Temperature, "GPU Hot Spot")?.RawValue));
        if (show.Contains("gpu_clock")) gpu.Add(MainGpuClock(Nvml.ClockGraphics, "GPU Core") is uint c ? $"{c} MHz" : "— MHz");
        if (show.Contains("gpu_memclock")) gpu.Add(MainGpuClock(Nvml.ClockMem, "GPU Memory") is uint mc ? $"mem {mc} MHz" : "mem —");
        if (show.Contains("gpu_power")) gpu.Add(MainGpuPower() is float w ? $"{w:0} W" : "— W");
        if (show.Contains("gpu_volt")) gpu.Add(GpuRow(SensorKind.Voltage, "GPU Core")?.RawValue is float v ? $"{v:0.000} V" : "— V");
        if (show.Contains("gpu_fan"))
            gpu.Add(GpuRow(SensorKind.Fan, "GPU Fan", true)?.RawValue is float rpm && rpm > 0 ? $"{rpm:0} RPM"
                  : GpuRow(SensorKind.Control, "GPU Fan", true)?.RawValue is float pct ? $"fan {pct:0}%" : "fan —");
        if (show.Contains("gpu_perfcap") && MainGpuPerfCap() is { Length: > 0 } cap) gpu.Add(cap);
        Line("GPU", gpu);

        var vram = new List<string>();
        var vr = MainGpuVram();
        if (show.Contains("vram_used")) vram.Add(vr is { } a ? $"{a.Used:0.0} GB" : "—");
        if (show.Contains("vram_pct")) vram.Add(P(s.VramPct));
        if (show.Contains("vram_temp")) vram.Add(T(GpuRow(SensorKind.Temperature, "GPU Memory")?.RawValue));
        Line("VRAM", vram);

        var cpu = new List<string>();
        var (avg, max) = CpuClocks();
        if (show.Contains("cpu_load")) cpu.Add(P(s.CpuTotal));
        if (show.Contains("cpu_maxcore")) cpu.Add(s.CpuMaxCore is float mx ? $"núcleo {mx:0}%" : "núcleo —");
        if (show.Contains("cpu_temp")) cpu.Add(T(s.CpuTemp));
        if (show.Contains("cpu_clock")) cpu.Add(avg is float ca ? $"{ca / 1000:0.00} GHz" : "— GHz");
        if (show.Contains("cpu_clockmax")) cpu.Add(max is float cm ? $"pico {cm / 1000:0.00} GHz" : "pico —");
        if (show.Contains("cpu_power")) cpu.Add(_monitor.CpuPower is float cw && cw > 0 ? $"{cw:0} W" : "— W"); // 0 = sem leitura (precisa de administrador)
        Line("CPU", cpu);

        var ram = new List<string>();
        var (total, availB) = _platform.SystemInfo.MemoryStatus();
        if (show.Contains("ram_used")) ram.Add(total > 0 ? $"{(total - availB) / 1073741824.0:0.0} GB" : "—");
        if (show.Contains("ram_pct")) ram.Add(P(s.RamPct));
        Line("RAM", ram);
        return lines;
    }

#if WINDOWS
    private const int HotkeyId = 0x5245; // "RE"
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
#endif
}

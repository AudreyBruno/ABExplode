using ABExplode.Core;
using ABExplode.Platform;
using Avalonia.Controls;

namespace ABExplode.App;

/// <summary>Tela Placa de vídeo: especificações estilo GPU-Z e sensores ao vivo da placa escolhida.</summary>
public partial class MainWindow
{
    private GpuInfo? _gpu;
    private int _gpuLiveRows = -1;

    /// <summary>Chips de escolha (só com mais de uma placa) e a primeira placa na tela.</summary>
    private void InitGpuPage()
    {
        GpuChips.Children.Clear();
        for (int i = 0; i < _gpus.Count && _gpus.Count > 1; i++)
        {
            var g = _gpus[i];
            var chip = new RadioButton
            {
                Content = g.Name, GroupName = "Gpu", IsChecked = i == 0,
                Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("Chip"),
            };
            chip.IsCheckedChanged += (_, _) => { if (chip.IsChecked == true) ShowGpu(g); };
            GpuChips.Children.Add(chip);
        }
        if (_gpus.Count > 0) ShowGpu(_gpus[0]);
    }

    private void ShowGpu(GpuInfo g)
    {
        _gpu = g;
        GpuName.Text = g.Name;
        GpuSubtitle.Text = g.Subtitle;
        GpuVendor.Text = g.Vendor;
        GpuSections.ItemsSource = g.Sections;
        _gpuLiveRows = -1;
        UpdateGpuPage();
    }

    /// <summary>A cada segundo com a página aberta: leituras do NVML e, se apareceram sensores novos, a lista ao vivo.</summary>
    private void UpdateGpuPage()
    {
        if (_gpu == null) return;
        GpuExtras.ItemsSource = _gpu.LiveExtras();
        if (_monitor.Rows.Count == _gpuLiveRows) return;
        // Sensores deste hardware, em ordem fixa (clock, temperatura, carga...), como no GPU-Z.
        var g = _gpu;
        GpuLive.ItemsSource = _monitor.Rows.Where(r => r.Hardware == g.Name).OrderBy(r => r.Order).ThenBy(r => r.Name).ToList();
        _gpuLiveRows = _monitor.Rows.Count;
    }
}

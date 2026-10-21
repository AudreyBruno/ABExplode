using System.Diagnostics;
using System.Text;
using ABExplode.Core;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace ABExplode.App;

/// <summary>Relatório exportável (HTML ou JSON) com sistema, GPU, sensores, discos, energia, rede e benchmarks.</summary>
public partial class MainWindow
{
    private async void BtnReport_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar relatório",
            SuggestedFileName = $"ABExplode-relatorio-{DateTime.Now:yyyyMMdd-HHmm}",
            DefaultExtension = "html",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Relatório HTML") { Patterns = new[] { "*.html" } },
                new FilePickerFileType("Dados JSON") { Patterns = new[] { "*.json" } },
            },
        });
        if (file == null) return;
        bool json = file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        BtnReport.IsEnabled = false;
        BtnReport.Content = "GERANDO…";
        try
        {
            // Dados que vivem na interface são copiados aqui (thread da tela); a leitura dos discos roda em segundo plano.
            var sensorsSnapshot = _monitor.Rows.ToList();
            var data = new ReportData
            {
                Date = DateTime.Now,
                Machine = Environment.MachineName,
                AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "",
                LogoBase64 = LoadLogoBase64(),
                System = (InfoCategories.ItemsSource as IEnumerable<InfoNode> ?? Enumerable.Empty<InfoNode>())
                         .Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList(),
                Gpus = _gpus.Select(g => new ReportGpu(g.Name, g.Subtitle,
                         g.Sections.Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList())).ToList(),
                Sensors = sensorsSnapshot.Select(s => new ReportSensor(s.Hardware, s.Category, s.Name, s.Value, s.Min, s.Max)).ToList(),
                Benchmarks = BuildBenchList(),
            };
            var disks = await Task.Run(() => _platform.DiskHealth.Read(sensorsSnapshot));
            data.Disks.AddRange(disks.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));
            var power = await Task.Run(() => _platform.SystemInfo.Battery(sensorsSnapshot));
            data.Power.AddRange(power.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));
            if (_lastNet != null)
                data.Network.AddRange(_lastNet.Select(d => new ReportDisk(d.Title, d.Meta, d.VerdictText, d.Reasons, d.Metrics)));

            string text = json ? ReportBuilder.Json(data) : ReportBuilder.Html(data);
            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                await writer.WriteAsync(text);

            string? path = file.TryGetLocalPath();
            SystemSub.Text = $"Relatório salvo em {path ?? file.Name}";
            if (path != null) try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            await Dialog.Show(this, "ABExplode", "Não foi possível gerar o relatório:\n" + ex.Message);
        }
        finally
        {
            BtnReport.Content = "EXPORTAR RELATÓRIO";
            BtnReport.IsEnabled = true;
        }
    }

    private List<ReportBench> BuildBenchList()
    {
        string TestName(string t) => t switch
        {
            "gpu" => "Cena de jogo (GPU)", "sim" => "Simulação de jogo (CPU)", "mem" => "Memória e carregamento",
            "disk" => "Disco", "oc" => "RESET OC (estabilidade)", _ => t
        };
        var list = _settings.BenchRecords.Select(r => new ReportBench(r.Date, TestName(r.Test), r.Summary)).ToList();
        foreach (var r in _settings.BenchRuns)
        {
            var parts = new List<string>();
            if (r.Multi is double mt) parts.Add($"multi-core {mt:N0} pts");
            if (r.Single is double st) parts.Add($"single-core {st:N0} pts");
            if (r.Sustained) parts.Add("sustentado");
            list.Add(new ReportBench(r.Date, "Render (CPU)", string.Join(" · ", parts) + (string.IsNullOrEmpty(r.Cpu) ? "" : $" · {r.Cpu}")));
        }
        return list.OrderByDescending(b => b.Date).ToList();
    }

    private static string? LoadLogoBase64()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://ABExplode/Assets/logo-badge.png"));
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }
}

using ABExplode.Core;
using ABExplode.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ABExplode.App;

/// <summary>RESET JOGOS · FPS estimado: estimativa local por GPU/CPU ou dados reais da HowManyFPS (com chave).</summary>
public partial class MainWindow
{
    private HowManyFpsClient? _fpsApi;
    private List<GameRow> _gameRows = new();
    private CancellationTokenSource? _gameFetch;
    private string _gpuName = "";
    private double _cpuIdx = 80, _gpuIdx = 50;

    /// <summary>Chamado quando a GPU e a CPU já foram identificadas.</summary>
    private void InitGames()
    {
        _gpuName = _gpus.Count > 0 ? _gpus[0].Name : "";
        _gpuIdx = GameCatalog.GpuPerf(_gpuName) ?? 50;
        _cpuIdx = GameCatalog.CpuPerf(_cpuName) ?? 80;
        GamesSub.Text = $"FPS esperado no seu PC · {GameCatalog.ShortGpuName(_gpuName)} · {GameCatalog.ShortCpuName(_cpuName)}";

        _fpsApi = new HowManyFpsClient(AppSettings.Folder) { ApiKey = _settings.HowManyFpsKey };
        if (_fpsApi.Configured) ApiKeyBox.Text = _settings.HowManyFpsKey;

        _gameRows = GameCatalog.Games.Select(g => new GameRow(g)).ToList();
        ApplyGameSearch();
        RefreshGames();
    }

    private void GameOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true } && _fpsApi != null) RefreshGames(); // também dispara ao montar a tela
    }

    private void GameSearch_Changed(object? sender, TextChangedEventArgs e) => ApplyGameSearch();

    private void ApplyGameSearch()
    {
        string q = GameSearch.Text?.Trim() ?? "";
        GameList.ItemsSource = q.Length == 0 ? _gameRows : _gameRows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void BtnApiKey_Click(object? sender, RoutedEventArgs e)
    {
        if (_fpsApi == null) return;
        _settings.HowManyFpsKey = ApiKeyBox.Text ?? "";
        _settings.Save();
        _fpsApi.ApiKey = _settings.HowManyFpsKey;
        RefreshGames();
    }

    /// <summary>Recalcula a estimativa local de todos os jogos e, com chave configurada, busca os dados reais na API.</summary>
    private void RefreshGames()
    {
        if (_fpsApi == null) return;
        string res = CheckedTag(GameResPanel) ?? "1920x1080";
        string preset = CheckedTag(GamePresetPanel) ?? "Alto";
        string up = CheckedTag(GameUpscalePanel) ?? "Native";

        foreach (var row in _gameRows)
        {
            var est = GameCatalog.Estimate(row.Game, _gpuIdx, _cpuIdx, res, preset, up);
            string limiter = est.Limiter switch
            {
                Bottleneck.Cpu => "gargalo: CPU",
                Bottleneck.Gpu => "gargalo: GPU",
                Bottleneck.Engine => $"trava do jogo em {row.Game.EngineCap:0} FPS",
                _ => ""
            };
            row.Set(est.Avg, 0, "ESTIMATIVA", limiter);
        }

        _gameFetch?.Cancel();
        if (_fpsApi.Configured)
        {
            GameSourceTitle.Text = "Fonte: HowManyFPS (dados reais)";
            GameSourceText.Text = "FPS médio e 1% low medidos em sessões reais de jogo. Respostas ficam em cache por 7 dias. " +
                                  "A API usa as configurações gráficas dela; \"Gráficos\" vale só para a estimativa.";
            BtnApiKey.Content = "ATUALIZAR CHAVE";
            _gameFetch = new CancellationTokenSource();
            _ = FetchGamesAsync(res, up, _gameFetch.Token);
        }
        else
        {
            GameSourceTitle.Text = "Fonte: estimativa local (aproximada)";
            GameSourceText.Text = "Calculada a partir do desempenho relativo da sua GPU e CPU. Para FPS medido em jogo, " +
                                  "peça uma chave da API em info@howmanyfps.com e cole ao lado.";
            BtnApiKey.Content = "SALVAR CHAVE";
        }
    }

    private async Task FetchGamesAsync(string res, string up, CancellationToken ct)
    {
        string cpu = GameCatalog.ShortCpuName(_cpuName), gpu = GameCatalog.ShortGpuName(_gpuName);
        int ok = 0;
        foreach (var row in _gameRows)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var r = await _fpsApi!.GetAsync(cpu, gpu, row.Name, res, up, ct);
                row.Set(r.AvgFps, r.Min1Fps, "HOWMANYFPS", "");
                ok++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                row.Set(row.Fps, 0, "ESTIMATIVA", $"API: {ex.Message}");
                if (ex.Message.Contains("chave") || ex.Message.Contains("cota"))
                {
                    GameSourceText.Text = $"A API recusou a consulta ({ex.Message}). Mostrando a estimativa local.";
                    return; // não adianta insistir nos outros jogos
                }
            }
        }
        if (!ct.IsCancellationRequested)
            GameSourceText.Text = $"{ok} de {_gameRows.Count} jogos com dados da HowManyFPS; os demais usam a estimativa local.";
    }
}

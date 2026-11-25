using ABExplode.Core;
using Avalonia.Controls;

namespace ABExplode.App;

/// <summary>Grupo da tabela de sensores (um por hardware, na ordem em que aparecem).</summary>
public sealed record SensorGroup(string Name, List<SensorRow> Rows);

/// <summary>
/// Tela Sensores. No WPF era um DataGrid agrupado; aqui é uma lista agrupada com as mesmas colunas. As linhas (SensorRow)
/// avisam a UI quando o valor muda, então a lista só é remontada quando o filtro muda ou aparecem sensores novos.
/// </summary>
public partial class MainWindow
{
    private string? _category;   // filtro por chip (null = todos)
    private string _search = ""; // filtro por texto
    private int _sensorRowsShown = -1;

    private void AddChip(string label, string? category, bool isChecked = false)
    {
        var chip = new RadioButton
        {
            Content = label,
            GroupName = "Category",
            Theme = (Avalonia.Styling.ControlTheme?)this.FindResource("Chip"),
            IsChecked = isChecked
        };
        chip.IsCheckedChanged += (_, _) =>
        {
            if (chip.IsChecked != true) return;
            _category = category;
            ApplySensorFilter();
        };
        CategoryChips.Children.Add(chip);
    }

    private void SensorSearch_Changed(object? sender, TextChangedEventArgs e)
    {
        _search = SensorSearch.Text?.Trim() ?? "";
        ApplySensorFilter();
    }

    private void ApplySensorFilter()
    {
        var rows = _monitor.Rows.Where(r =>
            (_category == null || r.Category == _category)
            && (_search.Length == 0
                || r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || r.Hardware.Contains(_search, StringComparison.OrdinalIgnoreCase)));
        SensorGroups.ItemsSource = rows.GroupBy(r => r.Hardware).Select(g => new SensorGroup(g.Key, g.ToList())).ToList();
        _sensorRowsShown = _monitor.Rows.Count;
    }

    /// <summary>Chamado a cada segundo: remonta a lista só se apareceram sensores novos.</summary>
    private void UpdateSensorList()
    {
        if (_monitor.Rows.Count != _sensorRowsShown) ApplySensorFilter();
    }
}

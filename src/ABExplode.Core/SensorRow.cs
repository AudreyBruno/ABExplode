using System.ComponentModel;

namespace ABExplode.Core;

/// <summary>Linha do painel de sensores, com notificação de mudança para a UI.</summary>
public class SensorRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public SensorKind Type { get; init; }
    public HardwareKind HardwareType { get; init; }

    private float? _value, _min, _max;
    public float? RawValue => _value;

    /// <summary>Ordem de exibição por tipo (clocks e temperaturas primeiro, como no GPU-Z).</summary>
    public int Order => Type switch
    {
        SensorKind.Clock => 0, SensorKind.Temperature => 1, SensorKind.Load => 2, SensorKind.Fan => 3,
        SensorKind.Control => 4, SensorKind.Power => 5, SensorKind.Voltage => 6, SensorKind.Current => 7,
        SensorKind.SmallData => 8, SensorKind.Data => 9, SensorKind.Throughput => 10, _ => 11
    };
    public string Value => Format(_value);
    public string Min => Format(_min);
    public string Max => Format(_max);

    public void Update(float? value, float? min, float? max)
    {
        _value = value; _min = min; _max = max;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Min)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Max)));
    }

    private string Format(float? v)
    {
        if (v == null) return "—";
        return Type switch
        {
            SensorKind.Temperature => $"{v:0.#} °C",
            SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => $"{v:0.#} %",
            SensorKind.Clock => $"{v:0} MHz",
            SensorKind.Power => $"{v:0.#} W",
            SensorKind.Voltage => $"{v:0.###} V",
            SensorKind.Current => $"{v:0.###} A",
            SensorKind.Fan => $"{v:0} RPM",
            SensorKind.Flow => $"{v:0.#} L/h",
            SensorKind.Frequency => $"{v:0.#} Hz",
            SensorKind.Data => $"{v:0.##} GB",
            SensorKind.SmallData => $"{v:0.#} MB",
            SensorKind.Throughput => $"{v / 1048576f:0.##} MB/s",
            SensorKind.Energy => $"{v:0.#} mWh",
            _ => $"{v:0.##}"
        };
    }

    public static string CategoryName(SensorKind t) => t switch
    {
        SensorKind.Temperature => "Temperatura",
        SensorKind.Load => "Carga",
        SensorKind.Clock => "Clock",
        SensorKind.Power => "Consumo",
        SensorKind.Voltage => "Tensão",
        SensorKind.Current => "Corrente",
        SensorKind.Fan => "Ventoinhas",
        SensorKind.Control => "Controle",
        SensorKind.Level => "Nível",
        SensorKind.Data or SensorKind.SmallData => "Dados",
        SensorKind.Throughput => "Taxa",
        SensorKind.Frequency => "Frequência",
        SensorKind.Energy => "Energia",
        SensorKind.Flow => "Fluxo",
        SensorKind.Humidity => "Umidade",
        SensorKind.Factor => "Fator",
        _ => t.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

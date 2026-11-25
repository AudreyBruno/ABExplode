using System.Collections.ObjectModel;

namespace ABExplode.Platform;

/// <summary>Por que a temperatura/consumo da CPU não aparecem.</summary>
public enum CpuSensorProblem { None, NotElevated, PawnIOMissing, ReadFailed }

/// <summary>Sensores em tempo real (Windows: LibreHardwareMonitor; Linux: hwmon).</summary>
public interface ISensorSource : IPlatformFeature, IDisposable
{
    /// <summary>Uma linha por sensor; atualizada no lugar a cada <see cref="Refresh"/>.</summary>
    ObservableCollection<SensorRow> Rows { get; }
    bool IsOpen { get; }
    Task OpenAsync();
    void Refresh();

    float? CpuTemperature { get; }
    float? CpuLoad { get; }
    float? CpuPower { get; }

    /// <summary>O driver auxiliar de leitura da CPU está instalado (Windows: PawnIO).</summary>
    bool IsCpuDriverInstalled { get; }

    /// <summary>O motivo mais provável de faltar temperatura/consumo da CPU.</summary>
    CpuSensorProblem DiagnoseCpuSensors();

    /// <summary>Explicação para o usuário de cada <see cref="CpuSensorProblem"/>.</summary>
    string Explain(CpuSensorProblem problem);
}

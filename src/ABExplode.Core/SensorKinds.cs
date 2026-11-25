namespace ABExplode.Core;

/// <summary>
/// Tipo de sensor, independente da biblioteca que o lê (no Windows, LibreHardwareMonitor; no Linux, hwmon).
/// Os nomes seguem os do LibreHardwareMonitor, para a conversão ser por nome; o que não existir aqui vira Other.
/// </summary>
public enum SensorKind
{
    Other, Voltage, Current, Power, Clock, Temperature, Load, Frequency, Fan, Flow, Control, Level, Factor,
    Data, SmallData, Throughput, TimeSpan, Timing, Energy, Noise, Conductivity, Humidity
}

/// <summary>Tipo de hardware do sensor (mesma regra de nomes de <see cref="SensorKind"/>).</summary>
public enum HardwareKind
{
    Other, Motherboard, SuperIO, Cpu, Memory, GpuNvidia, GpuAmd, GpuIntel, Storage, Network, Cooler,
    EmbeddedController, Psu, Battery
}

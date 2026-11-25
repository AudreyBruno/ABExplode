namespace ABExplode.Platform;

/// <summary>Placas de vídeo: dados estilo GPU-Z. As leituras ao vivo da NVIDIA vêm do <see cref="Nvml"/>, comum aos dois sistemas.</summary>
public interface IGpuInfo : IPlatformFeature
{
    List<GpuInfo> Collect();

    /// <summary>Versão do driver da placa com este nome (vazio se não achar).</summary>
    string DriverVersionOf(string adapterName);
}

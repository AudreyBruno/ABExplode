namespace ABExplode.Platform;

/// <summary>Recurso que pode não existir em todo sistema; a UI esconde o que não for suportado.</summary>
public interface IPlatformFeature
{
    bool IsSupported { get; }
}

/// <summary>
/// Tudo que muda de um sistema para outro e que a UI usa. Cada sistema tem a sua implementação
/// (ABExplode.Platform.Windows, ABExplode.Platform.Linux); a UI só conhece estas interfaces.
/// </summary>
public interface IPlatform
{
    /// <summary>Nome curto do sistema ("Windows", "Linux").</summary>
    string Name { get; }

    /// <summary>O app está rodando com permissão de administrador (Windows) ou root (Linux).</summary>
    bool IsElevated { get; }

    ISystemInfo SystemInfo { get; }
    IGpuInfo Gpu { get; }
    IDiskHealth DiskHealth { get; }
    IHardwareErrors HardwareErrors { get; }
    IGameLibrary Games { get; }
    ISecretStore Secrets { get; }

    /// <summary>Leitor de sensores novo (abrir com <see cref="ISensorSource.OpenAsync"/>; descartar ao fechar o app).</summary>
    ISensorSource CreateSensors();

    /// <summary>Medidor de FPS de qualquer jogo (Windows: ETW).</summary>
    IFrameSource CreateFrameSource();
}

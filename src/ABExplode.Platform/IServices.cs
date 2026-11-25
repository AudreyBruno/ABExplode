namespace ABExplode.Platform;

/// <summary>Saúde dos discos físicos (S.M.A.R.T.).</summary>
public interface IDiskHealth : IPlatformFeature
{
    List<DiskHealthCard> Read(IEnumerable<SensorRow>? sensors = null);
}

/// <summary>Erros de hardware registrados pelo sistema e identificação da placa-mãe (RESET OC).</summary>
public interface IHardwareErrors : IPlatformFeature
{
    HardwareErrors ErrorsSince(DateTime since);
    (string Maker, string Model) Motherboard();
}

/// <summary>Jogos instalados (Steam, Epic... e os adicionados pelo usuário).</summary>
public interface IGameLibrary : IPlatformFeature
{
    List<LibGame> Scan(IEnumerable<CustomGame> custom);
}

/// <summary>FPS de qualquer jogo, sem injetar nada nele.</summary>
public interface IFrameSource : IPlatformFeature, IDisposable
{
    bool Running { get; }
    string? Error { get; }
    void Start();
    FrameStats? GetStats(int processId);
    FrameStats? GetForegroundStats();
}

/// <summary>Guarda segredos (a chave da API) de forma que só este usuário do sistema leia.</summary>
public interface ISecretStore
{
    string Protect(string plain);
    /// <summary>null se não der para ler (outro usuário, outra máquina, dado corrompido).</summary>
    string? Unprotect(string protectedText);
}

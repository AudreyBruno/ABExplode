namespace ABExplode.Platform;

/// <summary>Informações do sistema: SO, placa-mãe, CPU, RAM, discos, unidades, bateria e memória livre.</summary>
public interface ISystemInfo : IPlatformFeature
{
    /// <summary>Árvore da tela "Sistema" (categorias com chave/valor).</summary>
    List<InfoNode> Collect();

    /// <summary>Relê os discos do nó de armazenamento (pendrive/HD externo conectado ou removido).</summary>
    void RefreshDisks(InfoNode storage);

    /// <summary>Unidades montadas (letras no Windows, pontos de montagem no Linux).</summary>
    List<DriveCard> Drives();

    /// <summary>Cartões de energia/bateria (vazio em desktop).</summary>
    List<DiskHealthCard> Battery(IEnumerable<SensorRow>? sensors = null);

    /// <summary>Nome comercial da CPU.</summary>
    string CpuName();

    /// <summary>Memória física livre em bytes.</summary>
    long FreePhysicalBytes();

    /// <summary>Memória física total e disponível em bytes.</summary>
    (long Total, long Available) MemoryStatus();
}

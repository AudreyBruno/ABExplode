namespace ABExplode.Platform;

/// <summary>Item removido, guardado para "Restaurar removidos".</summary>
public sealed class StartupBackup
{
    public string Kind { get; set; } = "";
    public bool Machine { get; set; }
    public string Name { get; set; } = "";
    public string? Command { get; set; }   // registro
    public string? Backup { get; set; }    // pasta: cópia do atalho em %AppData%\ABExplode\Startup
    public string? Original { get; set; }  // pasta: caminho original
}

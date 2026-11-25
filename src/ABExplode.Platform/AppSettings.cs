using System.IO;
using System.Text.Json;

namespace ABExplode.Platform;

public sealed record BenchRun(DateTime Date, double? Multi, double? Single, bool Sustained, string Cpu);

/// <summary>Resultado dos benchmarks de jogo (Test = "gpu", "sim" ou "mem"). Score é usado para a barra do histórico.</summary>
public sealed record BenchRecord(DateTime Date, string Test, double Score, string Summary);

/// <summary>Programa extra que abre antes do jogo (ex.: ferramenta de comando/monitoramento) e pode fechar junto com ele.</summary>
public sealed class HelperProgram
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool CloseWithGame { get; set; } = true;
    public string Display => System.IO.Path.GetFileName(Path) + (string.IsNullOrWhiteSpace(Args) ? "" : " " + Args);
}

/// <summary>Perfil de um jogo na aba RESET JOGOS: desempenho, argumentos de gráficos e programas auxiliares.</summary>
public sealed class GameProfile
{
    public string Priority { get; set; } = "normal";   // normal | above | high
    public bool HighPerfPlan { get; set; }
    public bool GpuHigh { get; set; }
    public bool DisableFso { get; set; }
    public string? ExePath { get; set; }
    public string WinMode { get; set; } = "default";   // default | full | borderless | window
    public string Res { get; set; } = "default";       // default | 1280x720 | 1920x1080 | 2560x1440 | 3840x2160
    public int Hz { get; set; }                        // 0 = não definir
    public string Style { get; set; } = "source";      // source | unity | unreal
    public string Extra { get; set; } = "";
    public List<HelperProgram> Helpers { get; set; } = new();
}

/// <summary>Jogo adicionado à mão (qualquer .exe).</summary>
public sealed class CustomGame
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
}

/// <summary>Configurações em %AppData%\ABExplode. A chave da API é cifrada por <see cref="Secrets"/> (no Windows, DPAPI: só este usuário a lê).</summary>
public sealed class AppSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ABExplode");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string? HowManyFpsKeyProtected { get; set; }

    // RESET BENCH: últimos resultados (mais recente primeiro)
    public List<BenchRun> BenchRuns { get; set; } = new();
    public List<BenchRecord> BenchRecords { get; set; } = new();

    // RESET OTIMIZA: valores originais dos ajustes do Windows antes da primeira alteração ("<ausente>" = não existia)
    public Dictionary<string, string?> TweakBackup { get; set; } = new();
    /// <summary>ComDB original (reservas de números COM) antes da primeira "liberação".</summary>
    public byte[]? ComDbBackup { get; set; }
    /// <summary>Programas de inicialização removidos, para "Restaurar removidos".</summary>
    public List<StartupBackup> StartupRemoved { get; set; } = new();

    // RESET JOGOS
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();

    // Aparência
    public bool LightTheme { get; set; }

    /// <summary>Linux: abrir os jogos da Biblioteca com o MangoHud (overlay dentro do jogo).</summary>
    public bool MangoHudInGames { get; set; }

    /// <summary>Linux: dependências que o usuário pediu para não lembrar mais na abertura (chaves de LinuxDependency).</summary>
    public List<string> DepsDismissed { get; set; } = new();

    // Overlay
    public string OverlayCorner { get; set; } = "TL";
    public double OverlayScale { get; set; } = 1.0;
    /// <summary>Itens escolhidos para o overlay (chaves de OverlayMetrics); null = padrão.</summary>
    public List<string>? OverlayItems { get; set; }

    /// <summary>Quem cifra a chave da API (Windows: DPAPI). Definido pelo app na inicialização, antes de ler a chave.</summary>
    public static ISecretStore? Secrets { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] // nunca gravar a chave em texto puro
    public string? HowManyFpsKey
    {
        get => string.IsNullOrEmpty(HowManyFpsKeyProtected) ? null : Secrets?.Unprotect(HowManyFpsKeyProtected);
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { HowManyFpsKeyProtected = null; return; }
            var store = Secrets ?? throw new InvalidOperationException("AppSettings.Secrets não foi definido.");
            HowManyFpsKeyProtected = store.Protect(value.Trim());
        }
    }

    /// <summary>Pasta usada antes do app se chamar ABExplode; movida para <see cref="Folder"/> na primeira abertura.</summary>
    private static string OldFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ResetExplode");

    public static AppSettings Load()
    {
        bool migrated = false;
        try
        {
            if (!Directory.Exists(Folder) && Directory.Exists(OldFolder))
            {
                Directory.Move(OldFolder, Folder);
                migrated = true;
            }
        }
        catch { }
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                if (migrated)
                {
                    // as cópias dos atalhos de inicialização guardam o caminho completo da pasta antiga
                    foreach (var b in s.StartupRemoved)
                        if (b.Backup != null && b.Backup.StartsWith(OldFolder, StringComparison.OrdinalIgnoreCase))
                            b.Backup = Folder + b.Backup[OldFolder.Length..];
                    s.Save();
                }
                return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

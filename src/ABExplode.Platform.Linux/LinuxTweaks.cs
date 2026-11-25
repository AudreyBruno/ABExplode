using System.ComponentModel;

namespace ABExplode.Platform.Linux;

/// <summary>Ajuste do Linux para jogos. Mesmas propriedades do modelo do Windows; aplicar e restaurar são assíncronos (pedem senha).</summary>
public sealed class LinuxTweak : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Effect { get; init; } = "";
    public bool NeedsRoot { get; init; }
    public bool NeedsRestart => false;
    internal Func<bool?> Read { get; init; } = () => null;
    /// <summary>Aplica (true) ou desfaz (false); devolve null se deu certo, ou o motivo.</summary>
    public Func<bool, AppSettings, Task<string?>> WriteAsync { get; init; } = (_, _) => Task.FromResult<string?>(null);
    internal Func<AppSettings, Task<string?>> RestoreAsync { get; init; } = _ => Task.FromResult<string?>(null);
    /// <summary>Não dá para usar neste sistema (ex.: kernel sem o parâmetro); o motivo vai no Status.</summary>
    public bool Unavailable { get; init; }
    /// <summary>Parâmetro do kernel (sysctl) deste ajuste, quando for um.</summary>
    internal string? SysctlName { get; init; }

    private bool _isOn;
    public bool IsOn { get => _isOn; set { _isOn = value; Changed(nameof(IsOn)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public string Badge => NeedsRoot ? "PEDE SENHA" : "";

    public void Refresh() => IsOn = Read() ?? false;

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Ajustes documentados e reversíveis. O valor original vai para AppSettings.TweakBackup (chave "linux.&lt;id&gt;") na primeira
/// alteração; os parâmetros do kernel ficam também em /etc/sysctl.d/99-abexplode-&lt;id&gt;.conf para valer depois de reiniciar.
/// </summary>
public static class LinuxTweaks
{
    private static string Key(string id) => "linux." + id;

    public static List<LinuxTweak> All() => new()
    {
        PowerProfile(),
        GameMode(),
        Sysctl("maxmap", "vm.max_map_count", "Limite de mapas de memória alto",
               "Aumenta o vm.max_map_count para 2147483642, o valor do SteamOS e do Fedora.",
               "Evita que jogos pesados fechem sozinhos (CS2, Hogwarts Legacy, Star Citizen, jogos da Unreal Engine 5).",
               v => v >= 1048576, "2147483642"),
        Sysctl("splitlock", "kernel.split_lock_mitigate", "Desligar a mitigação de split lock",
               "Desliga a pausa proposital que o kernel aplica a programas que fazem acessos de memória \"split lock\".",
               "Alguns jogos pelo Proton fazem isso o tempo todo e perdem muito FPS com a mitigação ligada. O SteamOS vem com ela desligada.",
               v => v == 0, "0"),
        Sysctl("swappiness", "vm.swappiness", "Usar menos a memória de troca (swap)",
               "Baixa o vm.swappiness para 10: o sistema só move dados da RAM para o swap quando ela está realmente cheia.",
               "Menos engasgos ao voltar para o jogo depois de alternar janelas, com 16 GB de RAM ou mais.",
               v => v <= 10, "10"),
    };

    // ---------------- perfil de energia (power-profiles-daemon / tuned-ppd: sem senha na sessão ativa) ----------------

    private static string? CurrentProfile() => Sys.Run("powerprofilesctl", "get")?.Trim();

    private static LinuxTweak PowerProfile()
    {
        bool has = Sys.HasCommand("powerprofilesctl");
        return new LinuxTweak
        {
            Id = "power", Title = "Perfil de energia: Desempenho",
            Description = "Mantém a CPU pronta para subir o clock na hora, sem economia agressiva de energia.",
            Effect = "Menos engasgos e 1% low melhor em alguns jogos. Em notebooks gasta mais bateria.",
            Unavailable = !has,
            Read = () => has ? CurrentProfile() == "performance" : null,
            WriteAsync = (on, s) =>
            {
                if (!has) return Task.FromResult<string?>("O power-profiles-daemon não está instalado (pacote power-profiles-daemon ou tuned-ppd).");
                if (!s.TweakBackup.ContainsKey(Key("power"))) s.TweakBackup[Key("power")] = CurrentProfile();
                string target = on ? "performance" : s.TweakBackup[Key("power")] is string prev && prev != "performance" ? prev : "balanced";
                return Task.FromResult(Sys.Run("powerprofilesctl", $"set {target}") != null ? null
                    : (string?)"O sistema recusou: este computador pode não ter o perfil Desempenho.");
            },
            RestoreAsync = s => Task.FromResult(s.TweakBackup.GetValueOrDefault(Key("power")) is string prev && Sys.Run("powerprofilesctl", $"set {prev}") == null
                ? (string?)"Não foi possível voltar o perfil de energia." : null),
        };
    }

    // ---------------- GameMode (Feral): instalar; o jogo usa com gamemoderun %command% ----------------

    private static LinuxTweak GameMode() => new()
    {
        Id = "gamemode", Title = "GameMode instalado", NeedsRoot = true,
        Description = "O GameMode (Feral Interactive) põe a CPU em desempenho, prioriza o jogo e pausa tarefas de fundo enquanto ele está aberto.",
        Effect = "Na Steam: Propriedades do jogo › Opções de inicialização › gamemoderun %command%. Muitos jogos nativos já ativam sozinhos.",
        Read = () => Sys.HasCommand("gamemoded"),
        WriteAsync = async (on, s) =>
        {
            if (!on) return "Para desinstalar o GameMode, use o gerenciador de pacotes da distribuição.";
            var pm = LinuxDependencies.Detect();
            if (!LinuxDependencies.CanInstall(pm))
                return "Instale pelo terminal: " + LinuxDependencies.Instructions(new[] { GameModeDep }, pm);
            var (ok, output) = await LinuxDependencies.InstallAsync(new[] { GameModeDep }, pm!);
            return ok ? null : "A instalação falhou: " + output;
        },
    };

    private static readonly LinuxDependency GameModeDep = new("gamemode", "GameMode", "otimizações durante o jogo",
        new Dictionary<string, string> { ["apt"] = "gamemode", ["dnf"] = "gamemode", ["pacman"] = "gamemode", ["zypper"] = "gamemode" });

    // ---------------- parâmetros do kernel (sysctl) ----------------

    private static string Proc(string name) => "/proc/sys/" + name.Replace('.', '/');

    private static LinuxTweak Sysctl(string id, string name, string title, string description, string effect, Func<long, bool> isOn, string value)
    {
        bool exists = File.Exists(Proc(name));
        string conf = $"/etc/sysctl.d/99-abexplode-{id}.conf";
        return new LinuxTweak
        {
            Id = id, Title = title, Description = description, Effect = effect, NeedsRoot = true, SysctlName = name,
            Unavailable = !exists,
            Read = () => Sys.ReadLong(Proc(name)) is long v ? isOn(v) : null,
            WriteAsync = async (on, s) =>
            {
                if (!exists) return "Este kernel não tem esse parâmetro.";
                if (!s.TweakBackup.ContainsKey(Key(id))) s.TweakBackup[Key(id)] = Sys.Read(Proc(name));
                string original = s.TweakBackup[Key(id)] ?? value;
                // Liga: aplica agora e grava o arquivo para valer depois de reiniciar. Desliga: volta o original e apaga o arquivo.
                string script = on ? $"sysctl -w {name}={value} && echo '{name} = {value}' > {conf}"
                                   : $"sysctl -w {name}={original} && rm -f {conf}";
                var (ok, output) = await Sys.RunAsRootAsync(script);
                return ok ? null : output;
            },
            RestoreAsync = async s =>
            {
                if (s.TweakBackup.GetValueOrDefault(Key(id)) is not string original) return null;
                var (ok, output) = await Sys.RunAsRootAsync($"sysctl -w {name}={original} && rm -f {conf}");
                return ok ? null : output;
            },
        };
    }

    /// <summary>Volta todos os ajustes alterados pelo app. Junta os de root numa pedida de senha só.</summary>
    public static async Task<List<string>> RestoreAllAsync(IEnumerable<LinuxTweak> tweaks, AppSettings s)
    {
        var errors = new List<string>();
        var changed = tweaks.Where(t => s.TweakBackup.ContainsKey(Key(t.Id))).ToList();
        foreach (var t in changed.Where(t => !t.NeedsRoot))
            if (await t.RestoreAsync(s) is string e) errors.Add($"{t.Title}: {e}");
        var root = changed.Where(t => t.SysctlName != null).ToList();
        if (root.Count > 0)
        {
            // Mesmo script dos Sysctl, juntos: sysctl -w nome=original && rm -f arquivo
            var parts = new List<string>();
            foreach (var t in root)
            {
                if (s.TweakBackup[Key(t.Id)] is string original) parts.Add($"sysctl -w {t.SysctlName}={original}");
                parts.Add($"rm -f /etc/sysctl.d/99-abexplode-{t.Id}.conf");
            }
            var (ok, output) = await Sys.RunAsRootAsync(string.Join(" ; ", parts));
            if (!ok) errors.Add(output);
        }
        if (errors.Count == 0)
            foreach (var t in changed) s.TweakBackup.Remove(Key(t.Id));
        return errors;
    }
}

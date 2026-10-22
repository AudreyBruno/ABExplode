using System.Security.Cryptography;
using System.Text;

namespace ABExplode.Platform.Linux;

/// <summary>Plataforma Linux: /proc, /sys, NVML e as ferramentas do sistema (lsblk, lspci, smartctl, udevadm, journalctl).</summary>
public sealed class LinuxPlatform : IPlatform
{
    public string Name => "Linux";
    public bool IsElevated => Sys.IsRoot;

    public ISystemInfo SystemInfo { get; } = new LinuxSystemInfo();
    public IGpuInfo Gpu { get; } = new LinuxGpuInfo();
    public IDiskHealth DiskHealth { get; } = new LinuxDiskHealth();
    public IHardwareErrors HardwareErrors { get; } = new LinuxHardwareErrors();
    public IGameLibrary Games { get; } = new LinuxGameLibrary();
    public ISecretStore Secrets { get; } = new FileKeySecretStore();

    public ISensorSource CreateSensors() => new LinuxSensors();
    public IFrameSource CreateFrameSource() => new NoFrameSource();
}

/// <summary>
/// O Linux não tem um rastreamento de quadros como o ETW, e o log do MangoHud só é gravado quando a coleta termina (não dá
/// para ler ao vivo). O FPS aparece dentro do jogo pelo MangoHud (LinuxMangoHud); aqui a medição fica indisponível com o motivo.
/// </summary>
internal sealed class NoFrameSource : IFrameSource
{
    public bool IsSupported => false;
    public bool Running => false;
    public string? Error => "o Linux não tem um medidor de quadros para apps; o FPS aparece dentro do jogo pelo MangoHud (quadro abaixo)";
    public void Start() { }
    public FrameStats? GetStats(int processId) => null;
    public FrameStats? GetForegroundStats() => null;
    public void Dispose() { }
}

/// <summary>
/// Cifra a chave da API com AES-GCM usando uma chave aleatória guardada em ~/.config/ABExplode/secret.key, com permissão
/// só do dono (600). Não protege contra quem já tem acesso à conta do usuário, como o DPAPI do Windows também não protege.
/// </summary>
internal sealed class FileKeySecretStore : ISecretStore
{
    private const string Prefix = "aesgcm1:";
    private static readonly string KeyFile = Path.Combine(AppSettings.Folder, "secret.key");

    private static byte[] Key(bool create)
    {
        if (File.Exists(KeyFile))
        {
            var k = File.ReadAllBytes(KeyFile);
            if (k.Length == 32) return k;
        }
        if (!create) throw new CryptographicException("sem chave");
        Directory.CreateDirectory(AppSettings.Folder);
        var key = RandomNumberGenerator.GetBytes(32);
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = ownerOnly; // já nasce 600, sem janela com outra permissão
        using (var fs = new FileStream(KeyFile, options)) fs.Write(key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFile, ownerOnly);
        return key;
    }

    public string Protect(string plain)
    {
        var key = Key(create: true);
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public string? Unprotect(string protectedText)
    {
        try
        {
            if (!protectedText.StartsWith(Prefix)) return null; // gravado por outro sistema (DPAPI do Windows)
            var all = Convert.FromBase64String(protectedText[Prefix.Length..]);
            if (all.Length < 28) return null;
            var plain = new byte[all.Length - 28];
            using var aes = new AesGcm(Key(create: false), 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }
}

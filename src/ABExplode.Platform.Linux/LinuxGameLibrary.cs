using System.Text.Json;

namespace ABExplode.Platform.Linux;

/// <summary>
/// Jogos instalados no Linux: Steam (nativa, Flatpak e Snap), Heroic (Epic e GOG) e os adicionados à mão. Só lê arquivos.
/// </summary>
internal sealed class LinuxGameLibrary : IGameLibrary
{
    public bool IsSupported => true;

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public List<LibGame> Scan(IEnumerable<CustomGame> custom)
    {
        var all = new List<LibGame>();
        try { all.AddRange(Steam()); } catch { }
        try { all.AddRange(Heroic()); } catch { }
        foreach (var c in custom)
        {
            if (!File.Exists(c.Exe)) continue;
            all.Add(new LibGame
            {
                Store = "Manual", Id = c.Exe, Name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileNameWithoutExtension(c.Exe) : c.Name,
                InstallDir = Path.GetDirectoryName(c.Exe) ?? "", ExePath = c.Exe, Args = c.Args,
            });
        }
        // A mesma biblioteca pode aparecer por dois caminhos (~/.steam/steam é link para ~/.local/share/Steam).
        return all.GroupBy(g => g.Key).Select(g => g.First())
                  .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------------- Steam ----------------

    /// <summary>Instalações da Steam que existem: nativa, Flatpak e Snap.</summary>
    public static IEnumerable<string> SteamRoots()
    {
        var seen = new HashSet<string>();
        foreach (var p in new[]
        {
            Path.Combine(Home, ".local/share/Steam"), Path.Combine(Home, ".steam/steam"), Path.Combine(Home, ".steam/root"),
            Path.Combine(Home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"),
            Path.Combine(Home, "snap/steam/common/.local/share/Steam"),
        })
        {
            if (!Directory.Exists(Path.Combine(p, "steamapps"))) continue;
            if (seen.Add(Sys.RealPath(p))) yield return p;
        }
    }

    private static IEnumerable<string> LibraryFolders(string steam)
    {
        var list = new List<string> { steam };
        try
        {
            string f = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(f) && Vdf.Parse(File.ReadAllText(f)).Child("libraryfolders") is { } root)
                foreach (var (_, c) in root.Children)
                    if (c.Get("path") is string p && Directory.Exists(p) && !list.Contains(p)) list.Add(p);
        }
        catch { }
        return list;
    }

    private static IEnumerable<LibGame> Steam()
    {
        var seenLibs = new HashSet<string>();
        foreach (var steam in SteamRoots())
            foreach (var lib in LibraryFolders(steam))
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps) || !seenLibs.Add(Sys.RealPath(apps))) continue;
                foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    LibGame? g = null;
                    try
                    {
                        var st = Vdf.Parse(File.ReadAllText(acf)).Child("AppState");
                        string? id = st?.Get("appid"), name = st?.Get("name"), dir = st?.Get("installdir");
                        if (id == null || name == null || dir == null) continue;
                        if (long.TryParse(st!.Get("StateFlags"), out long flags) && (flags & 4) == 0) continue; // 4 = instalado por completo
                        if (id == "228980" || name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)) continue;
                        string full = Path.Combine(apps, "common", dir);
                        if (!Directory.Exists(full)) continue;
                        g = new LibGame { Store = "Steam", Id = id, Name = name, InstallDir = full, LaunchUri = $"steam://rungameid/{id}" };
                    }
                    catch { }
                    if (g != null) yield return g;
                }
            }
    }

    // ---------------- Heroic (Epic pela Legendary, GOG) ----------------

    private static IEnumerable<string> HeroicRoots() => new[]
    {
        Path.Combine(Home, ".config/heroic"), Path.Combine(Home, ".var/app/com.heroicgameslauncher.hgl/config/heroic"),
    }.Where(Directory.Exists);

    private static IEnumerable<LibGame> Heroic()
    {
        foreach (var root in HeroicRoots())
        {
            // Epic: installed.json da Legendary, dentro da pasta do Heroic
            string epic = Path.Combine(root, "legendaryConfig", "legendary", "installed.json");
            if (File.Exists(epic))
            {
                List<LibGame> games = new();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(epic));
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var v = p.Value;
                        string? S(string k) => v.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                        string? name = S("title"), dir = S("install_path"), exe = S("executable");
                        if (name == null || dir == null || !Directory.Exists(dir)) continue;
                        string? exePath = exe != null ? Path.Combine(dir, exe) : null;
                        games.Add(new LibGame
                        {
                            Store = "Epic Games", Id = p.Name, Name = name, InstallDir = dir,
                            ExePath = exePath != null && File.Exists(exePath) ? exePath : null,
                            LaunchUri = $"heroic://launch/legendary/{p.Name}",
                        });
                    }
                }
                catch { }
                foreach (var g in games) yield return g;
            }

            // GOG: gog_store/installed.json (só pastas) + library.json (nomes)
            string gog = Path.Combine(root, "gog_store", "installed.json");
            if (File.Exists(gog))
            {
                List<LibGame> games = new();
                try
                {
                    var names = new Dictionary<string, string>();
                    string lib = Path.Combine(root, "store_cache", "gog_library.json");
                    if (File.Exists(lib))
                    {
                        using var ld = JsonDocument.Parse(File.ReadAllText(lib));
                        if (ld.RootElement.TryGetProperty("games", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var g in arr.EnumerateArray())
                                if (g.TryGetProperty("app_name", out var a) && g.TryGetProperty("title", out var t))
                                    names[a.GetString() ?? ""] = t.GetString() ?? "";
                    }
                    using var doc = JsonDocument.Parse(File.ReadAllText(gog));
                    if (doc.RootElement.TryGetProperty("installed", out var inst) && inst.ValueKind == JsonValueKind.Array)
                        foreach (var g in inst.EnumerateArray())
                        {
                            string? id = g.TryGetProperty("appName", out var a) ? a.GetString() : null;
                            string? dir = g.TryGetProperty("install_path", out var ip) ? ip.GetString() : null;
                            if (id == null || dir == null || !Directory.Exists(dir)) continue;
                            games.Add(new LibGame
                            {
                                Store = "GOG", Id = id, Name = names.GetValueOrDefault(id) ?? Path.GetFileName(dir), InstallDir = dir,
                                LaunchUri = $"heroic://launch/gog/{id}",
                            });
                        }
                }
                catch { }
                foreach (var g in games) yield return g;
            }
        }
    }
}

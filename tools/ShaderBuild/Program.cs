using System.Text.RegularExpressions;
using Vortice.Dxc;

// Compila cada src/ABExplode.Vulkan/Shaders/*.hlsl para SPIR-V. A primeira linha do arquivo diz os pontos de entrada:
//   // entries: VS:vs_6_0 PS:ps_6_0
// Opções extras do compilador, se o arquivo precisar (ex.: ray query pede Vulkan 1.2):
//   // options: -fspv-target-env=vulkan1.2 -fvk-use-scalar-layout
// Saída: <nome>.<entrada>.spv ao lado (ex.: burn.VS.spv). As matrizes ficam como no Direct3D (row_major + mul(v, M)) e o
// eixo Y é invertido pela viewport negativa no Vulkan, então o mesmo HLSL do Windows serve sem mudar a matemática.

string root = args.Length > 0 ? args[0] : FindRepoRoot();
string dir = Path.Combine(root, "src", "ABExplode.Vulkan", "Shaders");
int built = 0, failed = 0;
foreach (var file in Directory.GetFiles(dir, "*.hlsl").OrderBy(f => f))
{
    string source = File.ReadAllText(file);
    var header = Regex.Match(source, @"^//\s*entries:\s*(.+)$", RegexOptions.Multiline);
    if (!header.Success) { Console.WriteLine($"{Path.GetFileName(file)}: sem a linha // entries:, pulado"); continue; }
    var extra = Regex.Match(source, @"^//\s*options:\s*(.+)$", RegexOptions.Multiline) is { Success: true } m
        ? m.Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
    foreach (var entry in header.Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = entry.Split(':');
        string name = parts[0], profile = parts[1];
        string env = extra.FirstOrDefault(o => o.StartsWith("-fspv-target-env=")) ?? "-fspv-target-env=vulkan1.1";
        string[] options = new[] { "-spirv", env, "-O3", "-E", name, "-T", profile, "-HV", "2021" }
            .Concat(extra.Where(o => !o.StartsWith("-fspv-target-env="))).ToArray();
        using var result = DxcCompiler.Compile(source, options);
        string output = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}.{name}.spv");
        if (result.GetStatus().Failure)
        {
            Console.WriteLine($"{Path.GetFileName(file)} {name}: ERRO\n{result.GetErrors()}");
            failed++;
            continue;
        }
        var bytes = result.GetObjectBytecodeArray();
        File.WriteAllBytes(output, bytes);
        Console.WriteLine($"{Path.GetFileName(output)}: {bytes.Length} bytes");
        built++;
    }
}
Console.WriteLine($"{built} compilados, {failed} com erro.");
return failed > 0 ? 1 : 0;

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "ABExplode.sln"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

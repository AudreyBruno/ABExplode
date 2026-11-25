using System.Numerics;
using System.Runtime.InteropServices;

namespace ABExplode.Core;

/// <summary>Vértice da cena de jogo do RESET BENCH (posição, normal e material: 0 terreno, 1 tronco, 2 folhas).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SceneVertex
{
    public Vector3 Pos;
    public Vector3 Normal;
    public float Mat;
}

/// <summary>
/// Geometria e câmera da cena de jogo do RESET BENCH, iguais no Direct3D (Windows) e no Vulkan (Linux): vale com terreno por
/// ruído fractal, 40 mil árvores, água e um trajeto fixo de câmera. Tudo determinístico: o mesmo trabalho em qualquer PC.
/// </summary>
public static class GameScene
{
    public const int TerrainN = 512;        // vértices por lado
    public const float TerrainSize = 800f;
    public const float WaterY = 6f;
    public const int TreeCount = 40_000;
    public const int ShadowRes = 4096;

    /// <summary>Direção do sol e a projeção ortográfica do mapa de sombras.</summary>
    public static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(-0.45f, -0.55f, -0.35f));
    public static Matrix4x4 LightViewProj =>
        Matrix4x4.CreateLookAt(-SunDir * 900, Vector3.Zero, Vector3.UnitY) * Matrix4x4.CreateOrthographic(900, 900, 1, 1900);
    public static Matrix4x4 Projection(int width, int height) => Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, width / (float)height, 0.5f, 2000);

    /// <summary>Altura do terreno: ruído fractal (fBm) com um vale sinuoso cortando o mapa.</summary>
    public static float TerrainHeight(float x, float z)
    {
        float h = 0, amp = 1, freq = 0.005f;
        for (int o = 0; o < 6; o++) { h += amp * ValueNoise(x * freq + o * 17.3f, z * freq - o * 9.1f); amp *= 0.5f; freq *= 2.03f; }
        h = h * 55 + 10;
        float river = MathF.Sin(z * 0.007f) * 120;                       // curva do vale
        float d = MathF.Abs(x - river);
        float valley = MathF.Min(1, d / 90f);
        return h * (0.25f + 0.75f * valley * valley) - (1 - valley) * 4;
    }

    private static float ValueNoise(float x, float z)
    {
        int xi = (int)MathF.Floor(x), zi = (int)MathF.Floor(z);
        float fx = x - xi, fz = z - zi;
        float u = fx * fx * (3 - 2 * fx), v = fz * fz * (3 - 2 * fz);
        float a = Hash(xi, zi), b = Hash(xi + 1, zi), c = Hash(xi, zi + 1), d = Hash(xi + 1, zi + 1);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    private static float Hash(int x, int z)
    {
        uint h = (uint)(x * 374761393 + z * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
    }

    public static (SceneVertex[] V, uint[] I) BuildTerrain()
    {
        var v = new SceneVertex[TerrainN * TerrainN];
        float step = TerrainSize / (TerrainN - 1), half = TerrainSize / 2;
        for (int z = 0; z < TerrainN; z++)
            for (int x = 0; x < TerrainN; x++)
            {
                float wx = x * step - half, wz = z * step - half;
                float h = TerrainHeight(wx, wz);
                float hx = TerrainHeight(wx + 0.5f, wz) - TerrainHeight(wx - 0.5f, wz);
                float hz = TerrainHeight(wx, wz + 0.5f) - TerrainHeight(wx, wz - 0.5f);
                v[z * TerrainN + x] = new SceneVertex { Pos = new(wx, h, wz), Normal = Vector3.Normalize(new(-hx, 1, -hz)), Mat = 0 };
            }
        var idx = new uint[(TerrainN - 1) * (TerrainN - 1) * 6];
        int k = 0;
        for (int z = 0; z < TerrainN - 1; z++)
            for (int x = 0; x < TerrainN - 1; x++)
            {
                uint a = (uint)(z * TerrainN + x), b = a + 1, c = a + TerrainN, d = c + 1;
                idx[k++] = a; idx[k++] = c; idx[k++] = b;
                idx[k++] = b; idx[k++] = c; idx[k++] = d;
            }
        return (v, idx);
    }

    /// <summary>Árvore low-poly: tronco (cilindro) + 3 cones de folhagem. Mat 1 = tronco, 2 = folhas.</summary>
    public static (SceneVertex[] V, uint[] I) BuildTree()
    {
        var v = new List<SceneVertex>(); var idx = new List<uint>();
        void Cone(float baseY, float height, float r0, float r1, int seg, float mat)
        {
            uint start = (uint)v.Count;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * MathF.Tau, c = MathF.Cos(a), s = MathF.Sin(a);
                float slope = (r0 - r1) / height;
                var n = Vector3.Normalize(new Vector3(c, slope, s));
                v.Add(new SceneVertex { Pos = new(c * r0, baseY, s * r0), Normal = n, Mat = mat });
                v.Add(new SceneVertex { Pos = new(c * r1, baseY + height, s * r1), Normal = n, Mat = mat });
            }
            for (int i = 0; i < seg; i++)
            {
                uint a = start + (uint)i * 2;
                idx.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 });
            }
        }
        Cone(0, 2.0f, 0.25f, 0.2f, 8, 1);
        Cone(1.4f, 2.6f, 2.0f, 0.05f, 12, 2);
        Cone(2.8f, 2.3f, 1.6f, 0.05f, 12, 2);
        Cone(4.0f, 2.0f, 1.1f, 0.02f, 12, 2);
        return (v.ToArray(), idx.ToArray());
    }

    /// <summary>Posições das árvores (x, y, z, escala), sempre as mesmas: semente fixa.</summary>
    public static Vector4[] PlaceTrees()
    {
        var list = new List<Vector4>(TreeCount);
        uint s = 2024;
        float R() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s >> 8) / 16777216f; }
        while (list.Count < TreeCount)
        {
            float x = (R() - 0.5f) * TerrainSize * 0.98f, z = (R() - 0.5f) * TerrainSize * 0.98f;
            float h = TerrainHeight(x, z);
            float slope = MathF.Abs(TerrainHeight(x + 1, z) - TerrainHeight(x - 1, z)) + MathF.Abs(TerrainHeight(x, z + 1) - TerrainHeight(x, z - 1));
            if (h < WaterY + 1.5f || h > 48 || slope > 1.4f) continue;
            list.Add(new Vector4(x, h - 0.2f, z, 0.7f + R() * 0.9f));
        }
        return list.ToArray();
    }

    /// <summary>Plano da água (4 vértices) e os índices.</summary>
    public static (SceneVertex[] V, uint[] I) BuildWater()
    {
        float h = TerrainSize / 2;
        return (new[]
        {
            new SceneVertex { Pos = new(-h, WaterY, -h) }, new SceneVertex { Pos = new(h, WaterY, -h) },
            new SceneVertex { Pos = new(-h, WaterY, h) }, new SceneVertex { Pos = new(h, WaterY, h) },
        }, new uint[] { 0, 2, 1, 1, 2, 3 });
    }

    // Trajeto da câmera (Catmull-Rom fechado): sobe o vale por uma margem do rio e volta pela outra.
    private static readonly Vector2[] Path = BuildPath();

    private static Vector2[] BuildPath()
    {
        var p = new List<Vector2>();
        for (int i = 0; i <= 6; i++) { float z = -330 + i * 110; p.Add(new(MathF.Sin(z * 0.007f) * 120 + 45, z)); }
        for (int i = 6; i >= 0; i--) { float z = -330 + i * 110; p.Add(new(MathF.Sin(z * 0.007f) * 120 - 75, z)); }
        return p.ToArray();
    }

    private static Vector2 PathAt(float t)
    {
        int n = Path.Length;
        float f = (t % 1 + 1) % 1 * n;
        int i = (int)f; float u = f - i;
        Vector2 p0 = Path[(i - 1 + n) % n], p1 = Path[i], p2 = Path[(i + 1) % n], p3 = Path[(i + 2) % n];
        return 0.5f * (2 * p1 + (-p0 + p2) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (-p0 + 3 * p1 - 3 * p2 + p3) * u * u * u);
    }

    private static float CamHeight(Vector2 p)
    {
        float h = WaterY;
        for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++) h = MathF.Max(h, TerrainHeight(p.X + dx * 10, p.Y + dz * 10));
        return h + 26;
    }

    /// <summary>Câmera no instante t (0 a 1 do teste): posição e matriz de visão. Pelo tempo, não pelos frames.</summary>
    public static (Vector3 Eye, Matrix4x4 View) Camera(float t)
    {
        var p2 = PathAt(t); var ahead = PathAt(t + 0.035f);
        var eye = new Vector3(p2.X, CamHeight(p2), p2.Y);
        var target = new Vector3(ahead.X, CamHeight(ahead) - 22, ahead.Y);
        return (eye, Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY));
    }
}

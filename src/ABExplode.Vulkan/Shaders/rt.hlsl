// entries: Main:cs_6_5
// options: -fspv-target-env=vulkan1.2 -fvk-use-scalar-layout
// Teste de ray tracing do RESET BENCH: o mesmo path tracer do DXR (GpuRtBench do Windows), com raios inline (RayQuery).
// Em Vulkan vira VK_KHR_ray_query. Layout escalar: os buffers têm o mesmo formato das structs do C# (como no Direct3D).
struct Frame { row_major float4x4 InvViewProj; float3 CamPos; float Time; float3 SunDir; uint FrameIndex; uint Width; uint Height; uint Spp; uint Bounces; };
[[vk::binding(0, 0)]] ConstantBuffer<Frame> F;
[[vk::binding(1, 0)]] RaytracingAccelerationStructure Scene;
struct Vtx { float3 pos; float3 nrm; };
[[vk::binding(2, 0)]] StructuredBuffer<Vtx> Verts;
[[vk::binding(3, 0)]] StructuredBuffer<uint> Indices;
struct Mesh { uint indexOffset; uint vertexOffset; uint p0; uint p1; };
[[vk::binding(4, 0)]] StructuredBuffer<Mesh> Meshes;
struct Inst { float3 albedo; float metal; float rough; float emissive; uint mesh; float pad; };
[[vk::binding(5, 0)]] StructuredBuffer<Inst> Insts;
[[vk::binding(6, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Output;

uint Pcg(inout uint s) { s = s * 747796405u + 2891336453u; uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u; return (w >> 22u) ^ w; }
float Rnd(inout uint s) { return (Pcg(s) & 0xFFFFFF) / 16777216.0; }

float3 CosineDir(float3 n, inout uint s)
{
    float u = Rnd(s), v = Rnd(s), r = sqrt(u), phi = 6.2831853 * v;
    float3 t = abs(n.y) < 0.99 ? normalize(cross(n, float3(0, 1, 0))) : float3(1, 0, 0);
    float3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0, 1 - u)));
}

float3 Sky(float3 d)
{
    float3 sky = lerp(float3(0.85, 0.9, 1.0), float3(0.25, 0.45, 0.85), saturate(d.y));
    float sun = pow(saturate(dot(d, -F.SunDir)), 900) * 60;
    return sky * 0.8 + sun * float3(1.0, 0.92, 0.8);
}

struct Hit { float3 pos; float3 nrm; Inst mat; };

bool Closest(float3 o, float3 d, out Hit h)
{
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    RayDesc r; r.Origin = o; r.TMin = 0.002; r.Direction = d; r.TMax = 2000;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, r);
    q.Proceed();
    h = (Hit)0;
    if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT) return false;
    Inst m = Insts[q.CommittedInstanceID()];
    Mesh me = Meshes[m.mesh];
    uint i0 = me.indexOffset + q.CommittedPrimitiveIndex() * 3;
    float2 bc = q.CommittedTriangleBarycentrics();
    float3 n0 = Verts[me.vertexOffset + Indices[i0]].nrm, n1 = Verts[me.vertexOffset + Indices[i0 + 1]].nrm, n2 = Verts[me.vertexOffset + Indices[i0 + 2]].nrm;
    float3 n = n0 * (1 - bc.x - bc.y) + n1 * bc.x + n2 * bc.y;
    float3x4 o2w = q.CommittedObjectToWorld3x4();
    n = normalize(mul((float3x3)o2w, n));
    if (dot(n, d) > 0) n = -n;
    h.pos = o + d * q.CommittedRayT() + n * 0.003;
    h.nrm = n; h.mat = m;
    return true;
}

bool Occluded(float3 o, float3 d, float tmax)
{
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER> q;
    RayDesc r; r.Origin = o; r.TMin = 0.002; r.Direction = d; r.TMax = tmax;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, r);
    q.Proceed();
    return q.CommittedStatus() != COMMITTED_NOTHING;
}

[numthreads(8, 8, 1)]
void Main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= F.Width || id.y >= F.Height) return;
    uint seed = (id.y * F.Width + id.x) * 9781u + F.FrameIndex * 6271u;
    float3 color = 0;
    for (uint s = 0; s < F.Spp; s++)
    {
        float2 uv = (float2(id.xy) + float2(Rnd(seed), Rnd(seed))) / float2(F.Width, F.Height);
        float4 p = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, 1, 1), F.InvViewProj);
        float3 o = F.CamPos, d = normalize(p.xyz / p.w - F.CamPos);
        float3 through = 1, acc = 0;
        for (uint b = 0; b < F.Bounces; b++)
        {
            Hit h;
            if (!Closest(o, d, h)) { acc += through * Sky(d); break; }
            acc += through * h.mat.albedo * h.mat.emissive;
            // Sombra suave do sol (direção sorteada num cone) + oclusão de ambiente (um raio no hemisfério).
            float3 sd = normalize(-F.SunDir + (float3(Rnd(seed), Rnd(seed), Rnd(seed)) - 0.5) * 0.06);
            float ndl = saturate(dot(h.nrm, sd));
            float sunVis = ndl > 0 && !Occluded(h.pos, sd, 1000) ? 1 : 0;
            float3 ad = CosineDir(h.nrm, seed);
            float ao = Occluded(h.pos, ad, 6) ? 0.15 : 1;
            float3 diffuse = h.mat.albedo * (1 - h.mat.metal);
            acc += through * diffuse * (ndl * sunVis * float3(3.6, 3.3, 2.9) + ao * float3(0.12, 0.15, 0.2));
            // Próxima rebatida: reflexo (metal) ou iluminação indireta (difuso).
            if (h.mat.metal > 0.5)
            {
                float3 rd = reflect(d, h.nrm);
                d = normalize(rd + (CosineDir(h.nrm, seed) - h.nrm) * h.mat.rough);
                through *= h.mat.albedo;
            }
            else
            {
                d = CosineDir(h.nrm, seed);
                through *= h.mat.albedo * 0.8;
            }
            o = h.pos;
            if (max(through.x, max(through.y, through.z)) < 0.02) break;
        }
        color += acc;
    }
    color /= F.Spp;
    color = color / (1 + color);               // tone mapping (Reinhard)
    color = pow(saturate(color), 1 / 2.2);     // gama
    Output[id.xy] = float4(color, 1);
}

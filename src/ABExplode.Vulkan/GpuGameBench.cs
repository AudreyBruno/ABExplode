using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ABExplode.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace ABExplode.Vulkan;

public sealed record GpuGameResult(double AvgFps, double Low1Fps, int Frames, double Seconds, int Width, int Height, string Adapter)
{
    /// <summary>Nota = FPS médio × 10.</summary>
    public double Score => AvgFps * 10;
}

/// <summary>
/// Cena de jogo do RESET BENCH em Vulkan, igual à do Direct3D 11 do Windows (mesma geometria em <see cref="GameScene"/>, mesmo
/// HLSL): terreno, 40 mil árvores por instancing, sombras (mapa 4096² com PCF), água, neblina, céu e pós-processamento HDR.
/// Renderiza fora da tela com uma fila de 8 frames e mostra na janela a ~60 Hz. O tempo de frame é o intervalo entre os
/// frames que a GPU termina. As notas de Vulkan e Direct3D não são comparáveis entre si.
/// </summary>
public sealed unsafe class GpuGameBench
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);
    public string AdapterName { get; init; } = "";

    public double Progress { get; private set; }
    public double CurrentFps { get; private set; }
    public string? Error { get; private set; }
    public bool Cancelled { get; private set; }

    private volatile bool _stop;

    public void Cancel() => _stop = true;

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameCB
    {
        public Matrix4x4 ViewProj, LightViewProj, InvViewProj;
        public Vector3 CamPos; public float Time;
        public Vector3 SunDir; public float Pad;
    }

    private const int InFlight = 8;

    public GpuGameResult? Run()
    {
        var oldPriority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest; // medição de frametime precisa de pontualidade
        IWindow? window = null;
        VkGpu? g = null;
        Scene? scene = null;
        try
        {
            // Janela que cabe na tela, mantendo a proporção da resolução de render (a cena é sempre Width x Height)
            var screen = Silk.NET.Windowing.Monitor.GetMainMonitor(null)?.VideoMode.Resolution ?? new Vector2D<int>(1920, 1080);
            double scale = Math.Min(1.0, Math.Min(screen.X * 0.85 / Width, screen.Y * 0.85 / Height));
            var opts = WindowOptions.DefaultVulkan with
            {
                Size = new Vector2D<int>((int)(Width * scale), (int)(Height * scale)), Title = "RESET BENCH · Cena de jogo (Vulkan)", VSync = false,
            };
            window = Window.Create(opts);
            window.Initialize();
            window.Closing += () => _stop = true;
            foreach (var kb in window.CreateInput().Keyboards)
                kb.KeyDown += (_, key, _) => { if (key == Key.Escape) _stop = true; };

            g = new VkGpu(window, AdapterName);
            var fb = window.FramebufferSize;
            g.CreateSwapchain(fb.X, fb.Y);
            scene = new Scene(g, Width, Height);
            var frameTimes = scene.Loop(this, window);

            if (_stop) { Cancelled = true; return null; }
            var sorted = frameTimes.OrderBy(x => x).ToArray();
            double p99 = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))] : 0;
            double secs = frameTimes.Sum() / 1000;
            return new GpuGameResult(secs > 0 ? frameTimes.Count / secs : 0, p99 > 0 ? 1000 / p99 : 0,
                                     frameTimes.Count, Duration.TotalSeconds, Width, Height, g.AdapterName);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
        finally
        {
            try { scene?.Destroy(); } catch { }
            try { g?.Dispose(); } catch { }
            try { window?.Dispose(); } catch { }
            Thread.CurrentThread.Priority = oldPriority;
        }
    }

    /// <summary>Recursos do Vulkan da cena: alvos, passes, pipelines, geometria e os frames na fila.</summary>
    private sealed class Scene
    {
        private readonly VkGpu _g;
        private readonly Vk _vk;
        private readonly uint _w, _h;
        private const Format HdrFormat = Format.R16G16B16A16Sfloat, LdrFormat = Format.B8G8R8A8Unorm, DepthFormat = Format.D32Sfloat;

        private GpuImage _shadow = null!, _hdr = null!, _depth = null!, _ldr = null!;
        private RenderPass _shadowPass, _mainPass, _postPass;
        private Framebuffer _shadowFb, _mainFb, _postFb;
        private Sampler _cmpSampler, _linSampler;
        private DescriptorSetLayout _setLayout;
        private PipelineLayout _layout;
        private DescriptorPool _descPool;
        private readonly List<Pipeline> _pipelines = new();
        private Pipeline _terrainShadow, _treeShadow, _sky, _terrain, _tree, _water, _post;

        private GpuBuffer _terrainVb = null!, _terrainIb = null!, _treeVb = null!, _treeIb = null!, _waterVb = null!, _waterIb = null!, _treeBuf = null!;
        private uint _terrainCount, _treeCount, _treeInstances, _waterCount;

        private readonly CommandBuffer[] _cmds = new CommandBuffer[InFlight];
        private readonly Fence[] _fences = new Fence[InFlight];
        private readonly GpuBuffer[] _ubos = new GpuBuffer[InFlight];
        private readonly DescriptorSet[] _sets = new DescriptorSet[InFlight];
        private readonly VkSemaphore[] _acquired = new VkSemaphore[InFlight], _done = new VkSemaphore[InFlight];

        public Scene(VkGpu g, int width, int height)
        {
            _g = g; _vk = g.Vk; _w = (uint)width; _h = (uint)height;
            CreateTargets();
            CreatePasses();
            CreateDescriptors();
            CreatePipelines();
            CreateGeometry();
            CreateFrames();
        }

        // ---------------- alvos e passes ----------------

        private void CreateTargets()
        {
            const uint sr = GameScene.ShadowRes;
            _shadow = _g.CreateImage(sr, sr, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, SampleCountFlags.Count1Bit, ImageAspectFlags.DepthBit);
            _hdr = _g.CreateImage(_w, _h, HdrFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, SampleCountFlags.Count1Bit, ImageAspectFlags.ColorBit);
            _depth = _g.CreateImage(_w, _h, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit, SampleCountFlags.Count1Bit, ImageAspectFlags.DepthBit);
            _ldr = _g.CreateImage(_w, _h, LdrFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, SampleCountFlags.Count1Bit, ImageAspectFlags.ColorBit);

            var sci = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear, MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge,
                CompareEnable = true, CompareOp = CompareOp.LessOrEqual, MaxLod = 1000,
            };
            VkGpu.Check(_vk.CreateSampler(_g.Device, in sci, null, out _cmpSampler), "criar o sampler de sombra");
            sci.CompareEnable = false;
            VkGpu.Check(_vk.CreateSampler(_g.Device, in sci, null, out _linSampler), "criar o sampler linear");
        }

        /// <summary>Render pass de um alvo de cor e/ou profundidade. Os frames e os passes usam as mesmas imagens: a
        /// dependência ampla faz cada passe esperar o anterior terminar de escrever e de ler.</summary>
        private RenderPass Pass(Format? color, AttachmentLoadOp colorLoad, ImageLayout colorFinal, Format? depth, ImageLayout depthFinal, AttachmentStoreOp depthStore)
        {
            var atts = stackalloc AttachmentDescription[2];
            uint n = 0;
            AttachmentReference colorRef = default, depthRef = default;
            if (color is Format cf)
            {
                atts[n] = new AttachmentDescription(0, cf, SampleCountFlags.Count1Bit, colorLoad, AttachmentStoreOp.Store,
                                                    AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.Undefined, colorFinal);
                colorRef = new AttachmentReference(n++, ImageLayout.ColorAttachmentOptimal);
            }
            if (depth is Format df)
            {
                atts[n] = new AttachmentDescription(0, df, SampleCountFlags.Count1Bit, AttachmentLoadOp.Clear, depthStore,
                                                    AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.Undefined, depthFinal);
                depthRef = new AttachmentReference(n++, ImageLayout.DepthStencilAttachmentOptimal);
            }
            var sub = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = color != null ? 1u : 0u, PColorAttachments = color != null ? &colorRef : null,
                PDepthStencilAttachment = depth != null ? &depthRef : null,
            };
            var deps = stackalloc SubpassDependency[2];
            deps[0] = new SubpassDependency(Vk.SubpassExternal, 0, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                                            AccessFlags.MemoryWriteBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
            deps[1] = new SubpassDependency(0, Vk.SubpassExternal, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                                            AccessFlags.MemoryWriteBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
            var rpci = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo, AttachmentCount = n, PAttachments = atts, SubpassCount = 1, PSubpasses = &sub,
                DependencyCount = 2, PDependencies = deps,
            };
            VkGpu.Check(_vk.CreateRenderPass(_g.Device, in rpci, null, out var pass), "criar render pass");
            return pass;
        }

        private Framebuffer Fb(RenderPass pass, uint w, uint h, params ImageView[] views)
        {
            fixed (ImageView* v = views)
            {
                var fci = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo, RenderPass = pass, AttachmentCount = (uint)views.Length, PAttachments = v,
                    Width = w, Height = h, Layers = 1,
                };
                VkGpu.Check(_vk.CreateFramebuffer(_g.Device, in fci, null, out var fb), "criar framebuffer");
                return fb;
            }
        }

        private void CreatePasses()
        {
            // 1) sombras: só profundidade, fica pronta para ser lida nos shaders
            _shadowPass = Pass(null, default, default, DepthFormat, ImageLayout.ShaderReadOnlyOptimal, AttachmentStoreOp.Store);
            // 2) cena em HDR: o céu cobre a tela toda (sem limpar a cor)
            _mainPass = Pass(HdrFormat, AttachmentLoadOp.DontCare, ImageLayout.ShaderReadOnlyOptimal, DepthFormat, ImageLayout.DepthStencilAttachmentOptimal, AttachmentStoreOp.DontCare);
            // 3) pós-processamento: HDR → LDR, pronta para ser copiada para a janela
            _postPass = Pass(LdrFormat, AttachmentLoadOp.DontCare, ImageLayout.TransferSrcOptimal, null, default, default);
            _shadowFb = Fb(_shadowPass, GameScene.ShadowRes, GameScene.ShadowRes, _shadow.View);
            _mainFb = Fb(_mainPass, _w, _h, _hdr.View, _depth.View);
            _postFb = Fb(_postPass, _w, _h, _ldr.View);
        }

        // ---------------- descritores e pipelines ----------------

        private void CreateDescriptors()
        {
            const ShaderStageFlags all = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
            var b = stackalloc DescriptorSetLayoutBinding[6];
            b[0] = new DescriptorSetLayoutBinding(0, DescriptorType.UniformBuffer, 1, all);
            b[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageBuffer, 1, all);
            b[2] = new DescriptorSetLayoutBinding(2, DescriptorType.SampledImage, 1, all);
            b[3] = new DescriptorSetLayoutBinding(3, DescriptorType.Sampler, 1, all);
            b[4] = new DescriptorSetLayoutBinding(4, DescriptorType.SampledImage, 1, all);
            b[5] = new DescriptorSetLayoutBinding(5, DescriptorType.Sampler, 1, all);
            var slci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 6, PBindings = b };
            VkGpu.Check(_vk.CreateDescriptorSetLayout(_g.Device, in slci, null, out _setLayout), "criar o layout de descritores");
            var sl = _setLayout;
            var plci = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &sl };
            VkGpu.Check(_vk.CreatePipelineLayout(_g.Device, in plci, null, out _layout), "criar o layout do pipeline");

            var sizes = stackalloc DescriptorPoolSize[4];
            sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, InFlight);
            sizes[1] = new DescriptorPoolSize(DescriptorType.StorageBuffer, InFlight);
            sizes[2] = new DescriptorPoolSize(DescriptorType.SampledImage, InFlight * 2);
            sizes[3] = new DescriptorPoolSize(DescriptorType.Sampler, InFlight * 2);
            var dpci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = InFlight, PoolSizeCount = 4, PPoolSizes = sizes };
            VkGpu.Check(_vk.CreateDescriptorPool(_g.Device, in dpci, null, out _descPool), "criar o pool de descritores");
        }

        /// <summary>Um pipeline da cena. fs null = só profundidade (sombras); vertexInput false = triângulo de tela cheia.</summary>
        private Pipeline Make(string vs, string? fs, RenderPass pass, bool vertexInput, bool depthTest, bool depthBias)
        {
            var vsMod = _g.LoadShader($"scene.{vs}.spv");
            var fsMod = fs != null ? _g.LoadShader($"scene.{fs}.spv") : default;
            var vsName = (byte*)Marshal.StringToHGlobalAnsi(vs);
            var fsName = (byte*)Marshal.StringToHGlobalAnsi(fs ?? "");
            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsMod, PName = vsName };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fsMod, PName = fsName };

                var vbind = new VertexInputBindingDescription(0, (uint)sizeof(SceneVertex), VertexInputRate.Vertex);
                var attrs = stackalloc VertexInputAttributeDescription[3];
                attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
                attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);
                attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32Sfloat, 24);
                var vi = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = vertexInput ? 1u : 0u, PVertexBindingDescriptions = &vbind,
                    VertexAttributeDescriptionCount = vertexInput ? 3u : 0u, PVertexAttributeDescriptions = attrs,
                };
                var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
                var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
                var rs = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.Clockwise, LineWidth = 1,
                    // Mesmo bias do Direct3D (DepthBias 800, SlopeScaledDepthBias 2): a fórmula para profundidade float é a mesma
                    DepthBiasEnable = depthBias, DepthBiasConstantFactor = depthBias ? 800 : 0, DepthBiasSlopeFactor = depthBias ? 2 : 0,
                };
                var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
                var ds = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = depthTest, DepthWriteEnable = depthTest, DepthCompareOp = CompareOp.Less,
                };
                var blendAtt = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                };
                var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = fs != null ? 1u : 0u, PAttachments = &blendAtt };
                var dyn = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
                var dy = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
                var gpci = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo, StageCount = fs != null ? 2u : 1u, PStages = stages, PVertexInputState = &vi,
                    PInputAssemblyState = &ia, PViewportState = &vp, PRasterizationState = &rs, PMultisampleState = &ms,
                    PDepthStencilState = &ds, PColorBlendState = &cb, PDynamicState = &dy, Layout = _layout, RenderPass = pass, Subpass = 0,
                };
                VkGpu.Check(_vk.CreateGraphicsPipelines(_g.Device, default, 1, in gpci, null, out var p), $"criar o pipeline {vs}/{fs}");
                _pipelines.Add(p);
                return p;
            }
            finally
            {
                Marshal.FreeHGlobal((nint)vsName);
                Marshal.FreeHGlobal((nint)fsName);
                _vk.DestroyShaderModule(_g.Device, vsMod, null);
                if (fs != null) _vk.DestroyShaderModule(_g.Device, fsMod, null);
            }
        }

        private void CreatePipelines()
        {
            _terrainShadow = Make("TerrainShadowVS", null, _shadowPass, true, true, true);
            _treeShadow = Make("TreeShadowVS", null, _shadowPass, true, true, true);
            _sky = Make("FullscreenVS", "SkyPS", _mainPass, false, false, false);
            _terrain = Make("TerrainVS", "TerrainPS", _mainPass, true, true, false);
            _tree = Make("TreeVS", "TreePS", _mainPass, true, true, false);
            _water = Make("WaterVS", "WaterPS", _mainPass, true, true, false);
            _post = Make("FullscreenVS", "PostPS", _postPass, false, false, false);
        }

        // ---------------- geometria e frames ----------------

        private void CreateGeometry()
        {
            var (tv, ti) = GameScene.BuildTerrain();
            _terrainVb = _g.UploadBuffer(tv, BufferUsageFlags.VertexBufferBit);
            _terrainIb = _g.UploadBuffer(ti, BufferUsageFlags.IndexBufferBit);
            _terrainCount = (uint)ti.Length;
            var (trv, tri) = GameScene.BuildTree();
            _treeVb = _g.UploadBuffer(trv, BufferUsageFlags.VertexBufferBit);
            _treeIb = _g.UploadBuffer(tri, BufferUsageFlags.IndexBufferBit);
            _treeCount = (uint)tri.Length;
            var trees = GameScene.PlaceTrees();
            _treeBuf = _g.UploadBuffer(trees, BufferUsageFlags.StorageBufferBit);
            _treeInstances = (uint)trees.Length;
            var (wv, wi) = GameScene.BuildWater();
            _waterVb = _g.UploadBuffer(wv, BufferUsageFlags.VertexBufferBit);
            _waterIb = _g.UploadBuffer(wi, BufferUsageFlags.IndexBufferBit);
            _waterCount = (uint)wi.Length;
        }

        private void CreateFrames()
        {
            var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _g.Pool, Level = CommandBufferLevel.Primary, CommandBufferCount = InFlight };
            fixed (CommandBuffer* p = _cmds) VkGpu.Check(_vk.AllocateCommandBuffers(_g.Device, in ai, p), "alocar os command buffers");
            var w = stackalloc WriteDescriptorSet[6];
            for (int i = 0; i < InFlight; i++)
            {
                _ubos[i] = _g.CreateBuffer((ulong)sizeof(FrameCB), BufferUsageFlags.UniformBufferBit, hostVisible: true);
                var sl = _setLayout;
                var dsai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descPool, DescriptorSetCount = 1, PSetLayouts = &sl };
                VkGpu.Check(_vk.AllocateDescriptorSets(_g.Device, in dsai, out _sets[i]), "alocar descritores");

                var ubo = new DescriptorBufferInfo(_ubos[i].Buffer, 0, (ulong)sizeof(FrameCB));
                var treeInfo = new DescriptorBufferInfo(_treeBuf.Buffer, 0, Vk.WholeSize);
                var shadowImg = new DescriptorImageInfo(default, _shadow.View, ImageLayout.ShaderReadOnlyOptimal);
                var cmp = new DescriptorImageInfo(_cmpSampler, default, ImageLayout.Undefined);
                var hdrImg = new DescriptorImageInfo(default, _hdr.View, ImageLayout.ShaderReadOnlyOptimal);
                var lin = new DescriptorImageInfo(_linSampler, default, ImageLayout.Undefined);
                WriteDescriptorSet W(uint binding, DescriptorType type) => new()
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = _sets[i], DstBinding = binding, DescriptorCount = 1, DescriptorType = type,
                };
                w[0] = W(0, DescriptorType.UniformBuffer); w[0].PBufferInfo = &ubo;
                w[1] = W(1, DescriptorType.StorageBuffer); w[1].PBufferInfo = &treeInfo;
                w[2] = W(2, DescriptorType.SampledImage); w[2].PImageInfo = &shadowImg;
                w[3] = W(3, DescriptorType.Sampler); w[3].PImageInfo = &cmp;
                w[4] = W(4, DescriptorType.SampledImage); w[4].PImageInfo = &hdrImg;
                w[5] = W(5, DescriptorType.Sampler); w[5].PImageInfo = &lin;
                _vk.UpdateDescriptorSets(_g.Device, 6, w, 0, null);

                var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
                VkGpu.Check(_vk.CreateFence(_g.Device, in fci, null, out _fences[i]), "criar fence");
                var sci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
                _vk.CreateSemaphore(_g.Device, in sci, null, out _acquired[i]);
                _vk.CreateSemaphore(_g.Device, in sci, null, out _done[i]);
            }
        }

        // ---------------- loop ----------------

        public List<double> Loop(GpuGameBench owner, IWindow window)
        {
            var frameTimes = new List<double>(20000);
            var clock = Stopwatch.StartNew();
            double lastDone = -1, lastPresent = 0, lastTitle = 0;
            int titleFrames = 0;
            long frame = 0;
            var lightVP = GameScene.LightViewProj;
            var proj = GameScene.Projection((int)_w, (int)_h);
            var lastSize = window.FramebufferSize;

            while (!owner._stop && clock.Elapsed < owner.Duration)
            {
                window.DoEvents();
                if (owner._stop || window.IsClosing) break;
                var size = window.FramebufferSize;
                if (size != lastSize && size.X > 0 && size.Y > 0) { _g.CreateSwapchain(size.X, size.Y); lastSize = size; }

                int slot = (int)(frame % InFlight);
                var fence = _fences[slot];
                if (frame >= InFlight)
                {
                    // Espera ativa (sem ceder o núcleo), como no Direct3D: ceder a vez registraria travadas que a GPU não teve.
                    while (_vk.GetFenceStatus(_g.Device, fence) == Result.NotReady && !owner._stop) Thread.SpinWait(20);
                    // Momento em que a GPU terminou um frame: intervalo = tempo de frame real da GPU.
                    double done = clock.Elapsed.TotalMilliseconds;
                    if (lastDone >= 0) frameTimes.Add(done - lastDone);
                    lastDone = done;
                    titleFrames++;
                }

                // Câmera pelo tempo (não pelos frames): a mesma cena aparece no mesmo segundo em qualquer PC
                float t = (float)(clock.Elapsed.TotalSeconds / owner.Duration.TotalSeconds);
                var (eye, view) = GameScene.Camera(t);
                var vp = view * proj;
                Matrix4x4.Invert(vp, out var inv);
                *(FrameCB*)_ubos[slot].Mapped = new FrameCB
                {
                    ViewProj = vp, LightViewProj = lightVP, InvViewProj = inv, CamPos = eye, Time = (float)clock.Elapsed.TotalSeconds, SunDir = GameScene.SunDir,
                };

                // Apresentação a ~60 Hz, sem esperar: se a janela ainda não liberou uma imagem, pula (não trava a fila da GPU)
                double now = clock.Elapsed.TotalSeconds;
                uint imageIndex = 0;
                bool present = false;
                if (now - lastPresent >= 1 / 60.0)
                {
                    var r = _g.KhrSwapchain.AcquireNextImage(_g.Device, _g.Swapchain, 0, _acquired[slot], default, ref imageIndex);
                    present = r is Result.Success or Result.SuboptimalKhr;
                    if (r == Result.ErrorOutOfDateKhr && size.X > 0 && size.Y > 0) _g.CreateSwapchain(size.X, size.Y);
                }
                _vk.ResetFences(_g.Device, 1, in fence);
                Record(_cmds[slot], slot, present ? imageIndex : null);

                var cmd = _cmds[slot];
                var waitStage = PipelineStageFlags.TransferBit;
                var acquired = _acquired[slot];
                var doneSem = _done[slot];
                var si = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd,
                    WaitSemaphoreCount = present ? 1u : 0u, PWaitSemaphores = &acquired, PWaitDstStageMask = &waitStage,
                    SignalSemaphoreCount = present ? 1u : 0u, PSignalSemaphores = &doneSem,
                };
                VkGpu.Check(_vk.QueueSubmit(_g.Queue, 1, in si, fence), "enviar o frame");
                frame++;

                if (present)
                {
                    var sc = _g.Swapchain;
                    var pi = new PresentInfoKHR
                    {
                        SType = StructureType.PresentInfoKhr, WaitSemaphoreCount = 1, PWaitSemaphores = &doneSem,
                        SwapchainCount = 1, PSwapchains = &sc, PImageIndices = &imageIndex,
                    };
                    _g.KhrSwapchain.QueuePresent(_g.Queue, in pi);
                    lastPresent = now;
                }
                if (now - lastTitle >= 0.5)
                {
                    owner.CurrentFps = titleFrames / (now - lastTitle);
                    titleFrames = 0; lastTitle = now;
                    owner.Progress = now / owner.Duration.TotalSeconds;
                    window.Title = $"RESET BENCH · Cena de jogo (Vulkan) · {_w}x{_h} · {owner.CurrentFps:0} FPS · {owner.Progress:P0}";
                }
            }
            return frameTimes;
        }

        private void Viewport(CommandBuffer cmd, uint w, uint h)
        {
            // Altura negativa: o Y do Vulkan aponta para baixo; assim a matemática do Direct3D (e o uv do mapa de sombras) fica igual
            var vp = new Viewport(0, h, w, -(float)h, 0, 1);
            var sc = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h));
            _vk.CmdSetViewport(cmd, 0, 1, in vp);
            _vk.CmdSetScissor(cmd, 0, 1, in sc);
        }

        private void Draw(CommandBuffer cmd, Pipeline p, GpuBuffer vb, GpuBuffer ib, uint count, uint instances)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, p);
            ulong offset = 0;
            var b = vb.Buffer;
            _vk.CmdBindVertexBuffers(cmd, 0, 1, in b, in offset);
            _vk.CmdBindIndexBuffer(cmd, ib.Buffer, 0, IndexType.Uint32);
            _vk.CmdDrawIndexed(cmd, count, instances, 0, 0, 0);
        }

        private void Record(CommandBuffer cmd, int slot, uint? swapIndex)
        {
            _vk.ResetCommandBuffer(cmd, 0);
            var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _vk.BeginCommandBuffer(cmd, in bi);
            var set = _sets[slot];
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);

            var clears = stackalloc ClearValue[2];
            // 1) Mapa de sombras
            clears[0] = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0));
            var rp = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo, RenderPass = _shadowPass, Framebuffer = _shadowFb,
                RenderArea = new Rect2D(default, new Extent2D(GameScene.ShadowRes, GameScene.ShadowRes)), ClearValueCount = 1, PClearValues = clears,
            };
            _vk.CmdBeginRenderPass(cmd, in rp, SubpassContents.Inline);
            Viewport(cmd, GameScene.ShadowRes, GameScene.ShadowRes);
            Draw(cmd, _terrainShadow, _terrainVb, _terrainIb, _terrainCount, 1);
            Draw(cmd, _treeShadow, _treeVb, _treeIb, _treeCount, _treeInstances);
            _vk.CmdEndRenderPass(cmd);

            // 2) Cena em HDR: céu, terreno, árvores, água
            clears[0] = new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f));
            clears[1] = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0));
            rp.RenderPass = _mainPass; rp.Framebuffer = _mainFb; rp.RenderArea = new Rect2D(default, new Extent2D(_w, _h)); rp.ClearValueCount = 2;
            _vk.CmdBeginRenderPass(cmd, in rp, SubpassContents.Inline);
            Viewport(cmd, _w, _h);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _sky);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            Draw(cmd, _terrain, _terrainVb, _terrainIb, _terrainCount, 1);
            Draw(cmd, _tree, _treeVb, _treeIb, _treeCount, _treeInstances);
            Draw(cmd, _water, _waterVb, _waterIb, _waterCount, 1);
            _vk.CmdEndRenderPass(cmd);

            // 3) Pós-processamento: HDR → LDR (tone mapping + vinheta)
            rp.RenderPass = _postPass; rp.Framebuffer = _postFb; rp.ClearValueCount = 0;
            _vk.CmdBeginRenderPass(cmd, in rp, SubpassContents.Inline);
            Viewport(cmd, _w, _h);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _post);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);

            if (swapIndex is uint idx)
            {
                // Cópia para a janela, reduzida para caber (filtro linear)
                var swap = _g.SwapImages[idx];
                _g.Barrier(cmd, swap, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit,
                           PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
                var sub = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
                var blit = new ImageBlit { SrcSubresource = sub, DstSubresource = sub };
                blit.SrcOffsets[1] = new Offset3D((int)_w, (int)_h, 1);
                blit.DstOffsets[1] = new Offset3D((int)_g.Extent.Width, (int)_g.Extent.Height, 1);
                _vk.CmdBlitImage(cmd, _ldr.Image, ImageLayout.TransferSrcOptimal, swap, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
                _g.Barrier(cmd, swap, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, AccessFlags.TransferWriteBit, 0,
                           PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);
            }
            _vk.EndCommandBuffer(cmd);
        }

        public void Destroy()
        {
            _vk.DeviceWaitIdle(_g.Device);
            for (int i = 0; i < InFlight; i++)
            {
                _g.Destroy(_ubos[i]);
                if (_fences[i].Handle != 0) _vk.DestroyFence(_g.Device, _fences[i], null);
                if (_acquired[i].Handle != 0) _vk.DestroySemaphore(_g.Device, _acquired[i], null);
                if (_done[i].Handle != 0) _vk.DestroySemaphore(_g.Device, _done[i], null);
            }
            foreach (var b in new[] { _terrainVb, _terrainIb, _treeVb, _treeIb, _waterVb, _waterIb, _treeBuf }) _g.Destroy(b);
            foreach (var p in _pipelines) _vk.DestroyPipeline(_g.Device, p, null);
            _vk.DestroyPipelineLayout(_g.Device, _layout, null);
            _vk.DestroyDescriptorPool(_g.Device, _descPool, null);
            _vk.DestroyDescriptorSetLayout(_g.Device, _setLayout, null);
            _vk.DestroySampler(_g.Device, _cmpSampler, null);
            _vk.DestroySampler(_g.Device, _linSampler, null);
            foreach (var f in new[] { _shadowFb, _mainFb, _postFb }) _vk.DestroyFramebuffer(_g.Device, f, null);
            foreach (var p in new[] { _shadowPass, _mainPass, _postPass }) _vk.DestroyRenderPass(_g.Device, p, null);
            foreach (var i in new[] { _shadow, _hdr, _depth, _ldr }) _g.Destroy(i);
        }
    }
}

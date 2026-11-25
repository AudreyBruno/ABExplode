using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace ABExplode.Vulkan;

public sealed class GpuBurnSettings
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public bool Fullscreen { get; init; }
    public int Msaa { get; init; } = 1;        // 1 = sem MSAA
    public int Shells { get; init; } = 96;     // camadas de pelo (overdraw)
    public int Iterations { get; init; }       // carga extra de ALU no pixel shader
    public string AdapterName { get; init; } = "";
}

/// <summary>
/// Stress de GPU no estilo FurMark em Vulkan, igual ao GpuBurn do Windows (Direct3D 11): torus com pelos por "shells"
/// (a malha desenhada N vezes, cada camada inflada pela normal, pixels descartados para formar os fios). Overdraw massivo
/// + shader pesado, sem limite de FPS: renderiza sem parar fora da tela e só copia para a janela a ~60 Hz.
/// </summary>
public sealed unsafe class GpuBurn
{
    private Thread? _thread;
    private volatile bool _stop;
    private long _frames, _presents;
    private double _seconds;

    public bool Running => _thread?.IsAlive == true;
    public double Fps { get; private set; }
    public long Frames => Interlocked.Read(ref _frames);
    /// <summary>Frames efetivamente exibidos na janela (~60/s; o resto é renderizado fora da tela).</summary>
    public long Presents => Interlocked.Read(ref _presents);
    public double AverageFps => _seconds > 0 ? Frames / _seconds : 0;
    public string? Error { get; private set; }
    public string AdapterUsed { get; private set; } = "";
    public bool ClosedByUser { get; private set; }

    public void Start(GpuBurnSettings s)
    {
        if (Running) return;
        _stop = false; _frames = 0; _presents = 0; _seconds = 0; Fps = 0; Error = null; ClosedByUser = false;
        _thread = new Thread(() => Run(s)) { IsBackground = true, Name = "GPU burn (Vulkan)" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(3000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { public Vector3 Pos; public Vector3 Normal; public Vector2 Uv; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 World, ViewProj;
        public float Time, ShellCount, FurLength, Iterations;
    }

    private const int InFlight = 6;

    private void Run(GpuBurnSettings s)
    {
        IWindow? window = null;
        VkGpu? g = null;
        try
        {
            // Tela cheia chega com tamanho 0 (como no Windows): usa a resolução do monitor principal
            var size = new Vector2D<int>(s.Width, s.Height);
            if (size.X <= 0 || size.Y <= 0)
                size = Silk.NET.Windowing.Monitor.GetMainMonitor(null)?.VideoMode.Resolution ?? new Vector2D<int>(1920, 1080);
            var opts = WindowOptions.DefaultVulkan with
            {
                Size = size, Title = "ABExplode · GPU burn (Vulkan)", VSync = false,
                WindowState = s.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
            };
            window = Window.Create(opts);
            window.Initialize();
            window.Closing += () => { ClosedByUser = true; _stop = true; };
            var input = window.CreateInput();
            foreach (var kb in input.Keyboards)
                kb.KeyDown += (_, key, _) => { if (key == Key.Escape) { ClosedByUser = true; _stop = true; } };

            g = new VkGpu(window, s.AdapterName);
            AdapterUsed = g.AdapterName;
            new Renderer(g, s, this, window).Loop();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            try { g?.Dispose(); } catch { }
            try { window?.Dispose(); } catch { }
            Fps = 0;
        }
    }

    /// <summary>Estado do Vulkan do teste (pipeline, alvos de renderização, frames na fila).</summary>
    private sealed class Renderer
    {
        private readonly VkGpu _g;
        private readonly Vk _vk;
        private readonly GpuBurnSettings _s;
        private readonly GpuBurn _owner;
        private readonly IWindow _window;
        private readonly SampleCountFlags _samples;
        private const Format ColorFormat = Format.B8G8R8A8Unorm, DepthFormat = Format.D32Sfloat;

        private RenderPass _pass;
        private PipelineLayout _layout;
        private Pipeline _pipeline;
        private DescriptorSetLayout _setLayout;
        private DescriptorPool _descPool;
        private readonly DescriptorSet[] _sets = new DescriptorSet[InFlight];
        private readonly GpuBuffer[] _ubos = new GpuBuffer[InFlight];
        private readonly CommandBuffer[] _cmds = new CommandBuffer[InFlight];
        private readonly Fence[] _fences = new Fence[InFlight];
        private readonly VkSemaphore[] _acquired = new VkSemaphore[InFlight], _done = new VkSemaphore[InFlight];
        private GpuBuffer _vb = null!, _ib = null!;
        private uint _indexCount;
        // Alvos fora da tela: cor (MSAA), profundidade e, com MSAA, a cor resolvida que vai para a janela
        private GpuImage? _color, _depth, _resolve;
        private Framebuffer _fb;
        private uint _w, _h;

        public Renderer(VkGpu g, GpuBurnSettings s, GpuBurn owner, IWindow window)
        {
            _g = g; _vk = g.Vk; _s = s; _owner = owner; _window = window;
            _samples = g.Samples(s.Msaa);
            var fb = window.FramebufferSize;
            g.CreateSwapchain(fb.X, fb.Y);
            CreatePass();
            CreatePipeline();
            CreateTargets();
            CreateFrames();
            BuildTorus(out var vertices, out var indices);
            _vb = g.UploadBuffer(vertices, BufferUsageFlags.VertexBufferBit);
            _ib = g.UploadBuffer(indices, BufferUsageFlags.IndexBufferBit);
            _indexCount = (uint)indices.Length;
        }

        private ImageView PresentedView => (_resolve ?? _color!).View;
        private Silk.NET.Vulkan.Image PresentedImage => (_resolve ?? _color!).Image;

        private void CreatePass()
        {
            bool msaa = _samples != SampleCountFlags.Count1Bit;
            var atts = stackalloc AttachmentDescription[3];
            // A imagem que vai para a janela termina pronta para ser copiada (TransferSrc)
            atts[0] = new AttachmentDescription(0, ColorFormat, _samples, AttachmentLoadOp.Clear, msaa ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
                                                AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.Undefined,
                                                msaa ? ImageLayout.ColorAttachmentOptimal : ImageLayout.TransferSrcOptimal);
            atts[1] = new AttachmentDescription(0, DepthFormat, _samples, AttachmentLoadOp.Clear, AttachmentStoreOp.DontCare,
                                                AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal);
            atts[2] = new AttachmentDescription(0, ColorFormat, SampleCountFlags.Count1Bit, AttachmentLoadOp.DontCare, AttachmentStoreOp.Store,
                                                AttachmentLoadOp.DontCare, AttachmentStoreOp.DontCare, ImageLayout.Undefined, ImageLayout.TransferSrcOptimal);
            var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
            var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
            var resolveRef = new AttachmentReference(2, ImageLayout.ColorAttachmentOptimal);
            var sub = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef,
                PDepthStencilAttachment = &depthRef, PResolveAttachments = msaa ? &resolveRef : null,
            };
            // Os frames na fila escrevem nas mesmas imagens: cada um espera o anterior terminar de escrever e de ser copiado
            var dep = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit | PipelineStageFlags.TransferBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            };
            var rpci = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo, AttachmentCount = msaa ? 3u : 2u, PAttachments = atts,
                SubpassCount = 1, PSubpasses = &sub, DependencyCount = 1, PDependencies = &dep,
            };
            VkGpu.Check(_vk.CreateRenderPass(_g.Device, in rpci, null, out _pass), "criar o render pass");
        }

        private void CreatePipeline()
        {
            var binding = new DescriptorSetLayoutBinding(0, DescriptorType.UniformBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
            var slci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
            VkGpu.Check(_vk.CreateDescriptorSetLayout(_g.Device, in slci, null, out _setLayout), "criar o layout de descritores");
            var setLayout = _setLayout;
            var plci = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
            VkGpu.Check(_vk.CreatePipelineLayout(_g.Device, in plci, null, out _layout), "criar o layout do pipeline");

            var vs = _g.LoadShader("burn.VS.spv");
            var ps = _g.LoadShader("burn.PS.spv");
            var vsName = (byte*)Marshal.StringToHGlobalAnsi("VS");
            var psName = (byte*)Marshal.StringToHGlobalAnsi("PS");
            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vs, PName = vsName };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = ps, PName = psName };

                var vbind = new VertexInputBindingDescription(0, (uint)sizeof(Vertex), VertexInputRate.Vertex);
                var attrs = stackalloc VertexInputAttributeDescription[3];
                attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
                attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);
                attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 24);
                var vi = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &vbind,
                    VertexAttributeDescriptionCount = 3, PVertexAttributeDescriptions = attrs,
                };
                var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
                var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
                var rs = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.Clockwise, LineWidth = 1,
                };
                var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = _samples };
                var ds = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.Less,
                };
                // Blending nas camadas: soma carga de ROP e banda de memória (como a pelagem translúcida do FurMark)
                var blendAtt = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = true, SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.Zero, AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                };
                var cb = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blendAtt };
                var dyn = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
                var dy = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dyn };
                var gpci = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages, PVertexInputState = &vi,
                    PInputAssemblyState = &ia, PViewportState = &vp, PRasterizationState = &rs, PMultisampleState = &ms,
                    PDepthStencilState = &ds, PColorBlendState = &cb, PDynamicState = &dy, Layout = _layout, RenderPass = _pass, Subpass = 0,
                };
                VkGpu.Check(_vk.CreateGraphicsPipelines(_g.Device, default, 1, in gpci, null, out _pipeline), "criar o pipeline");
            }
            finally
            {
                Marshal.FreeHGlobal((nint)vsName);
                Marshal.FreeHGlobal((nint)psName);
                _vk.DestroyShaderModule(_g.Device, vs, null);
                _vk.DestroyShaderModule(_g.Device, ps, null);
            }
        }

        private void CreateTargets()
        {
            _w = _g.Extent.Width; _h = _g.Extent.Height;
            bool msaa = _samples != SampleCountFlags.Count1Bit;
            _color = _g.CreateImage(_w, _h, ColorFormat, ImageUsageFlags.ColorAttachmentBit | (msaa ? 0 : ImageUsageFlags.TransferSrcBit),
                                    _samples, ImageAspectFlags.ColorBit);
            _depth = _g.CreateImage(_w, _h, DepthFormat, ImageUsageFlags.DepthStencilAttachmentBit, _samples, ImageAspectFlags.DepthBit);
            if (msaa) _resolve = _g.CreateImage(_w, _h, ColorFormat, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                                                SampleCountFlags.Count1Bit, ImageAspectFlags.ColorBit);
            var views = stackalloc ImageView[] { _color.View, _depth.View, msaa ? _resolve!.View : default };
            var fci = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo, RenderPass = _pass, AttachmentCount = msaa ? 3u : 2u, PAttachments = views,
                Width = _w, Height = _h, Layers = 1,
            };
            VkGpu.Check(_vk.CreateFramebuffer(_g.Device, in fci, null, out _fb), "criar o framebuffer");
        }

        private void DestroyTargets()
        {
            _vk.DestroyFramebuffer(_g.Device, _fb, null);
            _g.Destroy(_color); _g.Destroy(_depth); _g.Destroy(_resolve);
            _color = _depth = _resolve = null;
        }

        private void CreateFrames()
        {
            var poolSize = new DescriptorPoolSize(DescriptorType.UniformBuffer, InFlight);
            var dpci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = InFlight, PoolSizeCount = 1, PPoolSizes = &poolSize };
            VkGpu.Check(_vk.CreateDescriptorPool(_g.Device, in dpci, null, out _descPool), "criar o pool de descritores");
            var ai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _g.Pool, Level = CommandBufferLevel.Primary, CommandBufferCount = InFlight };
            fixed (CommandBuffer* p = _cmds) VkGpu.Check(_vk.AllocateCommandBuffers(_g.Device, in ai, p), "alocar os command buffers");
            for (int i = 0; i < InFlight; i++)
            {
                _ubos[i] = _g.CreateBuffer((ulong)sizeof(Constants), BufferUsageFlags.UniformBufferBit, hostVisible: true);
                var setLayout = _setLayout;
                var dsai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descPool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
                VkGpu.Check(_vk.AllocateDescriptorSets(_g.Device, in dsai, out _sets[i]), "alocar descritores");
                var bi = new DescriptorBufferInfo(_ubos[i].Buffer, 0, (ulong)sizeof(Constants));
                var w = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = _sets[i], DstBinding = 0, DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &bi,
                };
                _vk.UpdateDescriptorSets(_g.Device, 1, in w, 0, null);
                var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
                VkGpu.Check(_vk.CreateFence(_g.Device, in fci, null, out _fences[i]), "criar fence");
                var sci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
                _vk.CreateSemaphore(_g.Device, in sci, null, out _acquired[i]);
                _vk.CreateSemaphore(_g.Device, in sci, null, out _done[i]);
            }
        }

        private void Recreate()
        {
            var fb = _window.FramebufferSize;
            if (fb.X <= 0 || fb.Y <= 0) return;
            _vk.DeviceWaitIdle(_g.Device);
            DestroyTargets();
            _g.CreateSwapchain(fb.X, fb.Y);
            CreateTargets();
        }

        public void Loop()
        {
            var clock = Stopwatch.StartNew();
            double lastT = 0, lastPresent = 0;
            long lastFrames = 0, frame = 0;
            var lastSize = _window.FramebufferSize;
            try
            {
                while (!_owner._stop)
                {
                    _window.DoEvents();
                    if (_owner._stop || _window.IsClosing) break;
                    var size = _window.FramebufferSize;
                    if (size.X <= 0 || size.Y <= 0) { Thread.Sleep(50); continue; } // minimizada
                    if (size != lastSize) { Recreate(); lastSize = size; }

                    int slot = (int)(frame % InFlight);
                    var fence = _fences[slot];
                    _vk.WaitForFences(_g.Device, 1, in fence, true, ulong.MaxValue);

                    double now = clock.Elapsed.TotalSeconds;
                    bool present = now - lastPresent >= 1 / 60.0;
                    uint imageIndex = 0;
                    if (present)
                    {
                        var r = _g.KhrSwapchain.AcquireNextImage(_g.Device, _g.Swapchain, ulong.MaxValue, _acquired[slot], default, ref imageIndex);
                        if (r == Result.ErrorOutOfDateKhr) { Recreate(); continue; }
                        if (r != Result.Success && r != Result.SuboptimalKhr) throw new InvalidOperationException($"Vulkan: falhou ao pegar a imagem da janela ({r}).");
                    }
                    _vk.ResetFences(_g.Device, 1, in fence);

                    float t = (float)now;
                    var world = Matrix4x4.CreateRotationY(t * 0.6f) * Matrix4x4.CreateRotationX(0.55f + 0.25f * MathF.Sin(t * 0.35f));
                    var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -3.5f), Vector3.Zero, Vector3.UnitY);
                    var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, _w / (float)_h, 0.1f, 50f);
                    *(Constants*)_ubos[slot].Mapped = new Constants
                    {
                        World = world, ViewProj = view * proj, Time = t, ShellCount = _s.Shells, FurLength = 0.2f, Iterations = _s.Iterations,
                    };

                    Record(_cmds[slot], slot, present ? imageIndex : null);
                    var cmd = _cmds[slot];
                    var waitStage = PipelineStageFlags.TransferBit;
                    var acquired = _acquired[slot];
                    var done = _done[slot];
                    var si = new SubmitInfo
                    {
                        SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd,
                        WaitSemaphoreCount = present ? 1u : 0u, PWaitSemaphores = &acquired, PWaitDstStageMask = &waitStage,
                        SignalSemaphoreCount = present ? 1u : 0u, PSignalSemaphores = &done,
                    };
                    VkGpu.Check(_vk.QueueSubmit(_g.Queue, 1, in si, fence), "enviar o frame");
                    frame++;
                    Interlocked.Increment(ref _owner._frames);

                    if (present)
                    {
                        var sc = _g.Swapchain;
                        var pi = new Silk.NET.Vulkan.PresentInfoKHR
                        {
                            SType = StructureType.PresentInfoKhr, WaitSemaphoreCount = 1, PWaitSemaphores = &done,
                            SwapchainCount = 1, PSwapchains = &sc, PImageIndices = &imageIndex,
                        };
                        var pr = _g.KhrSwapchain.QueuePresent(_g.Queue, in pi);
                        if (pr is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) Recreate();
                        Interlocked.Increment(ref _owner._presents);
                        lastPresent = now;
                    }
                    _owner._seconds = now;
                    if (now - lastT >= 0.5)
                    {
                        long f = _owner.Frames;
                        _owner.Fps = (f - lastFrames) / (now - lastT);
                        lastT = now; lastFrames = f;
                        _window.Title = $"ABExplode · GPU burn (Vulkan) · {_owner.Fps:0} FPS · {_w}x{_h}" +
                                        (_samples != SampleCountFlags.Count1Bit ? $" · MSAA {(int)_samples}x" : "") + $" · {_g.AdapterName}";
                    }
                }
            }
            finally { Destroy(); }
        }

        private void Record(CommandBuffer cmd, int slot, uint? swapIndex)
        {
            _vk.ResetCommandBuffer(cmd, 0);
            var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _vk.BeginCommandBuffer(cmd, in bi);

            var clears = stackalloc ClearValue[3];
            clears[0] = new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f));
            clears[1] = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0));
            clears[2] = clears[0];
            var rpbi = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo, RenderPass = _pass, Framebuffer = _fb,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(_w, _h)), ClearValueCount = 3, PClearValues = clears,
            };
            _vk.CmdBeginRenderPass(cmd, in rpbi, SubpassContents.Inline);
            // Viewport com altura negativa: o Y do Vulkan aponta para baixo; assim a matemática do Direct3D fica igual
            var vp = new Viewport(0, _h, _w, -(float)_h, 0, 1);
            var sc = new Rect2D(new Offset2D(0, 0), new Extent2D(_w, _h));
            _vk.CmdSetViewport(cmd, 0, 1, in vp);
            _vk.CmdSetScissor(cmd, 0, 1, in sc);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
            var set = _sets[slot];
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
            ulong offset = 0;
            var vb = _vb.Buffer;
            _vk.CmdBindVertexBuffers(cmd, 0, 1, in vb, in offset);
            _vk.CmdBindIndexBuffer(cmd, _ib.Buffer, 0, IndexType.Uint32);
            _vk.CmdDrawIndexed(cmd, _indexCount, (uint)_s.Shells, 0, 0, 0);
            _vk.CmdEndRenderPass(cmd);

            if (swapIndex is uint idx)
            {
                // Cópia da imagem renderizada para a janela (o Vulkan converte o formato se a janela usar outro)
                var swap = _g.SwapImages[idx];
                _g.Barrier(cmd, swap, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit,
                           PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
                var sub = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
                var blit = new ImageBlit { SrcSubresource = sub, DstSubresource = sub };
                blit.SrcOffsets[1] = new Offset3D((int)_w, (int)_h, 1);
                blit.DstOffsets[1] = new Offset3D((int)_g.Extent.Width, (int)_g.Extent.Height, 1);
                _vk.CmdBlitImage(cmd, PresentedImage, ImageLayout.TransferSrcOptimal, swap, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Nearest);
                _g.Barrier(cmd, swap, ImageLayout.TransferDstOptimal, ImageLayout.PresentSrcKhr, AccessFlags.TransferWriteBit, 0,
                           PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);
            }
            _vk.EndCommandBuffer(cmd);
        }

        private void Destroy()
        {
            _vk.DeviceWaitIdle(_g.Device);
            DestroyTargets();
            for (int i = 0; i < InFlight; i++)
            {
                _g.Destroy(_ubos[i]);
                _vk.DestroyFence(_g.Device, _fences[i], null);
                _vk.DestroySemaphore(_g.Device, _acquired[i], null);
                _vk.DestroySemaphore(_g.Device, _done[i], null);
            }
            _g.Destroy(_vb); _g.Destroy(_ib);
            _vk.DestroyDescriptorPool(_g.Device, _descPool, null);
            _vk.DestroyPipeline(_g.Device, _pipeline, null);
            _vk.DestroyPipelineLayout(_g.Device, _layout, null);
            _vk.DestroyDescriptorSetLayout(_g.Device, _setLayout, null);
            _vk.DestroyRenderPass(_g.Device, _pass, null);
        }
    }

    private static void BuildTorus(out Vertex[] vertices, out uint[] indices)
    {
        const int seg = 160, sides = 64;
        const float R = 1f, r = 0.42f;
        vertices = new Vertex[(seg + 1) * (sides + 1)];
        for (int i = 0, k = 0; i <= seg; i++)
        {
            float u = i / (float)seg * MathF.Tau;
            for (int j = 0; j <= sides; j++, k++)
            {
                float v = j / (float)sides * MathF.Tau;
                var n = new Vector3(MathF.Cos(u) * MathF.Cos(v), MathF.Sin(v), MathF.Sin(u) * MathF.Cos(v));
                vertices[k] = new Vertex
                {
                    Pos = new Vector3(MathF.Cos(u) * R, 0, MathF.Sin(u) * R) + n * r,
                    Normal = n,
                    Uv = new Vector2(i / (float)seg, j / (float)sides)
                };
            }
        }
        indices = new uint[seg * sides * 6];
        for (int i = 0, k = 0; i < seg; i++)
            for (int j = 0; j < sides; j++)
            {
                uint a = (uint)(i * (sides + 1) + j), b = (uint)((i + 1) * (sides + 1) + j);
                indices[k++] = a; indices[k++] = b; indices[k++] = a + 1;
                indices[k++] = a + 1; indices[k++] = b; indices[k++] = b + 1;
            }
    }
}

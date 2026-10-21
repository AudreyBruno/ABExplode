# Plano de migração: ABExplode para Avalonia

Objetivo: trocar a UI WPF por **Avalonia** e separar o código por plataforma, para o app rodar no **Windows e no Linux**
reaproveitando o máximo do C# atual.

## Ponto de partida (v0.16)

- ~17,6 mil linhas de C#/XAML, app WPF `net8.0-windows`.
- **UI em code-behind:** `MainWindow.xaml` (2.905 linhas) + `MainWindow.xaml.cs` e 13 arquivos parciais `MainWindow.*.cs`; só 142 `{Binding}`.
  A migração mantém o code-behind; MVVM não é pré-requisito.
- **WPF a adaptar** concentrado no `Theme.xaml`: 29 `Trigger`, 7 `DataTrigger`, 21 `ControlTemplate`, 5 colunas de `DataGrid`.
- **Benchmarks de GPU já abrem janela Win32 própria** (`CreateWindowExW` em `GpuBurn.cs`, `GpuGameBench.cs`, `GpuRtBench.cs`):
  no Windows passam para o Avalonia sem mudança.
- Dependências só de Windows: WMI (`System.Management`), LibreHardwareMonitorLib, Direct3D 11/12 (Vortice), ETW (TraceEvent),
  DPAPI, registro, P/Invoke em `user32`/`setupapi`/`cfgmgr32`/`advapi32`/`wintrust`/`ntdll`, winget, drivers `.inf`.

## Estrutura final

```
ABExplode.sln
├─ src/ABExplode.Core/              net8.0          lógica pura: benchmarks, análise, catálogos, modelos
├─ src/ABExplode.Platform/          net8.0          interfaces (ISystemInfo, ISensors, ...)
├─ src/ABExplode.Platform.Windows/  net8.0-windows  código atual: WMI, LHM, D3D, ETW, registro
├─ src/ABExplode.Platform.Linux/    net8.0          /sys, /proc, hwmon, NVML .so, Vulkan
├─ src/ABExplode.App/               net8.0          UI Avalonia (escolhe a plataforma no início)
└─ ABExplode.csproj (WPF)           continua funcionando até a Fase 5
```

**Regra principal:** o app WPF atual **não é apagado** até a versão Avalonia ter as mesmas funções no Windows.
Assim há sempre um app funcionando.

## Fase 0: Preparação

- Criar a solution e os projetos vazios, com `Directory.Build.props` comum (versão, `Optimize=true`, `AllowUnsafeBlocks`).
- Configurar CI (GitHub Actions) com build em `windows-latest` e `ubuntu-latest`.
- **Pronto quando:** `dotnet build` passa nos dois sistemas.
- **Status: feita.** `ABExplode.sln`, `Directory.Build.props`, projetos em `src\` e `.github/workflows/build.yml`.
  O app Avalonia (`ABExplode.App`) tem dois alvos: `net8.0-windows` (usa Platform.Windows) e `net8.0` (usa Platform.Linux).
  O app WPF continua na raiz, igual, e o `publicar.yml` não mudou.

## Fase 1: Extrair o Core (sem mudar comportamento)

Mover para `ABExplode.Core` o que não depende do Windows:

| Vai direto | Precisa de ajuste pequeno |
|---|---|
| `BenchRenderer`, `GameSimBench`, `BottleneckAnalyzer`\*, `GameCatalog`, `GpuSpecDb`, `HowManyFpsClient`, `Vdf`, `ReportBuilder`, `NetworkTest`, `OverlayMetrics`, `InfoNode`, `GameRow`, `SensorRow`\*, `LightBackends`, `HardwareErrors` | `StressTester` (WMI), `MemoryBench`/`DiskBench` (registro e flags de arquivo), `AppSettings` (DPAPI) |

\* Hoje recebem tipos do LibreHardwareMonitor. Trocar por um modelo próprio (`SensorReading { Name, Type, Value, Unit }`).

- O app WPF passa a **referenciar o Core**.
- **Pronto quando:** o WPF continua igual e o RESET BENCH dá **a mesma nota** de antes
  (conferir no 7700X: ≈4.500 multi / 530 single).

## Fase 2: Separar o que é de cada plataforma

Criar as interfaces em `Platform` e mover o código atual para `Platform.Windows`:

| Interface | Implementação Windows (atual) |
|---|---|
| `ISystemInfo` | `SystemInfoCollector`, `DriveMap`, `BatteryInfo` |
| `ISensors` | `SensorMonitor` (LHM) |
| `IGpuInfo` / `IGpuControl` | `GpuInfoCollector`, `Dxgi`, `Nvml` (o NVML fica em comum, só muda o nome da biblioteca) |
| `IDiskHealth` | `DiskHealth` |
| `IFrameSource` | `FrameMonitor` (ETW) |
| `IGpuBenchmark` | `GpuBurn`, `GpuGameBench`, `GpuRtBench` |
| `IDeviceWatcher` | `WM_DEVICECHANGE` (hoje em `MainWindow`/`ToastWindow`) |
| `ISecretStore` | DPAPI (`AppSettings`) |
| `IStartupManager`, `IAppInstaller`, `ITweaks`, `IOptimizer`, `ISerialDrivers`, `IDriverManager`, `IGameLibrary`, `IHardwareErrors` | os arquivos de mesmo nome |

- Cada interface expõe `bool IsSupported`. A UI esconde as abas que a plataforma não suporta.
- O código WPF passa a chamar as interfaces, não as classes direto.
- **Pronto quando:** o WPF funciona igual, agora em cima das interfaces.

## Fase 3: UI em Avalonia, só no Windows

Portar as janelas mantendo o code-behind.

| WPF | Avalonia |
|---|---|
| `Theme.xaml` com `Trigger`/`DataTrigger` | Estilos com seletores e pseudo-classes (`:pointerover`, `:checked`, `.classe`) |
| `ControlTemplate.Triggers` | `ControlTheme` + seletores `^:pressed` |
| `Visibility` | `IsVisible` |
| `FindResource` | `this.FindResource` / `TryFindResource` |
| `Dispatcher.Invoke` | `Dispatcher.UIThread.InvokeAsync` |
| `DataGrid` | pacote `Avalonia.Controls.DataGrid` |
| `Polyline` (gráficos de sensores e frametime) | `Polyline` existe; se ficar lento, usar `Render` customizado |
| `ThemeManager` (claro/escuro) | `RequestedThemeVariant` + `ThemeDictionaries` |

Ordem, da mais simples para a mais complexa:

1. `ToastWindow`
2. Informações do sistema e Armazenamento
3. Sensores e Dashboard
4. Placa de vídeo
5. RESET BENCH (CPU, disco, memória e os 3 de GPU, que só disparam a janela nativa)
6. Jogos, Gargalo, RESET OC
7. OTIMIZA, Ferramentas, MAKER, Integração
8. **Overlay** por último: janela transparente, `Topmost` e ignorar cliques. Ignorar cliques pede código específico
   (pegar o HWND via `TryGetPlatformHandle()` e aplicar `WS_EX_TRANSPARENT | WS_EX_LAYERED`, como hoje).

- O atalho global Ctrl+Shift+O continua via `RegisterHotKey` no Windows.
- **Pronto quando:** a versão Avalonia no Windows faz tudo que a v0.16 faz e o `publicar.bat` gera o `.exe` novo.

## Fase 4: Linux básico

Implementações em `Platform.Linux`:

| Recurso | Fonte no Linux |
|---|---|
| SO, CPU, RAM, placa-mãe/BIOS | `/etc/os-release`, `/proc/cpuinfo`, `/proc/meminfo`, `/sys/class/dmi/id/*` |
| Módulos de RAM | `dmidecode -t memory` (precisa de root; sem root, mostrar só o total) |
| Unidades | `lsblk -J -o NAME,SIZE,TYPE,MOUNTPOINT,MODEL,TRAN,ROTA` + `statvfs` |
| Sensores | `/sys/class/hwmon/*` (k10temp, coretemp, nct67xx, amdgpu, nvme) |
| GPU NVIDIA | NVML via `libnvidia-ml.so.1` (`Nvml.cs` com `NativeLibrary` escolhendo o nome) |
| GPU AMD | `/sys/class/drm/card*/device` (`gpu_busy_percent`, `hwmon`, `mem_info_vram_*`, `pp_dpm_*`) |
| Dados da GPU | `lspci -vmm` + `GpuSpecDb` (o mesmo de hoje) |
| Saúde do disco | `smartctl -j -a` |
| Bateria | `/sys/class/power_supply/BAT*` |
| Benchmark de disco | `O_DIRECT` com buffer alinhado em vez de `FILE_FLAG_NO_BUFFERING` |
| Avisos de USB | netlink/udev, ou monitorar `/dev` e `/sys/bus/usb` |
| Chave da API | arquivo em `~/.config/ABExplode` com permissão 600 (ou libsecret) |
| Jogos instalados | `~/.steam/steam/steamapps/libraryfolders.vdf` (o `Vdf.cs` já lê isso) |

- **Pronto quando:** no Linux funcionam informações, sensores, Placa de vídeo (sem os benchmarks de GPU),
  benchmarks de CPU, memória e disco, Jogos e Gargalo.

## Fase 5: Funções do Linux equivalentes às do Windows

- **RESET MAKER:**
  - Diagnóstico de `/dev/ttyUSB*` e `/dev/ttyACM*` por VID:PID (`/sys/bus/usb-serial`).
  - Verificar o grupo `dialout`/`uucp` e criar regras udev.
  - Detectar o `brltty` roubando o CH340 (problema clássico no Ubuntu).
  - Não há drivers para instalar, eles já vêm no kernel.
- **Inicialização:** `~/.config/autostart/*.desktop` + `systemctl --user`.
- **Instalar apps:** Flatpak (Steam, Chrome, Discord, VLC...) ou apt/dnf conforme a distro.
- **OTIMIZA:**
  - `powerprofilesctl`/`cpupower` no lugar do plano de energia.
  - Detectar e instalar o `gamemode`.
  - Liberar cache (`drop_caches`, precisa de root) no lugar da memória em espera.
- **Erros de hardware:** `journalctl -k` (MCE, Xid da NVIDIA, reset do amdgpu) no lugar de WHEA.
- **Root:** sem `app.manifest`. As ações que precisam de root passam por `pkexec` só quando necessário;
  o app inteiro não roda como root.
- **A partir daqui**, o projeto WPF antigo pode ser removido.

## Fase 6: GPU no Linux (a mais pesada)

- **Benchmarks de GPU em Vulkan** (Silk.NET ou Vortice.Vulkan), com janela via SDL3/GLFW:
  - Shaders: compilar o HLSL atual para SPIR-V com DXC (`-spirv`). O `Vortice.Dxc` já está no projeto.
  - Ordem: **GpuBurn** (mais simples), **GpuGameBench**, **GpuRtBench** (`VK_KHR_ray_query` é o equivalente do DXR 1.1 inline).
  - **As notas Vulkan e D3D não são diretamente comparáveis.** Marcar a API no histórico.
- **Overlay e FPS:** o Linux não tem nada equivalente ao ETW. Opções:
  - **Recomendado:** detectar o **MangoHud**, ler o log dele (`MANGOHUD_LOG`) para o FPS, e manter o overlay próprio só com sensores.
  - Alternativa: usar o próprio MangoHud como overlay e configurá-lo pelo app.
  - Janela transparente sempre no topo funciona no X11. **No Wayland não**: lá vale só o MangoHud.
- **RESET OC:** NVML (`nvmlDeviceSetPowerManagementLimit`, `SetGpuLockedClocks`, precisa de root). AMD via `pp_od_clk_voltage`.

## Empacotamento

- **Windows:** o `publicar.bat` atual, com `-r win-x64`.
- **Linux:** `dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true`, distribuído como **AppImage**
  (um arquivo, roda em qualquer distro), mais `.tar.gz`. Flatpak depois, se fizer sentido
  (o sandbox atrapalha o acesso a `/sys` e `/dev`).

## Riscos e cuidados

| Risco | Cuidado |
|---|---|
| Nota do RESET BENCH mudar ao mover o código | Teste de regressão na Fase 1: mesma semente, mesma nota |
| Gráficos ao vivo lentos no Avalonia | Medir cedo, na Fase 3 (Dashboard); se preciso, desenhar com `DrawingContext` |
| Overlay no Wayland | Aceitar MangoHud como solução oficial no Linux |
| Sensores muito diferentes entre placas-mãe Linux | Mapear nomes do hwmon por chip (k10temp, coretemp, nct6798...) e mostrar os dados brutos como fallback |
| Funções que precisam de root | Isolar as ações e chamar com `pkexec`, nunca o app inteiro |

Cada fase termina com algo usável. As fases 1 e 2 não mudam nada para quem usa o app hoje,
mas são as que deixam o resto possível.

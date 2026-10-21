# Plano de migração: ABExplode para Avalonia

Objetivo: trocar a UI WPF por **Avalonia** e separar o código por plataforma, para o app rodar no **Windows e no Linux**
reaproveitando o máximo do C# atual.

## Ponto de partida (v0.16)

- ~17,6 mil linhas de C#/XAML, app WPF `net10.0-windows`.
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
├─ src/ABExplode.Core/              net10.0          lógica pura: benchmarks, análise, catálogos, modelos
├─ src/ABExplode.Platform/          net10.0          interfaces (ISystemInfo, ISensors, ...)
├─ src/ABExplode.Platform.Windows/  net10.0-windows  código atual: WMI, LHM, D3D, ETW, registro
├─ src/ABExplode.Platform.Linux/    net10.0          /sys, /proc, hwmon, NVML .so, Vulkan
├─ src/ABExplode.App/               net10.0          UI Avalonia (escolhe a plataforma no início)
└─ ABExplode.csproj (WPF)           continua funcionando até a Fase 5
```

**Regra principal:** o app WPF atual **não é apagado** até a versão Avalonia ter as mesmas funções no Windows.
Assim há sempre um app funcionando.

## Fase 0: Preparação

- Criar a solution e os projetos vazios, com `Directory.Build.props` comum (versão, `Optimize=true`, `AllowUnsafeBlocks`).
- Configurar CI (GitHub Actions) com build em `windows-latest` e `ubuntu-latest`.
- **Pronto quando:** `dotnet build` passa nos dois sistemas.
- **Status: feita.** `ABExplode.sln`, `Directory.Build.props`, projetos em `src\` e `.github/workflows/build.yml`.
  O app Avalonia (`ABExplode.App`) tem dois alvos: `net10.0-windows` (usa Platform.Windows) e `net10.0` (usa Platform.Linux).
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
- **Status: feita.**
  - Foram para `src\ABExplode.Core` (namespace `ABExplode.Core`, `global using` no WPF): `BenchRenderer`, `GameSimBench`,
    `BottleneckAnalyzer`, `GameCatalog`, `GpuSpecDb`, `HowManyFpsClient`, `Vdf`, `ReportBuilder`, `NetworkTest`,
    `OverlayMetrics`, `InfoNode`, `GameRow`, `SensorRow`, `HardwareErrors`, `StressTester`, `MemoryBench`, `DiskBench`,
    mais `DiskHealthCard` (tirado do `DiskHealth.cs`).
  - `SensorRow` usa enums próprios (`SensorKind`, `HardwareKind` em `SensorKinds.cs`); o `SensorMonitor` converte os do
    LibreHardwareMonitor pelo nome.
  - `BottleneckAnalyzer.Sample` (lê LHM + NVML) ficou no WPF como `BottleneckSampler`.
  - `StressTester` recebe a memória livre de fora (`SystemMemory.FreePhysicalBytes`, WMI, no WPF);
    `HowManyFpsClient` recebe a pasta do cache.
  - `MemoryBench`/`DiskBench` não precisaram de ajuste: usam `File.OpenHandle` com `FileOptions` 0x20000000
    (no Linux o .NET deve traduzir para `O_DIRECT`; conferir na Fase 4).
  - **Ficaram para a Fase 2:** `AppSettings` (DPAPI → `ISecretStore`; também usa `StartupBackup`) e
    `LightBackends` + `CorsairSdk` (DLL do iCUE; `ILightBackend` já é uma interface).
  - Teste de regressão em `tests\ABExplode.Core.Tests`: hash SHA-256 da imagem do RESET BENCH, gravado antes da mudança.
    Só compara no Windows (`MathF.Sin/Cos/Pow/Tan` podem diferir no último bit no Linux).

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
- **Status: feita, com escopo ajustado.**
  - Todo o backend saiu da raiz: na raiz do app WPF ficaram só as janelas, o tema e o `UsedArcConverter`.
  - **`Platform`** (comum aos dois sistemas): `IPlatform` e as interfaces `ISystemInfo`, `ISensorSource`, `IGpuInfo`,
    `IDiskHealth`, `IHardwareErrors`, `IGameLibrary`, `IFrameSource`, `ISecretStore` (todas com `IsSupported`, menos
    `ISecretStore`); os modelos que a UI usa (`GpuInfo`, `FrameStats`, `DriveCard`, `LibGame`, `StartupBackup`,
    `CpuSensorProblem`); e o que funciona igual nos dois: `AppSettings`, `Nvml` (no Linux carrega `libnvidia-ml.so.1`),
    `BottleneckSampler` e a iluminação por rede (`ILightBackend`, OpenRGB, Razer Chroma).
  - **`Platform.Windows`**: todo o código Windows (WMI, LHM, ETW, D3D, registro), os pacotes, os drivers embutidos do
    RESET MAKER (`EmbeddedResource`, mesmos nomes de recurso) e `WindowsPlatform`, que liga as interfaces às classes de antes.
    A chave da API continua no mesmo formato (base64 do DPAPI), então as chaves salvas continuam valendo.
  - O app cria a `WindowsPlatform` no `App.xaml.cs`, define `AppSettings.Secrets` e passa a plataforma para a `MainWindow`.
  - `DriveCard.UsedArc` (geometria WPF) virou o `UsedArcConverter` na UI.
  - **Ajuste de escopo:** as ferramentas que só existem no Windows continuam sendo chamadas direto pela UI, sem interface:
    `DriverManager`, `MakerDrivers`, `UsbCleaner` (ComDB), `WindowsTweaks`, `Optimizer` (fechar programas, limpar memória),
    `AppInstaller` (winget), `StartupManager` (registro), `SteamInfo`/`GameLauncher`, Corsair iCUE e os benchmarks D3D
    (`GpuBurn`, `GpuGameBench`, `GpuRtBench`). No Linux elas funcionam de outro jeito (ver Fase 5) ou viram Vulkan (Fase 6);
    uma interface desenhada agora a partir do Windows provavelmente sairia errada. Ganham interface na Fase 5/6.
  - `IDeviceWatcher` (avisos de USB) fica para a Fase 3: hoje depende da janela WPF (`WM_DEVICECHANGE`).

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
- **Status: em andamento.**
  - **Etapa 1 (feita):** base e primeiras telas em `src\ABExplode.App`.
    - `Palette.axaml` + `ThemeManager` (troca os pincéis no modo claro, como no WPF) e `Theme.axaml`: os estilos com
      template viraram `ControlTheme` (`Theme="{StaticResource PrimaryButton}"`), os de texto viraram classes
      (`Classes="h1"`), e o cartão de veredito virou o modelo `HealthCard`.
    - Janela com barra de título própria (`WindowDecorations="BorderOnly"` + `ExtendClientAreaToDecorationsHint` e
      `WindowDecorationProperties.ElementRole="TitleBar"` para arrastar), menu lateral com todas as categorias e rodapé ao vivo.
    - Telas migradas: **Sistema** (com os cartões de unidade), **Discos**, **Energia** e **Rede**. As outras abrem um
      aviso de que ainda estão no app WPF. A Visão geral ainda não foi migrada, então o app abre em Sistema.
    - Aviso de dispositivo (`ToastWindow`): `WM_DEVICECHANGE` pelo `Win32Properties.AddWndProcHookCallback` e
      `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` pelo `Win32Properties.AddWindowStylesCallback`, sem P/Invoke.
    - `Dialog` no lugar do `MessageBox`; `UsedArcConverter` com `StreamGeometry`.
    - Sem `app.manifest` de administrador durante a migração (abre sem o UAC; sem admin, falta temperatura/consumo da CPU).
    - A fonte monoespaçada é `Consolas`: com "Cascadia Mono" o Avalonia desenhava outra fonte.
    - Os itens do menu e os botões da barra de título têm `AutomationProperties.Name` (leitor de tela e testes por UI Automation).
  - **Etapa 2 (feita):** **Visão geral** e **Sensores**. O app volta a abrir na Visão geral.
    - Visão geral: gráfico de área CPU × GPU (1/5/15 min; temperatura, carga, consumo), desempenho, tabela de componentes,
      anéis de RAM/VRAM/disco, eventos recentes e saúde do PC, desenhados em `Canvas` como no WPF.
      Os itens da saúde que dependem de ferramentas só do Windows (Modo de Jogo, Game DVR, portas COM) ficam em `#if WINDOWS`.
    - Sensores: o `DataGrid` agrupado virou uma lista agrupada por hardware com as mesmas colunas (sem o pacote DataGrid);
      filtros por tipo e busca. A lista só é remontada quando o filtro muda ou aparecem sensores novos.
    - `TextBox` no visual do app (borda, foco, `PlaceholderText` no lugar do `Tag`).
  - **Etapa 3 (feita):** **Placa de vídeo**: destaque, seções em 3 colunas, leituras do NVML (P-state, PerfCap, NVENC...)
    e sensores ao vivo da placa, ordenados como no GPU-Z; chips para escolher a placa quando há mais de uma.
    A lista ao vivo era uma `ListCollectionView` filtrada e ordenada; aqui é remontada só quando aparecem sensores novos.
  - **Etapa 4 (feita):** **RESET BENCH** (render de CPU, cena de jogo, ray tracing, simulação, memória, disco e históricos)
    e o **relatório exportável** na tela Sistema.
    - Imagem do render e da simulação num `WriteableBitmap` do Avalonia, copiada linha a linha a cada 100 ms.
    - As barras de progresso (`ScaleTransform` no WPF) viraram `ProgressBar` com a classe `bar`.
    - Cena de jogo e ray tracing (Direct3D) em `#if WINDOWS`; no Linux avisam que ainda não estão disponíveis (Fase 6).
    - "Salvar como" do relatório pelo `StorageProvider`.
    - **Timers em `DispatcherPriority.Normal`:** no Avalonia o padrão é `Background`, e com o timer da imagem em `Render`
      o timer de 1 s dos sensores ficava sem vez durante o render (a carga aparecia parada em ~6%).
    - Ainda não desliga o overlay durante os testes de GPU (o overlay ainda não foi migrado).
  - **Etapa 5 (feita):** **RESET JOGOS** (Biblioteca e FPS estimado) e **Gargalo**.
    - Biblioteca: a lista vem da plataforma (`IGameLibrary`); achar o .exe, montar os argumentos, opções da Steam e
      JOGAR usam `GameLauncher`/`SteamInfo` e ficam em `#if WINDOWS` (no Linux avisam que ainda não estão disponíveis).
      Janelas de escolher arquivo pelo `StorageProvider`.
    - FPS estimado: estimativa local e HowManyFPS; a chave usa um `TextBox` com `PasswordChar` (no lugar do `PasswordBox`).
    - Gargalo: equilíbrio GPU × CPU, veredito ao vivo (cartão invertido quando o limite é um problema), leituras ao vivo
      e análise de sessão; o medidor de FPS é o `IFrameSource` da plataforma.
  - **Etapa 6 (feita):** **Stress test** e **RESET OC**.
    - Stress test: CPU, RAM e GPU (FurMark, `GpuBurn`, só Windows), leituras, gráfico de temperatura com os limites,
      registro e corte automático; roda a cada segundo mesmo com a tela fechada, como no WPF. Benchmarks e teste de
      rede esperam o stress test terminar.
    - RESET OC: limite de energia e teto de clock pelo NVML (comum aos dois sistemas), verificador de estabilidade
      (a parte de GPU usa `GpuGameBench`/`GpuBurn` e fica em `#if WINDOWS`), erros recentes, histórico e guia de BIOS.
      O código veio de um `MainWindow.Oc.cs` com cara de descompilado; foi reescrito legível, com o mesmo comportamento.
    - `Slider` monocromático pelas chaves do tema Fluent (`SliderTrackFill`, `SliderTrackValueFill`, `SliderThumbBackground`),
      trocadas também no modo claro pelo `ThemeManager`.
  - **Etapa 7 (feita):** **RESET FERRAMENTAS** (Otimização, Inicialização, Instalar apps, Drivers, Integração).
    - Otimização, Inicialização, Instalar apps e Drivers usam ferramentas só do Windows (`MainWindow.Tools.cs` em
      `#if WINDOWS`); no Linux os botões avisam que ainda não estão disponíveis (Fase 5). A barra de RAM vem da plataforma.
    - Os modelos de item dessas telas são tipos do `Platform.Windows`, que o alvo Linux não referencia: os `DataTemplate`
      usam binding por reflexão (`x:CompileBindings="False"`) para compilar nos dois alvos.
    - Integração: OpenRGB e Razer Chroma nos dois sistemas; Corsair iCUE (DLL) em `#if WINDOWS`. Timer dos efeitos em
      `DispatcherPriority.Normal`. O quadro de diagnóstico rola o `ScrollViewer` de dentro: mover o cursor ou
      `ScrollToLine` faziam a página inteira rolar até ele.
  - **Etapa 8 (feita):** **RESET MAKER** (Portas COM, Drivers seriais) em `MainWindow.Maker.cs`, em `#if WINDOWS`
    (SetupAPI, ComDB e pnputil via `UsbCleaner` e `MakerDrivers`); no Linux os botões avisam que ainda não estão
    disponíveis (Fase 5: `/dev/ttyUSB*`, `/dev/ttyACM*`, grupo `dialout`). As duas telas se atualizam sozinhas quando uma
    placa é plugada ou removida (`WM_DEVICECHANGE`).
  - **Etapa 9 (feita, falta testar com jogo):** **Overlay**: tela de configuração, janela transparente sobre o jogo,
    atalho Ctrl+Shift+O (`RegisterHotKey` pelo hook de WndProc) e pausa durante os benchmarks de GPU.
  - **Instância única (feita):** `Mutex` em `Program.cs` (só Windows); abrir de novo traz a janela aberta para a frente.
  - **`app.manifest` de administrador (feito):** em `src\ABExplode.App`, só no alvo `net10.0-windows`.
  - **Próximas etapas:** testar o overlay sobre um jogo (clique-através e atalho); trocar a publicação
    (`publicar.bat` e `publicar.yml`) para o app Avalonia, que ainda gera o WPF.


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

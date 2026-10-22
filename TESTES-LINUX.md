# Testes no Linux

Roteiro para conferir as Fases 4 a 6 do `MIGRACAO-AVALONIA.md` num PC com Linux. Marque cada item. Quando algo falhar,
anote o item, o que apareceu e o que era esperado (veja [Como relatar](#como-relatar) no final).

Depois desta rodada, o projeto WPF antigo pode ser removido.

## 0. Preparação

**Onde baixar:** o push nesta branch gera as versões de desenvolvimento na aba **Actions** do GitHub (execução do
"Build" → Artifacts). Baixe o AppImage, o Flatpak e o executável avulso.

**Em que testar.** Quanto mais combinações, melhor; a primeira é a principal:

- [ ] Ubuntu (ou Mint/Pop!_OS), GNOME, **Wayland**
- [ ] A mesma distro em **X11** (na tela de login: "Ubuntu em Xorg")
- [ ] Outra família, se der: Fedora (dnf), Arch/CachyOS (pacman) ou openSUSE (zypper)
- [ ] Live USB (sem nada instalado: bom para testar as dependências)

**Placa de vídeo.** Anote qual foi usada em cada teste:

- [ ] NVIDIA com driver proprietário
- [ ] NVIDIA com nouveau (só para ver os avisos)
- [ ] AMD (Mesa)
- [ ] Intel (Mesa)
- [ ] Notebook com duas GPUs (Intel/AMD + NVIDIA)

## 1. Pacotes e abertura

### AppImage
- [ ] Depois de dar permissão de execução, abre com dois cliques no gerenciador de arquivos
- [ ] Abre pelo terminal: `./ABExplode-*.AppImage`
- [ ] Sem FUSE (live USB costuma não ter): `./ABExplode-*.AppImage --appimage-extract-and-run`

### Flatpak
- [ ] `flatpak install --user ABExplode-*.flatpak` instala (baixa o runtime freedesktop 25.08 do Flathub)
- [ ] Aparece no menu de aplicativos, com ícone e nome certos
- [ ] Abre pelo menu e por `flatpak run io.github.audreybruno.ABExplode`
- [ ] As leituras que usam comandos do sistema funcionam dentro do Flatpak: lspci, smartctl, journalctl e pkexec
  (Placa de vídeo, Discos e Erros de hardware)

### Executável avulso
- [ ] `tar -xzf ABExplode-linux-*.tar.gz && ./ABExplode` abre

### Dependências
- [ ] Num live USB ou numa instalação mínima, sem as bibliotecas gráficas: explica o que falta e oferece instalar
  - [ ] pelo terminal: pergunta ali mesmo
  - [ ] com dois cliques: janela do zenity ou do kdialog, ou no mínimo uma notificação
- [ ] Com a janela aberta: o aviso lista o que completa as leituras (pciutils, smartmontools, util-linux, vulkan-tools
  sem NVIDIA, módulo de sensores da CPU)
- [ ] INSTALAR abre a janela de senha do sistema (pkexec), instala, e as leituras aparecem sem reabrir o app
- [ ] "Agora não" volta a avisar na próxima abertura; "Não lembrar mais" não volta
- [ ] `./ABExplode --deps` confere e instala pelo terminal

### Instância única
- [ ] Abrir o app de novo, com ele já aberto, traz a janela que já estava aberta (não abre outra)

## 2. Leitura do sistema (`--diag`)

- [ ] `./ABExplode --diag > diag.txt` termina sem erro; confira cada seção (sistema, CPU, RAM, discos, sensores, GPU,
  bateria, rede, jogos)
- [ ] `sudo ./ABExplode --diag > diag-root.txt` mostra também o consumo da CPU (RAPL), o S.M.A.R.T. e os módulos de RAM
  (dmidecode)
- [ ] Os nomes e números batem com `lscpu`, `free -h`, `lsblk`, `sensors` e `nvidia-smi`

> O `diag.txt` tem números de série: revise antes de mandar para alguém.

## 3. Páginas

### Visão geral
- [ ] Os cartões de CPU, GPU, RAM e disco mostram valores e se atualizam
- [ ] O gráfico troca entre Temperatura, Carga e Consumo
- [ ] RODAR BENCHMARK e RESET OTIMIZA levam às páginas certas

### ABExplode
- [ ] **Sistema:** SO e versão, CPU (modelo, núcleos, caches), placa-mãe e BIOS, módulos de RAM (com ou sem root,
  conforme o systemd)
- [ ] **Sensores:**
  - [ ] temperatura da CPU (k10temp, coretemp ou zenpower)
  - [ ] carga e clock por núcleo
  - [ ] GPU (NVML, amdgpu ou i915)
  - [ ] placa-mãe, NVMe e ventoinhas, quando o chip tiver driver
  - [ ] sem o módulo de sensores, o app avisa e o CORRIGIR carrega o módulo
- [ ] **Placa de vídeo:**
  - [ ] nome, VRAM, clocks, driver, barramento PCIe e dados do GpuSpecDb
  - [ ] NVIDIA: VBIOS, limites de energia e CUDA
- [ ] **Discos:**
  - [ ] lista de discos e partições, inclusive LUKS e LVM, com o disco físico certo
  - [ ] saúde pelo smartctl, com senha
  - [ ] sem smartmontools, o cartão explica como liberar
- [ ] **Energia:**
  - [ ] bateria em notebook: carga, desgaste e ciclos
  - [ ] perfil de energia (power-profiles-daemon) e GameMode
- [ ] **Rede:** interfaces, velocidade do link e diagnóstico de lag (ping e perda)

### RESET JOGOS
- [ ] **Biblioteca:**
  - [ ] lista os jogos da Steam nativa, da Steam Flatpak, da Steam Snap e do Heroic (Epic e GOG)
  - [ ] JOGAR abre o jogo pela loja (Steam ou Heroic)
  - [ ] um binário do Linux abre direto; um `.exe` abre pelo Wine
  - [ ] o app percebe o jogo aberto e fechado (inclusive pelo Proton)
  - [ ] perfil "Alto desempenho": muda o perfil de energia para Desempenho durante o jogo e volta depois
  - [ ] "GPU dedicada" em notebook com duas GPUs: o jogo roda na dedicada (no MangoHud, ou na carga da GPU)
  - [ ] prioridade "Alta" usa o `gamemoderun` (`gamemoded -s` diz "active" durante o jogo)
  - [ ] APLICAR NAS OPÇÕES DA STEAM grava os prefixos antes de `%command%` (com a Steam fechada: confira nas
    propriedades do jogo)
  - [ ] "Tela cheia" e "Desativar otimizações de tela cheia do Windows" não aparecem
- [ ] **FPS estimado:** a estimativa por jogo, preset e resolução aparece (precisa da chave da API, se você usa)
- [ ] **Gargalo:** a análise roda e o resultado faz sentido para o PC
- [ ] **Overlay (MangoHud):**
  - [ ] INSTALAR O MANGOHUD instala pela distro (e a camada do Flatpak se a Steam for Flatpak)
  - [ ] APLICAR ESTAS ESCOLHAS grava o `~/.config/MangoHud/MangoHud.conf`
    - [ ] um arquivo feito à mão só é trocado depois de perguntar, e fica uma cópia
  - [ ] com "Abrir os jogos da Biblioteca com o MangoHud", o jogo abre com o overlay, no canto, tamanho e itens
    escolhidos
  - [ ] Ctrl + Shift + O dentro do jogo liga e desliga
  - [ ] GNOME: o app cria o atalho do sistema para `--toggle-overlay`
  - [ ] KDE e outros: o app mostra o comando para colar nas configurações de teclado

### RESET DESEMPENHO
- [ ] **Benchmark:**
  - [ ] CPU (render): roda e dá nota; ESC/CANCELAR para
  - [ ] Cena de jogo (GPU, Vulkan): abre a janela, mostra o FPS e termina com nota; no histórico sai marcado "Vulkan"
  - [ ] Ray tracing (Vulkan):
    - [ ] numa RTX 20+, RX 6000+ ou Arc: roda o aquecimento e as 3 passadas, a imagem tem reflexos e sombras, e o
      resultado diz se ficou confiável
    - [ ] numa placa sem RT: a mensagem diz o que é preciso
  - [ ] Simulação de jogo (CPU), Memória e carregamento, e Disco: rodam e dão resultado
  - [ ] O teste de disco roda na pasta pessoal e não reclama de permissão
- [ ] **Stress test:**
  - [ ] CPU e RAM: roda; o limite de temperatura para o teste
  - [ ] GPU (Vulkan): abre a janela; as resoluções e as intensidades mudam a carga
- [ ] **Overclock (RESET OC):**
  - [ ] NVIDIA: APLICAR pede a senha e muda o limite de energia e o teto de clock (`nvidia-smi -q -d POWER,CLOCK`)
  - [ ] AMD: APLICAR muda o limite de energia (`power1_cap`)
  - [ ] RESTAURAR PADRÃO volta os valores; depois de reiniciar, os valores também voltam
  - [ ] O verificador roda o stress de GPU e dá a nota de GPU (Vulkan)

### RESET FERRAMENTAS
- [ ] **Otimização:**
  - [ ] a lista de programas para fechar tem os apps abertos, e a Steam, o áudio e a sessão gráfica não aparecem
  - [ ] fechar encerra os programas escolhidos (pela janela, sem matar à força)
  - [ ] limpar memória pede a senha e a memória em cache cai (`free -h`)
  - [ ] perfil Desempenho (sem senha) e GameMode (instala, se faltar)
  - [ ] `vm.max_map_count`, `split_lock_mitigate` e `swappiness`: pedem a senha uma vez e continuam depois de reiniciar
    (`sysctl vm.max_map_count`)
  - [ ] Restaurar originais volta tudo numa pedida de senha só
- [ ] **Inicialização:**
  - [ ] lista o autostart do usuário e do sistema e os serviços do usuário (systemd)
  - [ ] desativar: o programa não abre no próximo login, e ainda dá para religar
  - [ ] remover e depois Restaurar removidos
- [ ] **Instalar apps:**
  - [ ] instala do Flathub sem senha (teste um app pequeno, como o VLC)
  - [ ] o que já está instalado (Flatpak ou distro) aparece como instalado
  - [ ] sem Flatpak no sistema, o botão o instala
- [ ] **Drivers:**
  - [ ] NVIDIA no Ubuntu: mostra o driver recomendado pelo `ubuntu-drivers`; com nouveau, avisa
  - [ ] Fedora: oferece o `akmod-nvidia` (RPM Fusion)
  - [ ] AMD/Intel: mostra a versão do Mesa
  - [ ] Firmware pelo fwupd: lista as atualizações, se houver
  - [ ] Dispositivos: cada um com o módulo do kernel (do kernel, de fora ou proprietário)
  - [ ] O backup de drivers não aparece
- [ ] **Integração (iluminação):**
  - [ ] OpenRGB aberto com o servidor SDK ligado: o app acha os dispositivos e muda as cores
  - [ ] Corsair iCUE: mostra o aviso para usar o OpenRGB

### RESET MAKER
Para estes testes, use uma placa com CH340, CP2102 ou FTDI, ou um Arduino Uno/Nano.

- [ ] **Portas COM:**
  - [ ] a placa aparece em `/dev/ttyUSB*` ou `/dev/ttyACM*`, com o nome certo
  - [ ] sem o grupo `dialout` (ou `uucp` no Arch): o app avisa, e ENTRAR NO GRUPO pede a senha e diz para sair e entrar
    na sessão
  - [ ] Ubuntu com o brltty pegando o CH340 (a porta some segundos depois de plugar): o app avisa, e DESATIVAR resolve
    (REATIVAR desfaz)
  - [ ] Plugar e tirar a placa atualiza a tela sozinho
- [ ] **Drivers seriais:**
  - [ ] o diagnóstico de cada placa: porta criada, sem permissão, pega pelo brltty, sem driver
  - [ ] REINSTALAR reconecta a placa
  - [ ] Arduino IDE: instala e atualiza pelo Flatpak
  - [ ] Com o Serial Monitor aberto noutro programa, ele aparece como "programa com a porta aberta"
  - [ ] Limpar cache apaga o `~/.arduino15`

## 4. Geral

- [ ] **Ícones:** os da barra lateral e os botões de minimizar, maximizar e fechar aparecem
- [ ] **Avisos de dispositivo:** pendrive, disco USB e placas USB (o toast aparece ao plugar e ao tirar)
- [ ] **Chave da API:** é salva e continua depois de reabrir (`~/.config/ABExplode/secret.key` com permissão 600)
- [ ] **Relatório:** exporta em HTML e em JSON
- [ ] **Configurações:** ficam em `~/.config/ABExplode` (no Flatpak, em
  `~/.var/app/io.github.audreybruno.ABExplode/config/ABExplode`) e nada é gravado como root
- [ ] **Desempenho da interface:** com os sensores ligados, o app não pesa na CPU (`top`) e não engasga ao trocar de
  página
- [ ] **Escala:** com 125% e 150% no GNOME, a janela e as fontes ficam certas

## Como relatar

Para cada item que falhou, mande:

1. **Identificação:** o número da seção e o nome do item (ex.: "3 · Overclock · AMD: APLICAR").
2. **Ambiente:** distro e versão, X11 ou Wayland, placa de vídeo e driver, e o formato usado (AppImage, Flatpak ou avulso).
3. **O resultado:** o que aconteceu e o que era esperado, com um print se for na tela.
4. **Saída do terminal:** abra o app pelo terminal e copie o que ele escreveu (`./ABExplode 2>&1 | tee abexplode.log`).
5. **Se for leitura de hardware:** o trecho do `diag.txt` da seção.

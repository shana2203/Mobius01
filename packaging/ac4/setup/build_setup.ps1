# Builds Desktop\Mobius01-Setup-<date>.exe, the installation wizard:
#   game.tar.xz       the game as make_package.ps1 stages it, plus
#                     Desinstalar.exe, minus ps2_image.bin (rebuilt from the
#                     player's ISO at install time)
#   toolchain.tar.xz  only the UCRT64 packages the per-PC compile needs and
#                     their dependencies (toolchain_files.py), with pacman's
#                     records of them; no bash, pacman, Python or docs
# Both use xz -9e (LZMA2) and are unpacked by Windows' own tar.exe.
# Needs MSYS2 in C:\msys64 (tar, xz) and Python.   .\build_setup.ps1 [-RebuildToolchain]
# -Release: the public download (GitHub Releases).  It carries the port's
# source and the toolchain but neither the recompiled game code (generated\)
# nor a prebuilt ac4.exe: on the first start Jugar.exe generates the code from
# the player's own SLUS_201.52 and compiles it.  Output Mobius01-Setup-v<Version>.exe
param([switch]$RebuildToolchain, [switch]$Release, [string]$ReleaseVersion = '1.0')
$ErrorActionPreference = 'Stop'

$here   = $PSScriptRoot
$config = (Resolve-Path (Join-Path $here '..\config')).Path
$core   = (Resolve-Path (Join-Path $here '..\installer\InstallerCore.cs')).Path
$csc    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$msys   = 'C:\msys64'
$credits = (Resolve-Path (Join-Path $here '..\..\..\..\..\AC4Recomp\CREDITOS.md')).Path
$creditsEn = (Resolve-Path (Join-Path $here '..\..\..\..\..\AC4Recomp\CREDITS.md')).Path
$bash   = Join-Path $msys 'usr\bin\bash.exe'
$work   = Join-Path $env:TEMP 'AC4Recomp-setup-build'
$cache  = Join-Path (Resolve-Path (Join-Path $here '..\..\..\..')).Path 'setup-cache'   # AC4Recomp-desarrollo\setup-cache
$date   = Get-Date -Format 'yyyyMMdd'
$version = Get-Date -Format 'yyyy.MM.dd'
$out    = Join-Path ([Environment]::GetFolderPath('Desktop')) "Mobius01-Setup-$date.exe"
if ($Release) {
    $version = $ReleaseVersion
    $out = Join-Path ([Environment]::GetFolderPath('Desktop')) "Mobius01-Setup-v$ReleaseVersion.exe"
}
# play.ico carries the game's logo: the public download uses our own icon
$appIcon = if ($Release) { 'icon.ico' } else { 'play.ico' }
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work, $cache | Out-Null

function Posix($p) { '/' + $p.Substring(0, 1).ToLower() + ($p.Substring(2) -replace '\\', '/') }
function Sh($cmd) {
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'
    & $bash -lc $cmd
    if ($LASTEXITCODE -ne 0) { throw "fallo: $cmd" }
}
function Compile($outFile, [string[]]$extra) {
    $refs = '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll',
            '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'
    & $csc /nologo /target:winexe /optimize+ /platform:x64 /codepage:65001 @refs `
        "/win32icon:$config\$appIcon" "/win32manifest:$here\setup.manifest" `
        "/resource:$config\build-bg.jpg,AC4.Background.jpg" "/resource:$credits,CREDITOS.md" "/resource:$creditsEn,CREDITS.md" @extra "/out:$outFile" `
        "$here\AC4Setup.cs" "$work\Payload.g.cs" "$config\Theme.cs" "$config\Builder.cs" $core
    if ($LASTEXITCODE -ne 0) { throw "csc fallo ($outFile)" }
}
function Payload($game, $tools, [string[]]$pkgs) {
    $list = ($pkgs | ForEach-Object { '"' + $_ + '"' }) -join ', '
@"
static class PayloadInfo
{
    public const string Version = "$version";
    public const int GameEntries = $game;
    public const int ToolEntries = $tools;
    public static readonly string[] ToolPackages = { $list };
}
"@ | Set-Content -Path "$work\Payload.g.cs" -Encoding UTF8
}

# 1. uninstaller (no payload needed)
Payload 0 0 @()
$uninst = Join-Path $work 'Desinstalar.exe'
Compile $uninst @('/define:UNINSTALLER')

# 2. the game
$stage = & (Join-Path $here '..\..\make_package.ps1') -StageOnly | Select-Object -Last 1
if (-not (Test-Path (Join-Path $stage 'Jugar.exe'))) { throw "make_package.ps1 no preparo la carpeta del juego ($stage)" }
Copy-Item $uninst $stage
# the launchers, compiled from this source (not the copies of the game folder,
# which may be older or in use); the public download gets our own icon
$refs = '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll'
$cs = "$config\Config.cs", "$config\Builder.cs", "$config\Theme.cs"
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/win32icon:$config\icon.ico" @refs "/resource:$config\build-bg.jpg,AC4.Background.jpg" "/out:$stage\Configurar AC4.exe" @cs
if ($LASTEXITCODE -ne 0) { throw 'csc fallo (Configurar AC4.exe)' }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /define:PLAY_LAUNCHER "/win32icon:$config\$appIcon" @refs "/resource:$config\build-bg.jpg,AC4.Background.jpg" "/out:$stage\Jugar.exe" @cs
if ($LASTEXITCODE -ne 0) { throw 'csc fallo (Jugar.exe)' }
Remove-Item (Join-Path $stage 'VulkanRuntime\runtime\ps2_image.bin') -ErrorAction SilentlyContinue
# game data never ships: the source's copy of the image too (the installer
# makes gamedata\ps2_image.bin from the player's ISO)
Remove-Item (Join-Path $stage 'VulkanRuntime\source\generated\ps2_image.bin') -ErrorAction SilentlyContinue
if (Get-ChildItem $stage -Recurse -Filter 'ps2_image.bin') { throw 'ps2_image.bin sigue en el paquete del instalador' }
if ($Release) {
    # the public download: no game code or data, no prebuilt game executable
    $src = Join-Path $stage 'VulkanRuntime\source'
    if (-not (Test-Path (Join-Path $src 'tools\ps2recomp\__main__.py'))) { throw 'falta tools\ps2recomp en el codigo del paquete' }
    if (-not (Test-Path (Join-Path $src 'config\ac4\vu1_recipe.json'))) { throw 'falta config\ac4\vu1_recipe.json' }
    foreach ($p in 'generated', 'out', 'build', 'SLUS_201.52') {
        Remove-Item (Join-Path $src $p) -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item (Join-Path $stage 'VulkanRuntime\runtime\ac4.exe') -Force -ErrorAction SilentlyContinue
    # nothing made from the game's files: the Spanish mod (text patches, HUD,
    # cards, movie overlays) is generated from the player's ISO on the first
    # start (tools/ac4_espanol_mod.py), and the retouched HD textures, which
    # have no generator, stay out; the test mod es419_prueba too
    foreach ($p in 'VulkanRuntime\mods\espanol', 'VulkanRuntime\mods\es419_prueba', 'VulkanRuntime\runtime\textures',
                   'VulkanRuntime\mods\mision_libre\files') {
        Remove-Item (Join-Path $stage $p) -Recurse -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem (Join-Path $stage 'VulkanRuntime\icons') -Filter *.ico -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item "$config\icon.ico" $_.FullName -Force }
    Copy-Item "$config\icon.ico" (Join-Path $src 'packaging\ac4\config\play.ico') -Force
    Copy-Item "$config\icon-hd.png" (Join-Path $src 'packaging\ac4\config\play-hd.png') -Force
    # our translation only, for tools/ac4_espanol_mod.py (the Spanish mod made
    # from the player's disc when the game has none)
    $loc = Join-Path (Resolve-Path (Join-Path $here '..\..\..')).Path 'localization'
    $dl = Join-Path $src 'localization'
    New-Item -ItemType Directory -Force (Join-Path $dl 'es-419-propio\es'), (Join-Path $dl 'es-419') | Out-Null
    Copy-Item (Join-Path $loc 'es-419-propio\es\*.json') (Join-Path $dl 'es-419-propio\es')
    Copy-Item (Join-Path $loc 'es-419-propio\README.md') (Join-Path $dl 'es-419-propio')
    Copy-Item (Join-Path $loc 'es-419\ejecutable.json') (Join-Path $dl 'es-419')
    if (-not (Test-Path (Join-Path $src 'tools\ac4_espanol_mod.py'))) { throw 'falta tools\ac4_espanol_mod.py' }
    $bad = Get-ChildItem $stage -Recurse -File | Where-Object {
        $_.Name -like 'ps2_code_*.c' -or $_.Name -like 'ps2_vu1_progs*' -or $_.Name -like 'SLUS_*' -or
        $_.Name -in 'ac4.exe', 'ps2_funcs.h', 'ps2_func_table.c', 'fuente_en.json', 'catalogo_en.json', 'elf_strings.json', 'hud_part0.bin' -or
        $_.Name -like '*.PARTS' -or $_.Extension -in '.pam', '.ppm', '.bin', '.iso', '.smv', '.cdp' }
    # (the uninstaller and launchers are .exe; no .bin of any kind ships)
    if ($bad) { throw ('archivos del juego en el paquete release: ' + ($bad.FullName -join ', ')) }
}
$gameEntries = (Get-ChildItem $stage -Recurse -Force).Count
$gameXz = Join-Path $work 'game.tar.xz'
Sh "cd '$(Posix $stage)' && tar -cf - --owner=0 --group=0 * | xz -9e -T0 > '$(Posix $gameXz)'"
Remove-Item (Split-Path $stage -Parent) -Recurse -Force
'game.tar.xz: {0} entradas, {1:N1} MB' -f $gameEntries, ((Get-Item $gameXz).Length / 1MB)

# 3. the toolchain (cached; rebuilt when the installed packages change)
$toolXz = Join-Path $cache 'toolchain.tar.xz'
$listFile = Join-Path $cache 'toolchain-files.txt'
$pkgFile = Join-Path $cache 'toolchain-packages.txt'
$dbTime = (Get-ChildItem (Join-Path $msys 'var\lib\pacman\local') -Directory | Measure-Object LastWriteTime -Maximum).Maximum
if ($RebuildToolchain -or -not (Test-Path $toolXz) -or (Get-Item $toolXz).LastWriteTime -lt $dbTime) {
    python -I (Join-Path $here 'toolchain_files.py') $msys $listFile $pkgFile
    if ($LASTEXITCODE -ne 0) { throw 'toolchain_files.py fallo' }
    $tmpXz = "$toolXz.tmp"
    # paths in the archive start with msys64/ so the setup unpacks it into C:\
    Sh "cd / && tar -cf - --owner=0 --group=0 --transform 's,^,msys64/,' -T '$(Posix $listFile)' | xz -9e -T0 > '$(Posix $tmpXz)'"
    Move-Item $tmpXz $toolXz -Force
}
$toolEntries = (Get-Content $listFile).Count
$pkgs = Get-Content $pkgFile | ForEach-Object { $_.Split(' ')[0] }
'toolchain.tar.xz: {0} paquetes, {1} archivos, {2:N1} MB' -f $pkgs.Count, $toolEntries, ((Get-Item $toolXz).Length / 1MB)

# 4. the wizard
Payload $gameEntries $toolEntries $pkgs
$defs = if ($Release) { @('/define:SOURCE_FROM_DISC') } else { @() }
Compile $out (@("/resource:$gameXz,game.tar.xz", "/resource:$toolXz,toolchain.tar.xz") + $defs)
'Instalador: {0} ({1:N1} MB)' -f $out, ((Get-Item $out).Length / 1MB)

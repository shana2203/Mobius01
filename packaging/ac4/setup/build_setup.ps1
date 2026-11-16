# Builds Desktop\Mobius01-Setup-<date>.exe, the installation wizard:
#   game.tar.xz       the game as make_package.ps1 stages it, plus
#                     Desinstalar.exe, minus ps2_image.bin (rebuilt from the
#                     player's ISO at install time)
#   toolchain.tar.xz  only the UCRT64 packages the per-PC compile needs and
#                     their dependencies (toolchain_files.py), with pacman's
#                     records of them; no bash, pacman, Python or docs
# Both use xz -9e (LZMA2) and are unpacked by Windows' own tar.exe.
# Needs MSYS2 in C:\msys64 (tar, xz) and Python.   .\build_setup.ps1 [-RebuildToolchain]
param([switch]$RebuildToolchain)
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
        "/win32icon:$config\play.ico" "/win32manifest:$here\setup.manifest" `
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
Remove-Item (Join-Path $stage 'VulkanRuntime\runtime\ps2_image.bin') -ErrorAction SilentlyContinue
# game data never ships: the source's copy of the image too (the installer
# makes gamedata\ps2_image.bin from the player's ISO)
Remove-Item (Join-Path $stage 'VulkanRuntime\source\generated\ps2_image.bin') -ErrorAction SilentlyContinue
if (Get-ChildItem $stage -Recurse -Filter 'ps2_image.bin') { throw 'ps2_image.bin sigue en el paquete del instalador' }
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
Compile $out @("/resource:$gameXz,game.tar.xz", "/resource:$toolXz,toolchain.tar.xz")
'Instalador: {0} ({1:N1} MB)' -f $out, ((Get-Item $out).Length / 1MB)

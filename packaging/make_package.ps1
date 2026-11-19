# Builds the lightweight AC4Recomp download: the prebuilt generic ac4.exe and
# what it needs to run, without the source, the compiler, or this PC's logs,
# saves, settings and caches.  Output: Desktop\Mobius01-<date>.zip
# Called by CREAR_PAQUETE.bat.  The ISO is never included.
# -StageOnly: leave the staged AC4Recomp folder and print its path (used by
# setup\build_setup.ps1); no zip is written.
param([switch]$StageOnly)
$ErrorActionPreference = 'Stop'
# the game folder sits next to the development folder (AC4Recomp-desarrollo)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\AC4Recomp')).Path
$game = Join-Path $root 'VulkanRuntime'
$rt   = Join-Path $game 'runtime'
$date = Get-Date -Format 'yyyy.MM.dd'
$desk = [Environment]::GetFolderPath('Desktop')
$stage = Join-Path $env:TEMP "AC4Recomp-package-$date"
$out  = Join-Path $stage 'AC4Recomp'
$zip  = Join-Path $desk "Mobius01-$date.zip"

if (-not (Test-Path (Join-Path $rt 'ac4.exe'))) { throw "No existe $rt\ac4.exe" }
$stamp = Join-Path $rt 'ac4_build.stamp'
if ((Test-Path $stamp) -and (Select-String -Path $stamp -Pattern '^recipe=1$' -Quiet)) {
    Write-Warning 'ac4.exe parece compilado para esta CPU (-march=native); podria no arrancar en otras PCs.'
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($f in 'Jugar.exe', 'Configurar AC4.exe', 'LEEME.md', 'CREDITOS.md', 'CREDITS.md', 'VULKAN.md', 'RECOGER_LOGS.bat') {
    $p = Join-Path $root $f
    if (Test-Path $p) { Copy-Item $p $out }
}

$g = Join-Path $out 'VulkanRuntime'
New-Item -ItemType Directory -Force $g, (Join-Path $g 'saves'), (Join-Path $g 'logs') | Out-Null
foreach ($f in 'cheats.pnach', 'DIAGNOSTICS.bat', 'recoger_logs.ps1') {
    $p = Join-Path $game $f
    if (Test-Path $p) { Copy-Item $p $g }
}
foreach ($d in 'icons', 'licenses', 'mods') {
    $p = Join-Path $game $d
    if (Test-Path $p) { Copy-Item $p $g -Recurse }
}

# Every mod comes with the port switched off: each player turns on what they
# want in Configurar AC4 (Mods, "Municion y dinero", Idioma).  This PC's
# credit cheat is not shipped.
foreach ($m in 'municion', 'guiado_misiles') {
    if (-not (Test-Path (Join-Path $g "mods\$m\mod.toml"))) { throw "Falta el mod $m en $game\mods" }
}
foreach ($p in Get-ChildItem (Join-Path $g 'mods') -Recurse -Filter mod.toml | ForEach-Object FullName) {
    $t = @(Get-Content $p) -replace '^\s*enabled\s*=.*$', 'enabled = false' -replace '^\s*credits\s*=.*$', 'credits = 0'
    if (-not ($t -match '^\s*enabled\s*=')) { $t = @('enabled = false') + $t }   # a missing line means "on"
    [IO.File]::WriteAllLines($p, [string[]]$t, (New-Object Text.UTF8Encoding $false))
}

# F6, the bug capturer, is on in this full download: ac4.exe looks for this
# marker (setup\build_setup.ps1 -Release removes it from the public one)
Set-Content -Path (Join-Path $g 'bugcapture.on') -Value 'F6 = captura de bugs (borrar este archivo para desactivarla)' -Encoding ASCII

$r = Join-Path $g 'runtime'
New-Item -ItemType Directory -Force $r | Out-Null
# no ps2_image.bin: game data never ships; ac4.exe builds it from the
# player's ISO when it starts (or the installer's gamedata copy is used)
Copy-Item (Join-Path $rt 'ac4.exe'), (Join-Path $rt 'ac4_pipelines.seed.keys') $r
Get-ChildItem $rt -Filter *.dll | Where-Object Name -ne 'vulkan-1.dll' | Copy-Item -Destination $r
Copy-Item (Join-Path $rt 'shaders') $r -Recurse
if (Test-Path (Join-Path $rt 'textures')) { Copy-Item (Join-Path $rt 'textures') $r -Recurse }
# the pipeline cache, settings and build stamp are made on each PC

# The source, so that Jugar.exe compiles the best version for each PC on its
# first run (the generic ac4.exe above is the fallback while it cannot).
$srcDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dst = Join-Path $g 'source'
New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($f in 'CMakeLists.txt', 'LICENSE', 'README.md') {
    if (Test-Path (Join-Path $srcDir $f)) { Copy-Item (Join-Path $srcDir $f) $dst }
}
foreach ($d in 'config', 'generated', 'runtime', 'third_party', 'pgo-profile', 'packaging', 'tools') {
    $p = Join-Path $srcDir $d
    if (Test-Path $p) {
        # (the compile does not need generated\ps2_image.bin: game data stays out)
        robocopy $p (Join-Path $dst $d) /E /XD __pycache__ /XF *.pyc ps2_image.bin /NFL /NDL /NJH /NJS /NP | Out-Null
    }
}

if ($StageOnly) { return $out }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
$files = Get-ChildItem $out -Recurse -File
'{0} archivos, {1:N1} MB sin comprimir, {2:N1} MB comprimido' -f $files.Count,
    (($files | Measure-Object Length -Sum).Sum / 1MB), ((Get-Item $zip).Length / 1MB)
"Paquete: $zip"
Remove-Item $stage -Recurse -Force

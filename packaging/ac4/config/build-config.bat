@echo off
rem Builds "Configurar AC4.exe" (graphics, controls and cheats launcher) and
rem Jugar.exe (play launcher with the per-PC compiler), using the C# compiler
rem built into Windows (.NET Framework 4.x).
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "OUT=%~dp0..\..\..\..\..\AC4Recomp\Configurar AC4.exe"
set "REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll"
set "RES=/resource:build-bg.jpg,AC4.Background.jpg"
set "ICON="
if exist "icon.ico" set "ICON=/win32icon:icon.ico"
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 %ICON% %REFS% %RES% /out:"%OUT%" Config.cs Builder.cs Theme.cs
if errorlevel 1 (
  echo [ERROR] No se pudo compilar el launcher de configuracion.
  pause
  exit /b 1
)
echo Listo: %OUT%
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /define:PLAY_LAUNCHER /win32icon:play.ico %REFS% %RES% /out:"%~dp0..\..\..\..\..\AC4Recomp\Jugar.exe" Config.cs Builder.cs Theme.cs
if errorlevel 1 exit /b 1
echo Listo: Jugar.exe

# Change history

## Unreleased

## Mobius01 1.2

- Stonehenge missions (10 and 12): the sun halo no longer turns into a hard-edged bright
  square at low altitude. The halo palette the game draws into a small target inside the
  bloom buffer is now read from that target (ties between render targets written in the
  same epoch go to the one drawn last, then to the nearer base).
- Megalith (mission 18): the lasers are lines with antialiasing (PRIM AA1). The GS blends
  such lines even with ABE off, with the pixel coverage as alpha; they were drawn opaque and
  looked like dark threads in the fog.
- Ending staff roll: the movie pictures follow the movie's own sound (AutoDMA stream) and no
  longer drift ahead of the song (up to 0.7 s over 4 minutes). `PS2_IPU_AUDIO_SYNC=0` turns
  it off, `PS2_IPU_SYNC_LOG=1` reports the offset.
- Windows 11 efficiency mode is turned off for the game process (it ran at about half speed
  when hidden or in the background).
- Bug capture (F6) shows an on-screen notice and writes one file per capture
  (`capture-YYYYMMDD-HHMMSS.gscap`); it is only enabled in builds that ship `bugcapture.on`.
- The gun aim-assist mod (`punteria_canon`) is no longer part of the public release.
- Launcher: Jugar shows a splash picture (Su-37) centred on screen, with rounded corners and
  a fade, from the moment it starts until the game's window appears (at least 3 s).
- New artwork for the installer, Configurar AC4 and the compile window (F-22), and a new ISAF
  icon for Jugar, Configurar AC4 and the game window in the taskbar.
- Package names use dotted dates (`Mobius01-Setup-2026.11.19.exe`).
- Test tools: `PS2_FIELD_WAIT=1` (scripted runs never drop fields), `PS2_SHOT_NAME=vblank`,
  and traces `PS2_TRACE_CLUT_RT`, `PS2_TRACE_TEX0_CBP`, `PS2_TRACE_RT_FBW`.

## 2.2.3 Beta

[Release and installer](https://github.com/PresidentRaptor/Ace-Combat-04-PC-Port/releases/tag/v2.2.3-beta)

- The renderer checks every GPU (Vulkan 1.3, dynamic rendering, synchronization2, dual-source
  blending, swapchain, a queue that can draw to the window), prefers a usable discrete one and
  falls back to any usable one; each GPU is logged. `PS2_GPU=<n>` forces one (#6).
- A failed graphics start shows an error and exits instead of running headless (#6).

## 2.2.2 Beta

[Release and installer](https://github.com/PresidentRaptor/Ace-Combat-04-PC-Port/releases/tag/v2.2.2-beta)

- Anisotropic filtering no longer corrupts textures on some GPUs (AMD): texture gradients
  are taken before any pixel is discarded, its reach stays inside clamped texture regions,
  and the final picture and render targets are never anisotropically filtered (#4).
- The swapchain is rebuilt only when the window size really changes; drivers that report
  it as suboptimal on every frame no longer force a GPU wait per frame, a likely cause of
  picture and audio stutter on AMD (#5). Rebuilds are now logged.

## 2.2.1 Beta

[Release and installer](https://github.com/PresidentRaptor/Ace-Combat-04-PC-Port/releases/tag/v2.2.1-beta)

- Updated the Windows installer from current `main`; player packages use the EXE launcher.
- Added visual regression checks, frame-timing analysis and opt-in runtime profiling tools.
- Retains the 2.2.0 rendering/audio fixes, ISO import, clean settings and license notices.
- Full campaign validation remains incomplete; this is a public beta.


- Organized developer documentation under `docs/` and removed retired experiments.
- Added contribution, conduct, security and support guidance, issue/PR templates,
  a compatibility checklist and game-data-free source checks for CI.

## 2.2.0 Beta

[Release and installer](https://github.com/PresidentRaptor/Ace-Combat-04-PC-Port/releases/tag/v2.2.0-beta)
— tagged commit `acb3843`.

- Windows folder-based installer verifies the USA executable inside the user's
  ISO, imports the disc, generates its memory image locally and offers a desktop shortcut.
- Included runtime DLLs/shaders, clean High/3x defaults, widescreen, FXAA and UI smoothing.
- Includes target-diamond, hangar-guide, objectives-scan, downward-view and startup-audio fixes.
- Uses AC4 mod API names with legacy compatibility aliases.
- Installer and SHA-256 checksums published without ISO or extracted game data.
- Mission 01 completed in live play; full campaign coverage remains incomplete.

The earlier AC5 version tags belong to the upstream project's history, not AC4
releases. Detailed implementation evidence is retained in the [development notes](docs/README.md).

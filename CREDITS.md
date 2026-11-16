# Credits

## Projects this port builds on

- **sal063**, *Ace Combat 5 Static Recompilation*: the static recompiler,
  runtime, renderer, settings menu and tools this port shares.
  https://github.com/sal063/Ace-Combat-5-Static-recompilation
- **PresidentRaptor and contributors**, *Ace Combat 04 PC Port*: the
  adaptation of that work to Ace Combat 04 (game configuration, sound
  services, renderer fixes, widescreen and packaging).
  https://github.com/PresidentRaptor/Ace-Combat-04-PC-Port
- **Mobius01**: this version of the port: launcher and per-PC build, Spanish
  translation, button symbols, `.ps2` saves, mods and this setup.
- **PCSX2**: its game database was the reference for the GS behaviours AC04
  relies on, and its `.ps2` memory card format is the one saves use.

## Included libraries

- **SDL3**: window, input and audio (zlib license).
- **Dear ImGui**: F4 menu (MIT license).
- **Lua**: mods (MIT license).
- **Vulkan Loader and Vulkan Headers** (Khronos, Apache 2.0).
- **MinGW-w64** (winpthreads, CRT) and **libiconv** (LGPL).

## Tools that build the game on each PC

GCC, binutils, CMake, Ninja, pkgconf, shaderc, glslang and SPIRV-Tools, as
the unmodified UCRT64 packages of **MSYS2** (https://www.msys2.org). Each
package carries its license in `msys64\ucrt64\share\licenses`; their source
code is at https://github.com/msys2/MINGW-packages.

## Licenses

The port's code is released under the Apache 2.0 license, like the project it
builds on. The libraries' licenses are in `VulkanRuntime\licenses`.

## Legal notice

Ace Combat is a trademark of Bandai Namco Entertainment. This project is not
affiliated with or endorsed by Bandai Namco. It includes no game files: you
need your own copy, the ISO of the USA version (SLUS-20152).

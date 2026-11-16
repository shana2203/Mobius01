# Créditos

## Proyectos en los que se basa este port

- **sal063**, *Ace Combat 5 Static Recompilation*: el recompilador estático,
  el runtime, el renderizador, el menú de ajustes y las herramientas que este
  port comparte. https://github.com/sal063/Ace-Combat-5-Static-recompilation
- **PresidentRaptor y colaboradores**, *Ace Combat 04 PC Port*: la adaptación
  de ese trabajo a Ace Combat 04 (configuración del juego, servicios de
  sonido, correcciones del renderizador, pantalla ancha y empaquetado).
  https://github.com/PresidentRaptor/Ace-Combat-04-PC-Port
- **Mobius01**: esta versión del port: launcher y compilación para cada PC,
  traducción al español, símbolos de botones, partidas `.ps2`, mods y este
  instalador.
- **PCSX2**: su base de datos de juegos fue la referencia de los
  comportamientos del GS que usa AC04, y su formato de tarjeta de memoria
  `.ps2` es el que usan las partidas.

## Bibliotecas incluidas

- **SDL3**: ventana, entrada y audio (licencia zlib).
- **Dear ImGui**: menú F4 (licencia MIT).
- **Lua**: mods (licencia MIT).
- **Vulkan Loader y Vulkan Headers** (Khronos, Apache 2.0).
- **MinGW-w64** (winpthreads, CRT) y **libiconv** (LGPL).

## Herramientas para compilar el juego en cada PC

GCC, binutils, CMake, Ninja, pkgconf, shaderc, glslang y SPIRV-Tools, en los
paquetes UCRT64 de **MSYS2** (https://www.msys2.org), sin modificar. Cada
paquete trae su licencia en `msys64\ucrt64\share\licenses`; su código fuente
está en https://github.com/msys2/MINGW-packages.

## Licencias

El código del port se publica bajo la licencia Apache 2.0, igual que el
proyecto en el que se basa. Las licencias de las bibliotecas están en
`VulkanRuntime\licenses`.

## Aviso legal

Ace Combat es una marca de Bandai Namco Entertainment. Este proyecto no está
afiliado ni respaldado por Bandai Namco. No incluye archivos del juego:
necesitas tu propia copia, la ISO de la versión de EE. UU. (SLUS-20152).

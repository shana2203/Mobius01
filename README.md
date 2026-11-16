# Mobius01

**Ace Combat 04: Shattered Skies para PC** — un port nativo para Windows (Vulkan)
de la versión de PlayStation 2 (EE. UU., SLUS-20152), hecho por recompilación
estática.

*English summary below.*

Mobius01 parte del
[Ace Combat 04 PC Port](https://github.com/PresidentRaptor/Ace-Combat-04-PC-Port)
de PresidentRaptor y colaboradores, que a su vez se basa en la
[recompilación estática de Ace Combat 5](https://github.com/sal063/Ace-Combat-5-Static-recompilation)
de sal063. El recompilador y la base del motor son trabajo de ellos, bajo la
licencia Apache 2.0, que se conserva junto con sus créditos (ver `NOTICE`,
`CREDITOS.md` y `docs/PORT-ORIGINAL.md`).

## Qué agrega Mobius01

- **Instalador** (`packaging/ac4/setup`): asistente en español e inglés. Toma
  tu ISO o una carpeta con los archivos del disco y deja listo el juego. Si
  faltan, instala las herramientas para compilarlo para tu PC, sin internet.
- **Lanzadores** `Jugar.exe` y `Configurar AC4.exe` (`packaging/ac4/config`):
  - compilación optimizada para cada PC, que se repite al cambiar de hardware
    o de driver;
  - gráficos, pantalla (resolución nativa en 16:9 por defecto), controles con
    selector de mando, audio, mods y cheats.
- **Arreglos del motor**:
  - suelo negro en el terreno, por los cuadros de sombra de nubes que el
    juego arma en la GPU;
  - capturas gráficas que ya no se corrompen con los gráficos en un hilo
    aparte;
  - el juego arranca desde la ISO, desde una carpeta o desde la imagen
    comprimida;
  - controles que nunca quedan sin asignar;
  - el menú F4 también se abre con LS + RS;
  - el mando funciona aunque la ventana pierda el foco.
- **Traducción propia al español neutro** (`localization/es-419-propio`) y sus
  herramientas (`tools/ac4_tr.py` y compañía), hecha solo a partir del disco
  de EE. UU.
- **Mods** (`mods/`, todos desactivados por defecto):

  | Mod | Qué hace |
  |---|---|
  | `punteria_canon` | Ayuda de puntería para el cañón |
  | `guiado_misiles` | Guiado de misiles ajustable |
  | `municion` | Munición extra |
  | `maniobrabilidad` | Ajustes de maniobrabilidad |
  | `giros_alto_g` | Giros de alto G |
  | `mision_libre` | Misión libre |
  | `tienda_completa` | Tienda completa |
  | `cheats` | Cheats |

## Descargar y jugar

En [Releases](https://github.com/shana2203/Mobius01/releases) está el
instalador `Mobius01-Setup-v<versión>.exe`. Necesitas tu propia ISO (o carpeta
del disco) de **Ace Combat 04 de EE. UU. (SLUS-20152)**.

1. El instalador copia el port, los mods (solo sus scripts) y las
   herramientas de compilación (sin internet), y toma de tu disco el
   ejecutable del juego.
2. La primera vez que abres **Jugar** (unos minutos, solo una vez), todo lo
   que sale del juego se genera en tu PC desde tu propia ISO:
   - el código del juego, desde tu `SLUS_201.52` (`tools/ps2recomp`, y
     `tools/ac4_vu1.py` + `tools/vurecomp` para los microprogramas VU1), que
     luego se compila para tu procesador;
   - el mod de español: nuestra traducción aplicada a los textos de tu disco,
     el HUD y las tarjetas redibujados con sus propias letras
     (`tools/ac4_mods_from_disc.py`);
   - el parche del menú de `mision_libre`;
   - las texturas HD de los menús: el juego arranca oculto hasta el título,
     guarda las texturas que dibuja y nuestro filtro las amplía x4
     (`tools/ac4_hd_menus.py`).

La descarga no trae código, textos, imágenes ni ningún otro dato del juego, ni
un ejecutable del juego ya compilado.

## Qué NO incluye (y por qué)

Este repositorio no contiene nada del juego. Ace Combat 04 es de Bandai Namco,
y cada persona necesita **su propia copia legal** del juego. No se incluyen:

- el código recompilado del juego (`generated/`) ni su imagen de memoria
  (`ps2_image.bin`): se generan en tu PC a partir de tu `SLUS_201.52`;
- textos, imágenes ni datos del disco: los textos originales en inglés que la
  traducción necesita se extraen de tu ISO con `tools/ac4_tr.py export`.

Tampoco se usa material de la versión europea (PAL).

## Compilar

Los pasos completos están en [`docs/PORT-ORIGINAL.md`](docs/PORT-ORIGINAL.md)
("Build and run"). En resumen, con MSYS2 UCRT64 (gcc, cmake, ninja, SDL3,
shaderc, Vulkan):

```
bash tools/build_ac4.sh ruta/a/SLUS_201.52
```

El instalador y el paquete se arman con
`packaging/ac4/setup/build_setup.ps1` y `packaging/make_package.ps1`.

La guía para jugadores (controles, idioma, mods, grabar video...) está en
[`docs/GUIA-DE-USO.md`](docs/GUIA-DE-USO.md).

---

## English summary

Mobius01 is a fork-style continuation of
[PresidentRaptor's Ace Combat 04 PC Port](https://github.com/PresidentRaptor/Ace-Combat-04-PC-Port),
itself built on [sal063's Ace Combat 5 static recompilation](https://github.com/sal063/Ace-Combat-5-Static-recompilation)
(Apache 2.0; their notices and credits are kept).

Download the installer from Releases. It ships the port's source, the build
tools and the mods' scripts, and no game code or data: on the first start the
game's code, the Spanish mod, the free-mission menu patch and the HD menu
textures (scaled 4x by our own filter) are all generated on your PC from your
own ISO, and the game is compiled there.

It adds:
- a bilingual installer;
- Windows launchers (per-PC optimized builds, settings, controller picker);
- engine fixes: terrain shadow tiles built on the GPU, thread-safe graphics
  captures, compressed or disc-built memory image, never-unbound controls;
- an original neutral-Spanish translation made from the US disc only;
- a set of Lua mods (gun aim assist, missile guidance, ammo, handling and
  more), all off by default.

No game code or data is included: you need your own copy of the game (USA,
SLUS-20152). `generated/` and `ps2_image.bin` are produced locally from your
`SLUS_201.52`. See [`docs/PORT-ORIGINAL.md`](docs/PORT-ORIGINAL.md) to build.

Ace Combat is a trademark of Bandai Namco Entertainment. This project is not
affiliated with or endorsed by Bandai Namco.

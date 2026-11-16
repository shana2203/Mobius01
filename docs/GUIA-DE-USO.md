# Mobius01: Ace Combat 04: Shattered Skies para PC (Vulkan)

Port nativo para Windows de 64 bits. No hace falta instalar nada: el juego ya
viene compilado y funciona en cualquier PC x86-64 con Windows 10 u 11 y una
tarjeta gráfica con Vulkan 1.3 (por ejemplo NVIDIA GTX 900 o posterior, AMD
RX 400 o posterior, Intel UHD 600 o posterior, con el driver al día).

## Instalación

`Mobius01-Setup-<fecha>.exe` es un asistente (en español o inglés) que pide tu ISO (versión de
EE. UU., SLUS-20152) y una carpeta. Instala el juego, y si faltan, las
herramientas para compilarlo para tu PC, sin internet. Crea en el escritorio el
acceso "Ace Combat 04 Shattered Skies", que abre `Jugar.exe` como
administrador. El idioma que elijas en el asistente queda como idioma del
juego (se cambia en `Configurar AC4.exe` > Juego). Todos los mods vienen apagados. El juego se juega desde
tu ISO o desde una carpeta con los archivos del disco (SYSTEM.CNF,
SLUS_201.52, DATA.CDP...); el instalador no los copia ni los modifica. Solo
guarda el programa del juego, sacado de ahí y comprimido (menos de 1 MB), en
`gamedata\ps2_image.bin`; si falta, el juego lo saca del disco al arrancar. La
primera vez la pantalla queda en pantalla completa sin bordes, a la
resolución de tu monitor, en 16:9 y con los FPS a la vista. Los nombres de
las misiones quedan en inglés, como en el original. Si eliges la carpeta de una
instalación anterior, la actualiza y conserva tus partidas, ajustes y mods.
Para desinstalar, usa "Aplicaciones instaladas" de Windows o `Desinstalar.exe`.

## Cómo jugar

1. Pon la ISO de Ace Combat 04 (USA, SLUS-20152) en la carpeta que contiene a
   esta, o elígela en `Configurar AC4.exe`, en la página Juego. Ahí también
   puedes elegir una carpeta con los archivos del disco en vez de la ISO.
2. Abre `Jugar.exe`.

La primera vez que juegas en una PC, `Jugar.exe` compila el juego para ese
procesador, tarjeta gráfica y sistema (unos minutos; instala lo necesario si
falta y pide permisos de administrador). Lo repite solo si cambias de
hardware, actualizas el driver de video o el port cambia. Mientras tanto, o
si la compilación falla, se puede jugar con la versión genérica incluida.
`Configurar AC4.exe` > Juego > "Compilar para esta PC" lo fuerza a mano.

## Idioma

`Configurar AC4.exe` > Juego > Idioma del juego: English (original) o
Español neutro. El español traduce los subtítulos de la radio y de las
cinemáticas, los briefings, las ayudas de los menús, las descripciones de
armas y aviones, los textos de los menús y de la misión, las etiquetas del
HUD, las de quién habla por radio (ALIADO, ENEMIGO, FLOTA) y el
"PRESIONA START" de la pantalla de título, la tarjeta de título de cada
misión y el texto del final de la introducción. Los textos grabados dentro de
los videos de briefing (por ejemplo "NOW LOADING" y la fecha sobre el mapa)
siguen en inglés. Las letras de bloque de los menús no
tienen acentos, así que esos textos se escriben sin tilde.

## Símbolos de botones

Los menús y avisos del juego muestran los botones del control que estás
usando: los símbolos de PlayStation, las letras A/B/X/Y de un control de Xbox
u otro, o la tecla asignada si juegas con teclado (X, S, Z, A por defecto).
En modo automático cambian solos según el último dispositivo que tocaste. Para
fijar uno: `Configurar AC4.exe` > Controles > "Simbolos de botones", o el
menú F4 > Controls > "Button symbols".

## Munición y guiado de misiles

Vienen incluidos con el port y apagados: para usarlos, marca "Activar las
funciones de esta pagina" arriba de `Configurar AC4.exe` > "Municion y
dinero". Ese interruptor enciende o apaga juntos la munición extra, los
créditos y el guiado; los valores se conservan aunque lo apagues. La página
aumenta el cañón y los misiles
hasta +100% y el arma especial (bombas, QAAM, XLAA...) hasta +250%. En la
misma página, la barra "Guiado de misiles" ajusta cuánto corrigen su rumbo
todos los misiles guiados: +0% es el juego original y +100% el doble. Si falta
el mod `guiado_misiles` en `VulkanRuntime\mods`, la barra queda desactivada.

## Puntería del cañón

El mod "Punteria del canon" (`Configurar AC4.exe` > Mods) ayuda a acertar con
la ametralladora: si al disparar hay un enemigo cerca de la línea de tiro
(dentro de 5 grados y a menos de 4500 unidades, lo que alcanza una bala),
las balas salen hacia el punto donde estará el enemigo cuando lleguen, así que
no hace falta calcular la anticipación. Fuera de ese cono el cañón dispara
como siempre. En `VulkanRuntime\mods\punteria_canon\mod.toml` se ajustan el
cono (`cono_grados`, hasta 30), el alcance y la fuerza de la ayuda.

El mismo mod dibuja una **mira de anticipación**: un círculo sobre la pantalla
que marca adónde apuntar la nariz del avión para que las balas alcancen al
enemigo más cercano a tu línea de tiro. Es ámbar mientras falta apuntar y se
pone verde cuando el enemigo ya está dentro del cono de la ayuda. Se apaga con
`mira = false`; `mira_cono_grados` y `mira_tamano` cambian hasta dónde busca y
el tamaño del círculo. Sale también en las grabaciones F7.

## Reflejo de los aviones

`Configurar AC4.exe` > Graficos > "Reflejo de los aviones" (o F4 > "Aircraft
reflection") ajusta el brillo metálico de los aviones. Viene al 50 %. Al 100 %
es como en PS2, donde en aviones plateados como el F-15C parece una segunda
imagen del avión.

## Grabar video

Pulsa **F7** durante el juego para empezar a grabar y **F7** otra vez para
terminar. Arriba a la derecha aparece un punto rojo con el tiempo grabado. El
video se guarda en `VulkanRuntime\videos` como MP4 (H.264 con sonido AAC, hasta
1080p a 60 cuadros por segundo), listo para subir o compartir. Graba la imagen
del juego y su sonido, sin el menú F4. Si cierras el juego mientras grabas, el
video se guarda igual. Usa el codificador de video de Windows (por hardware si
la tarjeta gráfica lo tiene); en Windows "N" hace falta el Media Feature Pack.
**F12** guarda una captura de pantalla en `VulkanRuntime\screenshots`.

## Archivos

- `Jugar.exe`: inicia el juego. `Jugar.exe diagnostico` guarda además los
  tiempos de cada cuadro, útil para informar de un problema de rendimiento.
- `Configurar AC4.exe`: gráficos (incluido AMD FSR 1.0), pantalla, controles
  de teclado y mando, audio, munición, cheats y mods. "Guardar y jugar" inicia
  el juego.
- `VulkanRuntime\runtime`: el juego (`ac4.exe`), sus DLL, los shaders y el
  pack de texturas HD.
- `VulkanRuntime\saves`: tarjetas de memoria de 8 MB en formato `.ps2`.
  Si ya existe `card0.ps2mc`, el primer inicio crea `card0.ps2` con sus partidas
  y conserva el archivo anterior. Para usar tu partida en PCSX2, cierra el juego
  y lleva una copia de `card0.ps2` al emulador. Para guardar en otra carpeta
  (por ejemplo una de OneDrive, para tener respaldo): `Configurar AC4.exe` > Juego >
  "Carpeta de partidas" > "Elegir carpeta...". Si la carpeta nueva está vacía,
  ofrece copiar ahí tus partidas; la carpeta anterior no se toca.
- `VulkanRuntime\mods`: los mods; se activan desde `Configurar AC4.exe`.
- `VulkanRuntime\logs`: se crea al jugar; registro de las últimas partidas.

Si el juego va lento en una PC con gráfica integrada y tarjeta gráfica,
conecta el monitor a la tarjeta gráfica: `Jugar.exe` avisa si el juego se
ejecutó en la integrada.

# Traducción al español neutro

Estado: investigación terminada y catálogo extraído. Falta traducir, inyectar y
preparar la fuente.

## Dónde están los textos de AC4

| Tipo | Dónde | Forma |
|---|---|---|
| Subtítulos de radio (1.952 líneas únicas) | Cada paquete de misión de `BIN/DATA.CDP` (entradas 1–100) | Tabla de códigos de 16 bits, formato "radio" |
| Briefings, ayudas de menú, resultados, descripciones de armas y aviones (686 únicas) | Entradas 176–354 de `DATA.CDP` | Tabla de códigos de 16 bits, formato "texto" |
| Subtítulos de la historia (cinemáticas) | Entradas 259–272 | Igual |
| HUD, menús en mayúsculas de bloque, nombres de misión | El ejecutable `SLUS_201.52` | ASCII; ver `menus-objectives.json` |
| Etiquetas de quién habla por radio ("AWACS", "BOMBER") | Texturas | Imagen; se traduce con el pack de texturas |

`tools/ac4_text.py scan <iso> <json>` extrae todo: 164 tablas, 2.638 textos únicos
y 115.779 caracteres. El resultado es `catalogo_en.json`, con dónde aparece cada
texto (entrada, ruta dentro del paquete e índice).

## Codificación (tablas de `DATA.CDP`)

- `u32` cabecera y luego códigos de 16 bits.
- Formato texto: `0xFFFE` cierra cada texto, `0xFFFF` es salto de línea; la
  cabecera no es el número de textos.
- Formato radio: `0xFFFF` cierra cada línea, `0xFFFD` es salto de línea y la
  cabecera es el número de líneas.
- `0x000D` es el espacio.
- Letras: `A–Z` = ASCII − 0x1A, `a–z` = ASCII − 0x20 (por eso no aparecen como
  texto plano), `0–9` = ASCII − 0x13.
- Puntuación: `, . : — ? ! / ' " ( ) - % #` = A4 A5 A6 A7 A8 A9 AC AF B0 B1 0E 12 18 AB.
  `<` `>` de la radio = 0000 0001.
- Botones del mando: 08 □, 09 ✕, 0A △, 0B ○ (en el catálogo `{SQUARE}`,
  `{CROSS}`, `{TRIANGLE}`, `{CIRCLE}`).
- Las letras con tilde no existen en la fuente US. En la versión PAL
  (`ISO Europea`) la fuente española usa Latin-1 − 0x64: á 7D, é 85, í 89, ó 8F,
  ú 96, ñ 8D, ü 98, Á 5D, É 65, Í 69, Ó 6F, Ú 76, Ñ 6D, ¿ 5B. Además ¡ es A3 y
  º es B4.

Cada paquete de texto lleva su propia fuente: páginas GIM y una tabla de glifos.
El disco PAL tiene una copia de cada paquete por idioma, en grupos de 5
entradas en el orden inglés, francés, alemán, italiano y español.

## Fuente de los subtítulos (resuelto)

La fuente de los subtítulos (radio, briefings, historia) está en el ejecutable US,
en el offset 0x2B1C70 de `SLUS_201.52`: 180 glifos de 32×32 a 4 bits, cada uno
con 16 bytes de cabecera, separados 0x210 bytes. Al arrancar se copia a la RAM
(0x3C2FF0) y cada línea se compone subiendo un glifo por celda a una textura
PSMT4 (TBP 0x3A80 o 0x3CC0).

**La fuente US ya tiene todas las letras de Latin-1**, con los mismos códigos
que el texto español PAL: código = Latin-1 − 0x64 entre U+00BF y U+00FF (¿ 5B,
Á 5D, É 65, Í 69, Ñ 6D, Ó 6F, Ú 76, á 7D, é 85, í 89, ñ 8D, ó 8F, ú 96, ü 98).
Además, ¡ es A3. No hace falta dibujar glifos ni tomar la fuente del disco PAL.
`tools/ac4_text.py` ya codifica esos caracteres.

Las fuentes de bloque del HUD y de los menús (también en el ejecutable, 256×80,
256×44 y 256×36) solo tienen ASCII.
## Plan

1. **Inyección:** que el sistema de archivos virtual permita a un mod
   reemplazar entradas sueltas de `DATA.CDP` (con `DATA.TBL` nuevo y los datos
   añadidos al final), sin tocar los 721 MB.
2. **Fuente con tildes:** añadir los glifos á é í ó ú ñ ü ¿ ¡ (y sus mayúsculas)
   a la fuente de cada paquete reescrito, usando los códigos PAL.
3. **Traducción:** `catalogo_es.json` en paralelo a `catalogo_en.json`, por
   bloques: menús y sistema, briefings, historia y radio, misión por misión.
4. **Reconstrucción:** una herramienta que recodifica, reempaqueta y genera el
   mod `es419` (radio, briefings, historia, menús, cadenas del ejecutable y
   etiquetas en textura).
5. **Validación** en pantalla, misión por misión, con el arnés de capturas.

# Traducción propia al español neutro

Traducción hecha desde el texto del disco **US** (SLUS-20152). No usa ningún
texto de otras versiones del juego: ni de la versión europea ni de ninguna
otra.

| Archivo | Contenido |
|---|---|
| `fuente_en.json` | Los 2.638 textos únicos del disco US, cada uno con un número fijo (`id`), su tipo y dónde aparece. Lo genera `tools/ac4_tr.py export`. |
| `es/01_menus.json` | Menús, tarjeta de memoria, opciones, hangar, resultados y VS |
| `es/02_briefings.json` | Briefings y nombres de misión |
| `es/03_historia.json` | Narración de las cinemáticas |
| `es/04…08_radio_*.json` | Radio de las misiones y tutoriales |
| `es/09_ajustes.json` | Correcciones de largo de línea; se carga al final y tiene prioridad |

Cada archivo `es/*.json` es `{"<id>": "<español>"}`. Seis textos quedan sin
traducir a propósito: el texto vacío (578), la etiqueta de depuración
"Briefing#15" (1696) y cuatro tablas de datos que no son texto (2606–2609).

## Reglas

- **Mismo número de líneas que el inglés.** El juego busca cada texto por
  número de *línea*. Si una frase tiene un salto de línea de más, todas las
  siguientes se corren: ayudas de menú desfasadas y subtítulos fuera de tiempo.
  `ac4_tr.py build` lo hace cumplir.
- La fuente tiene las letras de Latin-1 (á é í ó ú ñ ü ¿ ¡). **No** tiene `;`
  ni `º`.
- Las líneas no deben superar la línea más larga de su tipo (en la radio, 44
  caracteres). `{SQUARE}`, `{CROSS}` y similares cuentan como un carácter.

## Comandos

```
python -I tools\ac4_tr.py export "<ISO US>" localization\es-419-propio
python -I tools\ac4_tr.py check localization\es-419-propio
python -I tools\ac4_tr.py build "<ISO US>" localization\es-419-propio <carpeta del mod> <hud_part0.bin>
```

Los textos del ejecutable (fuente de bloque, solo ASCII) están en
`localization/es-419/ejecutable.json`, también es traducción propia. Se
convierten en `script.lua` con `tools/ac4_es.py elf`. Las etiquetas del HUD
(`hud_part0.bin`) se dibujan con la propia fuente del atlas US
(`tools/ac4_hud.py`).

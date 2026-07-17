# Recolector — Ranked ARAM Caos

Recolector de partidas para el ranked privado del grupo.

**Descarga**: [Releases](../../releases/latest) → `Instalar-Recolector-ARAM-Caos.exe`

## ¿Qué hace?

Riot no publica las partidas de ARAM: Caos en su API pública, así que este
programa las lee directamente del cliente de LoL de tu PC (la misma técnica
que usan Blitz o Porofessor: **solo lectura** vía la API local del cliente,
sin tocar el juego ni su memoria) y las sube al ranked del grupo.

- Vive como icono en la bandeja del sistema, junto al reloj
- Durante la partida no hace nada (cero lag); al terminar sube unos KB
- Necesitas una cuenta en el ranked y tu código de vinculación personal —
  sin eso, el programa no hace nada

## ¿Por qué Windows muestra "Windows protegió su PC" al instalar?

Porque el instalador no está firmado con un certificado de pago. Es el aviso
estándar para software independiente, no una detección de nada. El código
fuente completo está en [`collector-app/`](collector-app/) para quien quiera
revisarlo o compilarlo por su cuenta:

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Privacidad

El recolector solo lee el historial de partidas del cliente de LoL y lo envía
al servidor del ranked del grupo. No lee nada más, no escribe nada en el
juego, y puedes cerrarlo cuando quieras desde el icono de la bandeja (Salir).

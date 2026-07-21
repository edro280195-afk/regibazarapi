# ADR 0002 — Plantillas de etiquetas versionadas como fuente única

**Estado:** aceptada · **Fecha:** 2026-07-21

## Contexto

Regi Bazar imprime tres artefactos físicos distintos: cajas de inventario NFC,
artículos de inventario y bolsas de pedidos. El diseño se prepara principalmente
en web, pero también se debe imprimir desde Android. Duplicar el editor o dejar
que cada dispositivo genere su propio formato haría imposible saber qué etiqueta
se reimprimió y provocaría diferencias de lectura entre impresoras.

## Decisión

1. El diseño se guarda como JSON versionado con coordenadas en milímetros. La
   web es el único editor; Android consume sólo la versión publicada.
2. Cada plantilla mantiene un borrador mutable y versiones publicadas
   inmutables. Publicar crea inmediatamente un nuevo borrador para el siguiente
   cambio.
3. Existe una única plantilla activa predeterminada por tipo de destino
   (`InventoryBox`, `InventoryItem`, `OrderPackage`). Cualquier reimpresión usa
   esa selección explícita, nunca el primer registro de una lista.
4. La API valida tamaño, límites, bindings requeridos y seguridad de QR/código de
   barras antes de guardar o publicar. Los activos visuales son imágenes
   administradas y no URLs arbitrarias del cliente.
5. El contexto de impresión se normaliza en la API. El renderer recibe sólo el
   diseño publicado, un mapa de datos permitido y los activos autorizados.
6. Cada solicitud de salida genera una bitácora con versión, destino, perfil de
   impresora, método y usuario. La bitácora registra la intención de impresión;
   no afirma que el hardware terminó físicamente.

## Consecuencias

- Una reimpresión conserva exactamente el diseño que estaba publicado cuando se
  envió, aun si después se modifica el borrador.
- Para cambiar el formato operativo, primero se publica y después se marca como
  predeterminado. Publicar por sí solo no cambia una operación en curso.
- Al archivar la predeterminada, la API promueve una alternativa publicada más
  reciente del mismo tipo; si no existe, la impresión se bloquea con un mensaje
  explícito en vez de usar una plantilla incierta.
- Los perfiles admitidos son intencionalmente cerrados: B1 50 × 50 mm para cajas
  y artículos; E40 Pro 4 × 6 in para bolsas. Esto evita imprimir un formato de
  bolsa en una etiqueta cuadrada por accidente.

## Integración de hardware

La salida se genera como PNG térmico a 203 DPI. En web se usa el diálogo de
impresión/compartir del dispositivo; Android comparte el PNG con la app oficial
de NIIMBOT o Label Expert, o abre el marco de impresión de Android. No se envían
bytes Bluetooth propietarios sin un SDK oficial verificable del fabricante.

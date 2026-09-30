# ADR 0003 — Las bolsas reales (con QR) son la fuente de verdad del número de bolsas

**Estado:** aceptada · **Fecha:** 2026-09-30

## Contexto

El sistema guardaba "las bolsas" de un pedido en dos lugares que no se
comunicaban:

- `Order.TotalPackages` + `PackagesConfirmed`: un **número** (agregado el
  2026-07-20 para capturar el dato en el alta y no olvidarlo).
- `OrderPackage`: las **bolsas reales** con su QR y estado (`Packed`, `Loaded`,
  `Delivered`). Es lo que ven "Logística y Etiquetas", la impresión de etiquetas
  y el escaneo del chofer.

El selector "¿Cuántas bolsas?" del alta solo escribía el número. El pedido
aparecía con "3 bolsas" en el Kanban, pero "Logística y Etiquetas" mostraba 0 y
al generar desde ahí el contador se sobrescribía (`0 + lo generado`), perdiendo
lo capturado. El chofer veía 0 bolsas que escanear y la puerta de ruta lo dejaba
pasar porque solo revisa `PackagesConfirmed`. La conexión existía en un solo
sentido (generar QR actualizaba el número, pero no al revés).

## Decisión

1. Las filas `OrderPackage` son la fuente de verdad. `Order.TotalPackages` es su
   reflejo y no se asigna por su cuenta.
2. Toda vía que "declare" cuántas bolsas lleva un pedido pasa por
   `OrderPackagePolicy` y crea las bolsas con QR que falten: alta manual
   (`POST /orders/manual`), edición (`PUT /orders/{id}`), set rápido
   (`PATCH /orders/{id}/packages`: Kanban, resolver de ruta, Android) y
   `POST /orders/{id}/packages/generate`.
3. El número es el **total deseado**, no un incremento. En un pedido abierto que
   ya tiene bolsas solo se crean las que faltan; pedir menos respeta las
   existentes.
4. **Nunca se borran bolsas automáticamente.** Una etiqueta ya impresa o pegada
   no se puede "desimprimir"; borrar sigue siendo una acción explícita
   (`DELETE /orders/{id}/packages/{packageId}`, solo en estado `Packed`). Si se
   pide menos de lo que existe, gana lo existente y el resumen devuelto trae el
   total real para que el cliente avise.
5. "Bolsas pendientes" (`PackagesConfirmed = false`) solo es válido mientras el
   pedido no tenga bolsas reales; con bolsas generadas el tema ya está resuelto.
6. En el alta, pedido y bolsas se guardan en una sola transacción: si falla la
   creación de bolsas no queda un pedido a medias.
7. Tope de 50 bolsas por pedido: ahora un número mal tecleado crea filas reales.

## Consecuencias

- Al capturar N bolsas en el alta, el pedido ya trae "Bolsa 1…N" listas para
  imprimir y todas las etiquetas dicen "de N" desde el principio (antes, generar
  en tandas producía "de 1" y "de 3" en el mismo pedido). No se imprime nada
  automáticamente al crear.
- Los pedidos capturados antes de este cambio tienen número pero no bolsas. No se
  migran en masa (no se modifican datos de producción de golpe): al abrir el
  pedido, "Logística y Etiquetas" avisa cuántas faltan y las genera con un toque.
- La puerta de ruta sigue revisando solo `PackagesConfirmed`; un pedido antiguo
  con número y sin bolsas aún puede salir a ruta hasta que se generen sus QR.
- Quien agregue un nuevo camino que toque las bolsas debe usar
  `OrderPackagePolicy`, no escribir `TotalPackages` directamente.

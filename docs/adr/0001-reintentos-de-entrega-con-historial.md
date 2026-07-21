# ADR-0001: Reintentos de entrega con historial

## Estado

Aceptada.

## Contexto

Una entrega no realizada debía poder programarse en una nueva ruta. El modelo anterior mantenía una relación uno a uno entre pedido y entrega, por lo que reutilizar el registro anterior borraba el motivo, firma y evidencia del intento fallido.

## Decisión

Un pedido conserva muchos intentos de entrega. `Order.DeliveryRouteId` representa solamente la asignación operativa vigente. Al fallar una visita, el pedido se libera y vuelve a `Pending`; la `Delivery` fallida permanece ligada a su ruta original. Al crear otra ruta se genera una `Delivery` nueva.

## Consecuencias

- Se puede reintentar un pedido sin editar la ruta pasada.
- La ruta anterior conserva evidencia y motivo de falla.
- Las métricas de no entrega se obtienen desde los intentos de entrega, no desde el estado operativo actual del pedido.
- La migración convierte los pedidos históricos `NotDelivered` en pendientes y libera su ruta, sin eliminar sus entregas previas.

## Alternativas descartadas

- Reutilizar la misma entrega: pierde trazabilidad del intento anterior.
- Eliminar la entrega fallida al reprogramar: elimina evidencia operativa.
- Mantener el pedido bloqueado hasta que alguien lo quite manualmente: genera el problema reportado.

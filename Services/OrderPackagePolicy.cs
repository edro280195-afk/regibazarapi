using EntregasApi.Models;

namespace EntregasApi.Services;

/// <summary>
/// Regla única de bolsas por pedido. Las bolsas reales (OrderPackage, con su QR) son la fuente
/// de verdad y Order.TotalPackages es solo su reflejo: cualquier endpoint que "declare" cuántas
/// bolsas lleva un pedido pasa por aquí, así el número del Kanban, las etiquetas y lo que escanea
/// el chofer nunca se separan.
///
/// Estas funciones NO agregan las bolsas nuevas a order.Packages: las devuelven para que el
/// controller las registre con _db.OrderPackages.AddRange (Add explícito). Meterlas por la
/// navegación con un Guid ya asignado haría que EF las tome como existentes (UPDATE en lugar de
/// INSERT), y el fixup posterior de EF haría que order.Packages.Count las cuente dos veces.
/// Requieren que order.Packages esté cargada y que el pedido ya tenga Id (el QR lo incluye).
/// </summary>
public static class OrderPackagePolicy
{
    /// <summary>Tope por pedido: evita crear cientos de QR por un dedazo en el selector.</summary>
    public const int MaxPackagesPerOrder = 50;

    /// <summary>Valida el número que llega en un request. Null = "no sé todavía", siempre válido.</summary>
    public static string? ValidateTotal(int? total) => total switch
    {
        < 0 => "El número de bolsas no puede ser negativo 🥺",
        > MaxPackagesPerOrder => TooManyMessage(),
        _ => null
    };

    /// <summary>Valida agregar `count` bolsas a un pedido que ya tiene `existing`.</summary>
    public static string? ValidateAddition(int existing, int count) =>
        existing + count > MaxPackagesPerOrder ? TooManyMessage() : null;

    private static string TooManyMessage() =>
        $"Un pedido no puede llevar más de {MaxPackagesPerOrder} bolsas 🥺";

    /// <summary>Crea `count` bolsas más a continuación de las que ya existen.</summary>
    public static PackageSyncResult AddPackages(Order order, int count, DateTime nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var existing = order.Packages.Count;
        var created = BuildPackages(order, count, nowUtc);
        SyncCounters(order, existing + created.Count, createdAny: true);
        return new PackageSyncResult(created, existing + count, existing + created.Count);
    }

    /// <summary>
    /// Hace que el pedido tenga al menos `total` bolsas reales: crea solo las que faltan y
    /// nunca borra (una bolsa ya impresa o pegada no se puede "desimprimir"). Si se pide menos
    /// de las que ya existen se respetan las existentes; el resultado lo indica.
    /// </summary>
    public static PackageSyncResult EnsureTotal(Order order, int total, DateTime nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);

        var existing = order.Packages.Count;
        var missing = Math.Max(0, total - existing);
        var created = missing > 0 ? BuildPackages(order, missing, nowUtc) : new List<OrderPackage>();
        SyncCounters(order, existing + created.Count, createdAny: created.Count > 0);
        return new PackageSyncResult(created, total, existing + created.Count);
    }

    /// <summary>
    /// "El pedido lleva N bolsas" (null = confirmar las que ya existan). Con confirmed = false
    /// lo regresa a "bolsas pendientes", pero solo si no hay bolsas reales: con bolsas ya
    /// generadas el tema está resuelto y ganan las filas.
    /// </summary>
    public static PackageSyncResult ApplyDeclaration(Order order, int? total, bool confirmed, DateTime nowUtc)
    {
        var existing = order.Packages.Count;

        if (!confirmed && existing == 0)
        {
            order.TotalPackages = total;
            order.PackagesConfirmed = false;
            return new PackageSyncResult(Array.Empty<OrderPackage>(), total ?? 0, 0);
        }

        return EnsureTotal(order, total ?? existing, nowUtc);
    }

    private static List<OrderPackage> BuildPackages(Order order, int count, DateTime nowUtc)
    {
        if (order.Id <= 0)
            throw new InvalidOperationException("El pedido debe estar guardado antes de generar sus bolsas.");

        // Numeración: a partir de la última (por si se agregan más días después o se borró alguna).
        var nextNumber = order.Packages.Count > 0 ? order.Packages.Max(p => p.PackageNumber) + 1 : 1;
        var created = new List<OrderPackage>(count);

        for (var i = 0; i < count; i++)
        {
            var packageId = Guid.NewGuid();
            created.Add(new OrderPackage
            {
                Id = packageId,
                OrderId = order.Id,
                PackageNumber = nextNumber + i,
                // QR infalible: RB-ORD[Id]-PKG[primeros 8 chars del Guid]
                QrCodeValue = $"RB-ORD{order.Id}-PKG{packageId.ToString().Substring(0, 8).ToUpper()}",
                Status = PackageTrackingStatus.Packed,
                CreatedAt = nowUtc
            });
        }

        return created;
    }

    private static void SyncCounters(Order order, int actualTotal, bool createdAny)
    {
        order.TotalPackages = actualTotal;
        order.PackagesConfirmed = true; // declarar o generar bolsas resuelve el pendiente
        order.PackagesReminderSentAt = null;

        if (createdAny)
        {
            order.IsFullyPacked = true;
            // Con bolsas nuevas el pedido ya no está completamente cargado en la camioneta.
            order.IsFullyLoaded = false;
        }
    }
}

/// <summary>
/// Resultado de sincronizar las bolsas de un pedido.
/// Created: bolsas nuevas por registrar. RequestedTotal: lo que se pidió. ActualTotal: las que
/// tiene el pedido al final (Created ya incluidas).
/// </summary>
public sealed record PackageSyncResult(IReadOnlyList<OrderPackage> Created, int RequestedTotal, int ActualTotal)
{
    /// <summary>Se pidió menos de lo que ya existía y se respetaron las bolsas actuales.</summary>
    public bool KeptMoreThanRequested => ActualTotal > RequestedTotal;
}

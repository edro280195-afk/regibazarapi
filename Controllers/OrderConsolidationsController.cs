using System.Data;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/orders/consolidations")]
[Authorize]
public class OrderConsolidationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly string _frontendUrl;

    public OrderConsolidationsController(AppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _frontendUrl = configuration["App:FrontendUrl"] ?? "https://regibazar.com";
    }

    [HttpGet("candidates")]
    public async Task<ActionResult<PagedResult<OrderSummaryDto>>> GetCandidates(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string search = "")
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = _db.Orders
            .AsNoTracking()
            .Include(order => order.Client)
            .Include(order => order.Items)
            .Include(order => order.Payments)
            .Include(order => order.Packages)
            .Where(order => order.OrderConsolidationId == null
                && order.DeliveryRouteId == null
                && order.OrderType == OrderType.Delivery
                && !order.Packages.Any(package => package.Status == PackageTrackingStatus.Loaded)
                && (order.Status == OrderStatus.Pending || order.Status == OrderStatus.Confirmed));

        var trimmedSearch = search.Trim();
        if (!string.IsNullOrEmpty(trimmedSearch))
        {
            var normalizedSearch = trimmedSearch.ToLower();
            var hasOrderId = int.TryParse(trimmedSearch.TrimStart('#'), out var orderId);
            query = query.Where(order =>
                (hasOrderId && order.Id == orderId)
                || order.Client.Name.ToLower().Contains(normalizedSearch)
                || order.Items.Any(item => item.ProductName.ToLower().Contains(normalizedSearch)));
        }

        var totalCount = await query.CountAsync();
        var orders = await query
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = orders
            .Select(order => ExcelService.MapToSummary(order, order.Client, _frontendUrl))
            .ToList();

        return Ok(new PagedResult<OrderSummaryDto>(items, totalCount, page, pageSize));
    }

    [HttpGet]
    public async Task<ActionResult<List<OrderConsolidationDto>>> GetAll()
    {
        var consolidations = await GetConsolidationQuery()
            .AsNoTracking()
            .OrderByDescending(consolidation => consolidation.CreatedAtUtc)
            .Take(100)
            .ToListAsync();

        return Ok(consolidations.Select(MapConsolidation).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderConsolidationDto>> GetById(int id)
    {
        var consolidation = await GetConsolidationQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id);

        if (consolidation == null)
            return NotFound("No encontramos esa bolsa consolidada.");

        return Ok(MapConsolidation(consolidation));
    }

    [HttpPost]
    public async Task<ActionResult<OrderConsolidationDto>> Create([FromBody] CreateOrderConsolidationRequest request)
    {
        var orderIds = request.OrderIds?.Distinct().ToList() ?? new List<int>();
        if (orderIds.Count < 2)
            return BadRequest("Selecciona al menos dos pedidos para preparar una bolsa.");

        if (orderIds.Any(orderId => orderId <= 0))
            return BadRequest("La lista contiene un número de pedido inválido.");

        if (!orderIds.Contains(request.PrimaryOrderId))
            return BadRequest("El pedido principal debe formar parte de la bolsa.");

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var orders = await _db.Orders
            .Where(order => orderIds.Contains(order.Id))
            .Include(order => order.Client)
            .Include(order => order.Items)
            .Include(order => order.Payments)
            .Include(order => order.Packages)
            .OrderBy(order => order.Id)
            .ToListAsync();

        if (orders.Count != orderIds.Count)
            return NotFound("Uno o más pedidos ya no están disponibles. Actualiza la lista e inténtalo de nuevo.");

        var unavailableOrder = orders.FirstOrDefault(order =>
            order.OrderConsolidationId != null
            || order.DeliveryRouteId != null
            || order.OrderType != OrderType.Delivery
            || order.Packages.Any(package => package.Status == PackageTrackingStatus.Loaded)
            || (order.Status != OrderStatus.Pending && order.Status != OrderStatus.Confirmed));

        if (unavailableOrder != null)
            return Conflict($"El pedido #{unavailableOrder.Id} ya cambió o no se puede incluir en esta bolsa. Actualiza la lista.");

        var primaryOrder = orders.First(order => order.Id == request.PrimaryOrderId);
        var primaryAddress = string.IsNullOrWhiteSpace(primaryOrder.AlternativeAddress)
            ? primaryOrder.Client.Address?.Trim()
            : primaryOrder.AlternativeAddress.Trim();

        if (string.IsNullOrWhiteSpace(primaryAddress))
            return BadRequest("La clienta principal necesita un domicilio para esta bolsa.");

        var primaryClientName = primaryOrder.Client.Name.Trim();
        var deliveryInstructions = (primaryOrder.DeliveryInstructions ?? primaryOrder.Client.DeliveryInstructions)?.Trim();
        if (primaryClientName.Length > 200)
            return BadRequest("El nombre de la clienta principal excede el límite permitido.");
        if (primaryAddress.Length > 500)
            return BadRequest("El domicilio de entrega excede el límite permitido.");
        if (deliveryInstructions?.Length > 1000)
            return BadRequest("Las instrucciones de entrega exceden el límite permitido.");

        var consolidation = new OrderConsolidation
        {
            PrimaryOrderId = primaryOrder.Id,
            PrimaryClientId = primaryOrder.ClientId,
            PrimaryClientNameSnapshot = primaryClientName,
            PrimaryAddressSnapshot = primaryAddress,
            PrimaryLatitude = primaryOrder.Client.Latitude,
            PrimaryLongitude = primaryOrder.Client.Longitude,
            DeliveryInstructionsSnapshot = deliveryInstructions,
            CreatedAtUtc = DateTime.UtcNow,
            Orders = orders
        };

        foreach (var order in orders)
        {
            order.ConsolidationOwnerClientId = order.ClientId;
            order.ConsolidationOwnerNameSnapshot = order.Client.Name.Trim();
            order.OrderConsolidation = consolidation;
        }

        _db.OrderConsolidations.Add(consolidation);

        try
        {
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception exception) when (IsConsolidationConflict(exception))
        {
            await transaction.RollbackAsync();
            return Conflict("Uno o más pedidos se acaban de agregar a otra bolsa. Actualiza la lista.");
        }

        var createdConsolidation = await GetConsolidationQuery()
            .AsNoTracking()
            .FirstAsync(item => item.Id == consolidation.Id);

        return CreatedAtAction(nameof(GetById), new { id = consolidation.Id }, MapConsolidation(createdConsolidation));
    }

    private IQueryable<OrderConsolidation> GetConsolidationQuery()
    {
        return _db.OrderConsolidations
            .Include(consolidation => consolidation.Orders)
                .ThenInclude(order => order.Client)
            .Include(consolidation => consolidation.Orders)
                .ThenInclude(order => order.Items)
            .Include(consolidation => consolidation.Orders)
                .ThenInclude(order => order.Payments);
    }

    private OrderConsolidationDto MapConsolidation(OrderConsolidation consolidation)
    {
        var orders = consolidation.Orders
            .OrderBy(order => order.Id)
            .Select(order => new OrderConsolidationMemberDto(
                order.Id,
                order.ConsolidationOwnerClientId ?? order.ClientId,
                order.ConsolidationOwnerNameSnapshot ?? order.Client.Name,
                ExcelService.MapToSummary(order, order.Client, _frontendUrl)))
            .ToList();

        return new OrderConsolidationDto(
            consolidation.Id,
            consolidation.PrimaryOrderId,
            consolidation.PrimaryClientId,
            consolidation.PrimaryClientNameSnapshot,
            consolidation.PrimaryAddressSnapshot,
            consolidation.PrimaryLatitude,
            consolidation.PrimaryLongitude,
            consolidation.DeliveryInstructionsSnapshot,
            consolidation.CreatedAtUtc,
            orders,
            orders.Sum(member => member.Order.Total),
            orders.Sum(member => member.Order.AmountPaid),
            orders.Sum(member => member.Order.BalanceDue));
    }

    private static bool IsConsolidationConflict(Exception exception)
    {
        if (exception is not DbUpdateException && exception is not PostgresException)
            return false;

        var postgresException = exception.GetBaseException() as PostgresException;
        return postgresException?.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation;
    }
}

using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/live")]
[Authorize]
public class LiveCaptureController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILiveCaptureQueue _queue;
    private readonly ILiveCaptureService _liveCaptureService;
    private readonly IClientResolverService _clientResolver;
    private readonly ITokenService _tokenService;
    private readonly IOrderService _orderService;
    private readonly IConfiguration _config;

    public LiveCaptureController(
        AppDbContext db,
        ILiveCaptureQueue queue,
        ILiveCaptureService liveCaptureService,
        IClientResolverService clientResolver,
        ITokenService tokenService,
        IOrderService orderService,
        IConfiguration config)
    {
        _db = db;
        _queue = queue;
        _liveCaptureService = liveCaptureService;
        _clientResolver = clientResolver;
        _tokenService = tokenService;
        _orderService = orderService;
        _config = config;
    }

    [HttpPost("import")]
    public async Task<ActionResult<LiveSessionDto>> Import([FromBody] ImportLiveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FacebookUrl))
            return BadRequest("Pega el URL del live de Facebook.");

        if (!Uri.TryCreate(request.FacebookUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("El URL del live no es valido.");
        }

        var session = new LiveSession
        {
            FacebookUrl = request.FacebookUrl.Trim(),
            Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim(),
            ImportedAt = DateTime.UtcNow,
            Status = LiveSessionStatus.Downloading
        };

        _db.LiveSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        await _queue.QueueAsync(session.Id, cancellationToken);

        return Ok(await MapSessionAsync(session.Id, cancellationToken));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LiveSessionDto>> GetSession(int id, CancellationToken cancellationToken)
    {
        var dto = await MapSessionAsync(id, cancellationToken);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpGet("{id:int}/products")]
    public async Task<ActionResult<List<LiveProductDto>>> GetProducts(int id, CancellationToken cancellationToken)
    {
        var exists = await _db.LiveSessions.AnyAsync(s => s.Id == id, cancellationToken);
        if (!exists) return NotFound();

        var products = await _db.LiveProducts
            .Where(p => p.LiveSessionId == id)
            .OrderBy(p => p.AnnouncedAtSeconds)
            .Select(p => new LiveProductDto(
                p.Id,
                p.Keyword,
                p.Description,
                p.Price,
                p.AnnouncedAtSeconds,
                p.Confidence,
                p.Candidates.Count))
            .ToListAsync(cancellationToken);

        return Ok(products);
    }

    [HttpGet("{id:int}/candidates")]
    public async Task<ActionResult<PagedResult<LiveCandidateDto>>> GetCandidates(
        int id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] int? productId = null,
        [FromQuery] string? status = null,
        [FromQuery] bool includeResolution = true,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.LiveCandidates
            .Include(c => c.LiveProduct)
            .Include(c => c.ResolvedClient)
            .Where(c => c.LiveSessionId == id);

        if (productId.HasValue)
            query = query.Where(c => c.LiveProductId == productId.Value);

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<LiveCandidateStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(c => c.Status == parsedStatus);
        }

        var total = await query.CountAsync(cancellationToken);
        var candidates = await query
            .OrderBy(c => c.LiveProduct == null ? double.MaxValue : c.LiveProduct.AnnouncedAtSeconds)
            .ThenBy(c => c.SpokenAtSeconds ?? c.CommentedAtSeconds ?? 0)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var dtos = new List<LiveCandidateDto>();
        foreach (var candidate in candidates)
        {
            ResolveClientResponse? resolution = null;
            if (includeResolution && candidate.Status == LiveCandidateStatus.Pending)
            {
                var name = candidate.ClientNameSpoken ?? candidate.CommentDisplayName;
                if (!string.IsNullOrWhiteSpace(name))
                    resolution = await _clientResolver.ResolveAsync(name, null, null);
            }

            dtos.Add(MapCandidate(candidate, resolution));
        }

        return Ok(new PagedResult<LiveCandidateDto>(dtos, total, page, pageSize));
    }

    [HttpGet("{id:int}/clip")]
    public async Task<IActionResult> GetClip(
        int id,
        [FromQuery] double at = 0,
        [FromQuery] int duration = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var clip = await _liveCaptureService.GetClipAsync(id, at, duration, cancellationToken);
            return File(clip.Content, clip.ContentType, clip.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("candidates/{id:int}/confirm")]
    public async Task<ActionResult<LiveCandidateDto>> ConfirmCandidate(
        int id,
        [FromBody] ConfirmLiveCandidateRequest request,
        CancellationToken cancellationToken)
    {
        var candidate = await _db.LiveCandidates
            .Include(c => c.LiveProduct)
            .Include(c => c.ResolvedClient)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (candidate == null) return NotFound();
        if (candidate.Status == LiveCandidateStatus.Confirmed)
            return BadRequest("Este candidato ya fue confirmado.");
        if (candidate.Status == LiveCandidateStatus.Ignored)
            return BadRequest("Este candidato esta ignorado; reactivalo antes de confirmar.");

        var client = await ResolveOrCreateClientAsync(candidate, request.ClientId ?? candidate.ResolvedClientId, cancellationToken);
        if (client == null)
            return BadRequest("Selecciona una clienta o deja un nombre detectado para crearla.");

        using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var order = await CreateOrMergeOrderAsync(candidate, client, request, cancellationToken);

        if (request.AcceptProposedAlias)
        {
            await AddLiveAliasIfNeededAsync(candidate, client.Id);
        }

        candidate.ResolvedClientId = client.Id;
        candidate.CreatedOrderId = order.Id;
        candidate.Status = LiveCandidateStatus.Confirmed;
        candidate.ReviewedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        candidate.ResolvedClient = client;
        return Ok(MapCandidate(candidate));
    }

    [HttpPost("candidates/{id:int}/ignore")]
    public async Task<ActionResult<LiveCandidateDto>> IgnoreCandidate(int id, CancellationToken cancellationToken)
    {
        var candidate = await _db.LiveCandidates
            .Include(c => c.LiveProduct)
            .Include(c => c.ResolvedClient)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (candidate == null) return NotFound();

        candidate.Status = LiveCandidateStatus.Ignored;
        candidate.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(MapCandidate(candidate));
    }

    private async Task<Order> CreateOrMergeOrderAsync(
        LiveCandidate candidate,
        Client client,
        ConfirmLiveCandidateRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _db.AppSettings.FirstAsync(cancellationToken);
        var productName = !string.IsNullOrWhiteSpace(request.ProductOverride)
            ? request.ProductOverride.Trim()
            : candidate.LiveProduct?.Description ?? candidate.Keyword;
        var price = request.PriceOverride ?? candidate.LiveProduct?.Price ?? 0m;

        var existingOrder = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.ClientId == client.Id && o.Status == OrderStatus.Pending, cancellationToken);

        if (existingOrder != null)
        {
            existingOrder.OrderType = OrderType.Delivery;
            existingOrder.ShippingCost = settings.DefaultShippingCost;
            existingOrder.Items.Add(new OrderItem
            {
                ProductName = productName,
                Quantity = 1,
                UnitPrice = price,
                LineTotal = price
            });
            existingOrder.Subtotal = existingOrder.Items.Sum(i => i.LineTotal);
            existingOrder.Total = Math.Max(0, existingOrder.Subtotal + existingOrder.ShippingCost - existingOrder.DiscountAmount);
            existingOrder.CreatedAt = DateTime.UtcNow;
            var dates = _orderService.CalculateOrderDates(client.Type, existingOrder.CreatedAt, null);
            existingOrder.ExpiresAt = dates.ExpiresAt;
            existingOrder.ScheduledDeliveryDate = dates.ScheduledDeliveryDate;
            return existingOrder;
        }

        var accessToken = _tokenService.GenerateAccessToken();
        var newDates = _orderService.CalculateOrderDates(client.Type, DateTime.UtcNow, null);
        var newOrder = new Order
        {
            ClientId = client.Id,
            AccessToken = accessToken,
            ShippingCost = settings.DefaultShippingCost,
            ExpiresAt = newDates.ExpiresAt,
            ScheduledDeliveryDate = newDates.ScheduledDeliveryDate,
            Status = OrderStatus.Pending,
            OrderType = OrderType.Delivery,
            CreatedAt = DateTime.UtcNow,
            SalesPeriodId = (await _db.SalesPeriods.FirstOrDefaultAsync(p => p.IsActive, cancellationToken))?.Id,
            DeliveryInstructions = client.DeliveryInstructions,
            Items = new List<OrderItem>
            {
                new()
                {
                    ProductName = productName,
                    Quantity = 1,
                    UnitPrice = price,
                    LineTotal = price
                }
            }
        };

        newOrder.Subtotal = newOrder.Items.Sum(i => i.LineTotal);
        newOrder.Total = Math.Max(0, newOrder.Subtotal + newOrder.ShippingCost - newOrder.DiscountAmount);
        _db.Orders.Add(newOrder);
        return newOrder;
    }

    private async Task<Client?> ResolveOrCreateClientAsync(
        LiveCandidate candidate,
        int? clientId,
        CancellationToken cancellationToken)
    {
        if (clientId.HasValue)
        {
            return await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId.Value, cancellationToken);
        }

        var detectedName = (candidate.ClientNameSpoken ?? candidate.CommentDisplayName)?.Trim();
        if (string.IsNullOrWhiteSpace(detectedName)) return null;

        var normalizedName = TextNormalizer.NormalizeName(detectedName);
        var existing = await _db.Clients.FirstOrDefaultAsync(c => c.NormalizedName == normalizedName, cancellationToken);
        if (existing != null) return existing;

        var client = new Client
        {
            Name = detectedName,
            Type = "Nueva",
            CreatedAt = DateTime.UtcNow,
            NormalizedName = normalizedName
        };

        _db.Clients.Add(client);
        await _db.SaveChangesAsync(cancellationToken);
        return client;
    }

    private async Task AddLiveAliasIfNeededAsync(LiveCandidate candidate, int clientId)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ProposedAlias))
        {
            await _clientResolver.AddAliasAsync(clientId, candidate.ProposedAlias, ClientAliasSource.LiveOcr);
            return;
        }

        var displayName = candidate.CommentDisplayName?.Trim();
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            await _clientResolver.AddAliasAsync(clientId, displayName, ClientAliasSource.LiveOcr);
        }
    }

    private async Task<LiveSessionDto?> MapSessionAsync(int id, CancellationToken cancellationToken)
    {
        return await _db.LiveSessions
            .Where(s => s.Id == id)
            .Select(s => new LiveSessionDto(
                s.Id,
                s.FacebookUrl,
                s.R2Key,
                s.Title,
                s.ImportedAt,
                s.Status.ToString(),
                s.DurationSeconds,
                s.ProcessedAt,
                s.ErrorMessage,
                s.Products.Count,
                s.Candidates.Count,
                s.Candidates.Count(c => c.Status == LiveCandidateStatus.Pending),
                s.Candidates.Count(c => c.Status == LiveCandidateStatus.Confirmed),
                s.Candidates.Count(c => c.Status == LiveCandidateStatus.Ignored)))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private LiveCandidateDto MapCandidate(LiveCandidate candidate, ResolveClientResponse? resolution = null)
    {
        return new LiveCandidateDto(
            candidate.Id,
            candidate.LiveSessionId,
            candidate.LiveProductId,
            candidate.Keyword,
            candidate.LiveProduct?.Description,
            candidate.LiveProduct?.Price,
            candidate.ClientNameSpoken,
            candidate.CommentDisplayName,
            candidate.ResolvedClientId,
            candidate.ResolvedClient?.Name,
            candidate.ProposedAlias != null && candidate.ProposedCanonicalName != null
                ? new ProposedAliasPairDto(candidate.ProposedAlias, candidate.ProposedCanonicalName)
                : null,
            candidate.Source.ToString(),
            candidate.Status.ToString(),
            candidate.Confidence,
            candidate.SpokenAtSeconds,
            candidate.CommentedAtSeconds,
            candidate.CreatedOrderId,
            candidate.CreatedAt,
            candidate.ReviewedAt,
            resolution);
    }
}

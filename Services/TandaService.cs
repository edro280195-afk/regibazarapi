using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public class TandaService : ITandaService
{
    private readonly AppDbContext _db;

    public TandaService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<TandaDto> CreateTandaAsync(CreateTandaDto dto)
    {
        var product = await _db.TandaProducts.FirstOrDefaultAsync(p => p.Id == dto.ProductId && p.IsActive);
        if (product == null)
            throw new Exception("El producto especificado no existe o no está activo.");

        TandaTurnPlanner.ValidateCompleteAssignments(
            dto.TotalWeeks,
            dto.Participants.Select(p => p.AssignedTurn).ToList());

        var clientIds = dto.Participants
            .Select(p => p.CustomerId)
            .Distinct()
            .ToList();
        var clients = await _db.Clients
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        if (clientIds.Any(id => !clients.ContainsKey(id)))
            throw new InvalidOperationException("Una o más clientas seleccionadas ya no existen.");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var tanda = new Tanda
        {
            Id = Guid.NewGuid(),
            ProductId = dto.ProductId,
            Product = product,
            Name = dto.Name,
            TotalWeeks = dto.TotalWeeks,
            WeeklyAmount = dto.WeeklyAmount,
            PenaltyAmount = dto.PenaltyAmount,
            StartDate = dto.StartDate,
            Status = "Active",
            AccessToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
        };

        foreach (var assignment in dto.Participants.OrderBy(p => p.AssignedTurn))
        {
            var participant = new TandaParticipant
            {
                Id = Guid.NewGuid(),
                TandaId = tanda.Id,
                CustomerId = assignment.CustomerId,
                Client = clients[assignment.CustomerId],
                PublicToken = NewPublicToken(),
                AssignedTurn = assignment.AssignedTurn,
                Status = "Active",
                Variant = string.IsNullOrWhiteSpace(assignment.Variant)
                    ? null
                    : assignment.Variant.Trim(),
                WeeklyAmount = assignment.WeeklyAmount
            };

            participant.Items = BuildParticipantItems(
                assignment.Items,
                product,
                participant.Variant,
                assignment.WeeklyAmount ?? dto.WeeklyAmount);
            foreach (var item in participant.Items)
                item.ParticipantId = participant.Id;

            tanda.Participants.Add(participant);
        }

        _db.Tandas.Add(tanda);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return MapToTandaDto(tanda);
    }

    public async Task<TandaParticipantDto> AddParticipantAsync(AddParticipantDto dto)
    {
        var tanda = await _db.Tandas.FindAsync(dto.TandaId);
        if (tanda == null)
            throw new Exception("La tanda especificada no existe.");

        if (dto.AssignedTurn < 1 || dto.AssignedTurn > tanda.TotalWeeks)
            throw new Exception("El turno asignado está fuera de los límites de las semanas de la tanda.");

        var isTurnOccupied = await _db.TandaParticipants
            .AnyAsync(p => p.TandaId == dto.TandaId && p.AssignedTurn == dto.AssignedTurn);

        if (isTurnOccupied)
            throw new Exception($"El turno {dto.AssignedTurn} ya está ocupado en esta tanda.");

        var participant = new TandaParticipant
        {
            Id = Guid.NewGuid(),
            TandaId = dto.TandaId,
            CustomerId = dto.CustomerId,
            PublicToken = NewPublicToken(),
            AssignedTurn = dto.AssignedTurn,
            Status = "Active",
            Variant = dto.Variant,
            WeeklyAmount = dto.WeeklyAmount
        };

        var product = await _db.TandaProducts.FirstOrDefaultAsync(p => p.Id == tanda.ProductId);
        participant.Items = BuildParticipantItems(
            dto.Items,
            product,
            dto.Variant,
            dto.WeeklyAmount ?? tanda.WeeklyAmount);
        foreach (var item in participant.Items)
            item.ParticipantId = participant.Id;

        _db.TandaParticipants.Add(participant);
        await _db.SaveChangesAsync();

        return MapToParticipantDto(participant);
    }

    public async Task<TandaPaymentDto> RegisterPaymentAsync(RegisterPaymentDto dto)
    {
        // Eliminada la restricción de día de la semana para pagos (a petición de la administradora)

        var participant = await _db.TandaParticipants.FindAsync(dto.ParticipantId);
        if (participant == null)
            throw new Exception("Participante no encontrado.");

        var payment = new TandaPayment
        {
            ParticipantId = dto.ParticipantId,
            WeekNumber = dto.WeekNumber,
            AmountPaid = dto.AmountPaid,
            PenaltyPaid = dto.PenaltyPaid,
            PaymentDate = DateTime.UtcNow,
            IsVerified = true,
            Notes = dto.Notes
        };

        _db.TandaPayments.Add(payment);
        await _db.SaveChangesAsync();

        return MapToPaymentDto(payment);
    }

    public async Task<TandaPaymentDto> VerifyPaymentAsync(Guid paymentId, VerifyTandaPaymentDto dto)
    {
        var payment = await _db.TandaPayments.FindAsync(paymentId);
        if (payment == null)
            throw new Exception("El registro de pago no existe.");

        if (dto.AmountPaid.HasValue)
        {
            if (dto.AmountPaid.Value < 0)
                throw new Exception("El importe no puede ser negativo.");
            payment.AmountPaid = dto.AmountPaid.Value;
        }

        if (dto.DepositDate.HasValue)
            payment.DepositDate = EnsureUtc(dto.DepositDate.Value);

        payment.IsVerified = dto.IsVerified;
        if (dto.Notes != null)
            payment.Notes = dto.Notes;

        await _db.SaveChangesAsync();
        return MapToPaymentDto(payment);
    }

    public async Task<TandaParticipantDto?> GetSundayDeliveryAsync(Guid tandaId)
    {
        var tanda = await _db.Tandas.FindAsync(tandaId);
        if (tanda == null)
            throw new Exception("La tanda especificada no existe.");

        int currentWeek = CalculateCurrentWeek(tanda.StartDate);

        if (currentWeek < 1 || currentWeek > tanda.TotalWeeks)
            return null;

        var participant = await _db.TandaParticipants
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.TandaId == tandaId && p.AssignedTurn == currentWeek);

        return participant != null ? MapToParticipantDto(participant) : null;
    }

    public async Task UpdateParticipantTurnAsync(Guid participantId, int newTurn)
    {
        var participant = await _db.TandaParticipants.FindAsync(participantId);
        if (participant == null) throw new Exception("Participante no encontrado");

        var tanda = await _db.Tandas.FindAsync(participant.TandaId);
        if (tanda == null) throw new Exception("Tanda no encontrada");

        if (newTurn < 1 || newTurn > tanda.TotalWeeks)
            throw new Exception($"El turno {newTurn} está fuera de los límites (1-{tanda.TotalWeeks})");

        // Validar si el turno ya está ocupado
        var existing = await _db.TandaParticipants
            .FirstOrDefaultAsync(p => p.TandaId == participant.TandaId && p.AssignedTurn == newTurn && p.Id != participantId);

        if (existing != null)
            throw new Exception($"El turno {newTurn} ya está ocupado por {existing.CustomerName ?? "otra persona"}");

        participant.AssignedTurn = newTurn;
        await _db.SaveChangesAsync();
    }

    public async Task UpdateParticipantVariantAsync(Guid participantId, string? variant)
    {
        var participant = await _db.TandaParticipants.FindAsync(participantId);
        if (participant == null) throw new Exception("Participante no encontrado");

        participant.Variant = variant;
        await _db.SaveChangesAsync();
    }

    public async Task ReplaceParticipantItemsAsync(Guid participantId, ReplaceTandaParticipantItemsDto dto)
    {
        var participant = await _db.TandaParticipants
            .Include(p => p.Items)
            .Include(p => p.Tanda)
                .ThenInclude(t => t!.Product)
            .FirstOrDefaultAsync(p => p.Id == participantId);

        if (participant == null)
            throw new Exception("Participante no encontrado.");

        if (dto.Items.Count == 0)
            throw new Exception("Agrega al menos un artículo a la clienta.");

        ValidateItems(dto.Items);
        _db.TandaParticipantItems.RemoveRange(participant.Items);
        // Desde este punto el cobro se deriva de los artículos configurados.
        participant.WeeklyAmount = null;
        participant.Items = BuildParticipantItems(
            dto.Items,
            participant.Tanda?.Product,
            participant.Variant,
            participant.WeeklyAmount ?? participant.Tanda?.WeeklyAmount ?? 0);
        foreach (var item in participant.Items)
            item.ParticipantId = participant.Id;

        await _db.SaveChangesAsync();
    }

    public async Task ConfirmParticipantDeliveryAsync(Guid participantId)
    {
        var participant = await _db.TandaParticipants.FindAsync(participantId);
        if (participant == null) throw new Exception("Participante no encontrado");

        participant.IsDelivered = true;
        participant.DeliveryDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task RemoveParticipantAsync(Guid participantId)
    {
        var participant = await _db.TandaParticipants
            .Include(p => p.Payments)
            .FirstOrDefaultAsync(p => p.Id == participantId);

        if (participant == null) throw new Exception("Participante no encontrado");

        // Eliminar pagos asociados para mantener integridad
        if (participant.Payments.Any())
        {
            _db.TandaPayments.RemoveRange(participant.Payments);
        }

        _db.TandaParticipants.Remove(participant);
        await _db.SaveChangesAsync();
    }

    public async Task ProcessPenaltiesAsync(Guid tandaId)
    {
        var tanda = await _db.Tandas
            .Include(t => t.Participants)
                .ThenInclude(p => p.Payments)
            .FirstOrDefaultAsync(t => t.Id == tandaId);

        if (tanda == null) throw new Exception("La tanda especificada no existe.");

        int currentWeek = CalculateCurrentWeek(tanda.StartDate);

        foreach (var participant in tanda.Participants.Where(p => p.Status == "Active"))
        {
            bool hasPaidCurrentWeek = participant.Payments.Any(p => p.WeekNumber == currentWeek && p.IsVerified);
            
            if (!hasPaidCurrentWeek)
            {
                participant.Status = "Delinquent";
            }
        }

        await _db.SaveChangesAsync();
    }

    public async Task<List<TandaProductDto>> GetProductsAsync()
    {
        var products = await _db.TandaProducts
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();
            
        return products.Select(MapToProductDto).ToList();
    }

    public async Task<TandaProductDto> CreateProductAsync(string name, decimal basePrice)
    {
        var product = new TandaProduct
        {
            Id = Guid.NewGuid(),
            Name = name,
            BasePrice = basePrice,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.TandaProducts.Add(product);
        await _db.SaveChangesAsync();

        return MapToProductDto(product);
    }

    public async Task<List<TandaDto>> GetTandasAsync()
    {
        var tandas = await _db.Tandas
            .Include(t => t.Product)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
            
        return tandas.Select(MapToTandaDto).ToList();
    }

    public async Task<TandaDto?> GetTandaByIdAsync(Guid id)
    {
        var tanda = await _db.Tandas
            .Include(t => t.Product)
            .Include(t => t.Participants)
                .ThenInclude(p => p.Client)
            .Include(t => t.Participants)
                .ThenInclude(p => p.Payments)
            .Include(t => t.Participants)
                .ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(t => t.Id == id);

        return tanda != null ? MapToTandaDto(tanda) : null;
    }

    public async Task<TandaViewDto?> GetTandaByTokenAsync(string token)
    {
        var participant = await _db.TandaParticipants
            .Include(p => p.Client)
            .Include(p => p.Payments)
            .Include(p => p.Items)
            .Include(p => p.Tanda)
                .ThenInclude(t => t!.Product)
            .FirstOrDefaultAsync(p => p.PublicToken == token);

        if (participant?.Tanda != null)
            return MapToPublicTandaView(participant.Tanda, participant);

        var tanda = await _db.Tandas
            .Include(t => t.Product)
            .Include(t => t.Participants)
                .ThenInclude(p => p.Client)
            .Include(t => t.Participants)
                .ThenInclude(p => p.Payments)
            .Include(t => t.Participants)
                .ThenInclude(p => p.Items)
            .FirstOrDefaultAsync(t => t.AccessToken == token);

        if (tanda == null) return null;

        return MapToPublicTandaView(tanda, null);
    }

    private TandaViewDto MapToPublicTandaView(Tanda tanda, TandaParticipant? scopedParticipant)
    {
        int currentWeek = CalculateCurrentWeek(tanda.StartDate);
        IEnumerable<TandaParticipant> sourceParticipants = scopedParticipant != null
            ? new[] { scopedParticipant }
            : tanda.Participants.OrderBy(p => p.AssignedTurn);

        var participants = sourceParticipants.Select(p => new TandaParticipantViewDto
        {
            Id = p.Id,
            Name = scopedParticipant != null
                ? (p.Client?.Name ?? "Participante")
                : AnonymizeName(p.Client?.Name ?? "Participante"),
            AssignedTurn = p.AssignedTurn,
            HasPaidCurrentWeek = p.Payments.Any(pay => pay.WeekNumber == currentWeek && pay.IsVerified),
            PaidWeeks = p.Payments.Where(pay => pay.IsVerified).Select(pay => pay.WeekNumber).ToList(),
            IsWinnerThisWeek = p.AssignedTurn == currentWeek,
            IsDelivered = p.IsDelivered,
            Variant = p.Variant,
            WeeklyAmount = p.WeeklyAmount ?? CalculateWeeklyAmount(tanda, p),
            PublicToken = p.PublicToken,
            Items = p.Items.Select(MapToItemDto).ToList(),
            Payments = p.Payments.Select(MapToPaymentDto).OrderByDescending(pay => pay.WeekNumber).ToList()
        }).ToList();

        return new TandaViewDto
        {
            Id = tanda.Id,
            Name = tanda.Name,
            ProductName = tanda.Product?.Name ?? "Producto Tanda",
            TotalWeeks = tanda.TotalWeeks,
            WeeklyAmount = tanda.WeeklyAmount,
            StartDate = tanda.StartDate,
            CurrentWeek = currentWeek,
            Participants = participants,
            Participant = scopedParticipant != null ? participants.FirstOrDefault() : null
        };
    }

    public async Task<TandaDto> UpdateTandaAsync(Guid id, UpdateTandaDto dto)
    {
        var tanda = await _db.Tandas.FindAsync(id);
        if (tanda == null) throw new Exception("Tanda no encontrada");

        tanda.Name = dto.Name;
        tanda.TotalWeeks = dto.TotalWeeks;
        tanda.WeeklyAmount = dto.WeeklyAmount;
        tanda.PenaltyAmount = dto.PenaltyAmount;
        tanda.StartDate = dto.StartDate;

        await _db.SaveChangesAsync();
        return MapToTandaDto(tanda);
    }

    // ── Mapeos ──
    private TandaDto MapToTandaDto(Tanda t) => new TandaDto
    {
        Id = t.Id,
        ProductId = t.ProductId,
        Name = t.Name,
        TotalWeeks = t.TotalWeeks,
        WeeklyAmount = t.WeeklyAmount,
        PenaltyAmount = t.PenaltyAmount,
        StartDate = t.StartDate,
        Status = t.Status,
        CreatedAt = t.CreatedAt,
        AccessToken = t.AccessToken,
        Product = t.Product != null ? MapToProductDto(t.Product) : null,
        Participants = t.Participants?.Select(MapToParticipantDto).ToList()
    };

    private TandaParticipantDto MapToParticipantDto(TandaParticipant p) => new TandaParticipantDto
    {
        Id = p.Id,
        TandaId = p.TandaId,
        CustomerId = p.CustomerId,
        CustomerName = p.Client?.Name ?? p.CustomerName,
        PublicToken = p.PublicToken,
        AssignedTurn = p.AssignedTurn,
        IsDelivered = p.IsDelivered,
        DeliveryDate = p.DeliveryDate,
        Status = p.Status,
        Variant = p.Variant,
        WeeklyAmount = p.WeeklyAmount,
        Payments = p.Payments?.Select(MapToPaymentDto).ToList(),
        Items = p.Items?.Select(MapToItemDto).ToList()
    };

    private TandaPaymentDto MapToPaymentDto(TandaPayment pay) => new TandaPaymentDto
    {
        Id = pay.Id,
        ParticipantId = pay.ParticipantId,
        WeekNumber = pay.WeekNumber,
        AmountPaid = pay.AmountPaid,
        PenaltyPaid = pay.PenaltyPaid,
        PaymentDate = pay.PaymentDate,
        DepositDate = pay.DepositDate,
        OcrAmount = pay.OcrAmount,
        ProofUrl = pay.ProofUrl,
        OcrText = pay.OcrText,
        OcrConfidence = pay.OcrConfidence,
        PaymentMethod = pay.PaymentMethod,
        IsVerified = pay.IsVerified,
        Notes = pay.Notes
    };

    private TandaParticipantItemDto MapToItemDto(TandaParticipantItem item) => new TandaParticipantItemDto
    {
        Id = item.Id,
        ParticipantId = item.ParticipantId,
        ProductId = item.ProductId,
        ProductName = item.ProductName,
        Quantity = item.Quantity,
        UnitPrice = item.UnitPrice,
        WeeklyAmount = item.WeeklyAmount,
        LineTotal = item.LineTotal,
        Variant = item.Variant
    };

    private static string NewPublicToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static decimal CalculateWeeklyAmount(Tanda tanda, TandaParticipant participant)
    {
        var itemAmount = participant.Items
            .Where(item => item.WeeklyAmount.HasValue)
            .Sum(item => item.WeeklyAmount!.Value * item.Quantity);
        return itemAmount > 0 ? itemAmount : tanda.WeeklyAmount;
    }

    private static void ValidateItems(IEnumerable<CreateTandaParticipantItemDto> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProductName))
                throw new Exception("Cada artículo necesita un nombre.");
            if (item.Quantity < 1)
                throw new Exception("La cantidad de cada artículo debe ser mayor a cero.");
            if (item.UnitPrice < 0 || item.WeeklyAmount < 0)
                throw new Exception("Los importes de los artículos no pueden ser negativos.");
        }
    }

    private static List<TandaParticipantItem> BuildParticipantItems(
        IEnumerable<CreateTandaParticipantItemDto>? requestedItems,
        TandaProduct? defaultProduct,
        string? defaultVariant,
        decimal defaultWeeklyAmount)
    {
        var items = requestedItems?.ToList() ?? new List<CreateTandaParticipantItemDto>();
        if (items.Count == 0 && defaultProduct != null)
        {
            items.Add(new CreateTandaParticipantItemDto
            {
                ProductId = defaultProduct.Id,
                ProductName = defaultProduct.Name,
                UnitPrice = defaultProduct.BasePrice,
                WeeklyAmount = defaultWeeklyAmount,
                Variant = defaultVariant
            });
        }

        ValidateItems(items);
        return items.Select(item => new TandaParticipantItem
        {
            Id = Guid.NewGuid(),
            ProductId = item.ProductId,
            ProductName = item.ProductName.Trim(),
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            WeeklyAmount = item.WeeklyAmount,
            Variant = string.IsNullOrWhiteSpace(item.Variant) ? null : item.Variant.Trim()
        }).ToList();
    }

    private TandaProductDto MapToProductDto(TandaProduct pr) => new TandaProductDto
    {
        Id = pr.Id,
        Name = pr.Name,
        BasePrice = pr.BasePrice,
        IsActive = pr.IsActive,
        CreatedAt = pr.CreatedAt
    };

    private string AnonymizeName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Participante";
        if (parts.Length == 1) return parts[0];
        
        string firstName = parts[0];
        string lastInitial = parts[1].Substring(0, 1).ToUpper() + ".";
        return $"{firstName} {lastInitial}";
    }

    private int CalculateCurrentWeek(DateTime startDate)
    {
        var timeSpan = DateTime.UtcNow.Date - startDate.Date;
        int days = (int)timeSpan.TotalDays;
        
        if (days <= 0) return 1;
        // Restamos 1 día para que el cambio de semana ocurra el Lunes (día 8) 
        // y no el Domingo (día 7), permitiendo que la entrega dominical sea el cierre de la semana.
        return ((days - 1) / 7) + 1;
    }

    public async Task DeletePaymentAsync(Guid paymentId)
    {
        var payment = await _db.TandaPayments.FindAsync(paymentId);
        if (payment == null) throw new Exception("El registro de pago no existe.");

        _db.TandaPayments.Remove(payment);
        await _db.SaveChangesAsync();
    }

    public async Task ReorderParticipantsAsync(Guid tandaId, List<Guid> participantIdsInOrder)
    {
        var participants = await _db.TandaParticipants
            .Where(p => p.TandaId == tandaId)
            .ToListAsync();

        TandaTurnPlanner.ValidateExactOrder(
            participants.Select(p => p.Id).ToList(),
            participantIdsInOrder);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        foreach (var p in participants)
        {
            p.AssignedTurn += 1000;
        }
        await _db.SaveChangesAsync();

        // Ahora asignamos el orden final solicitado
        for (int i = 0; i < participantIdsInOrder.Count; i++)
        {
            var pId = participantIdsInOrder[i];
            var participant = participants.FirstOrDefault(p => p.Id == pId);
            if (participant != null)
            {
                participant.AssignedTurn = i + 1;
            }
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}

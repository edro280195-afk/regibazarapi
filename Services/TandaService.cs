using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public class TandaService(AppDbContext db) : ITandaService
{
    private static readonly HashSet<string> ValidTandaStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "Draft", "Active", "Completed", "Cancelled" };

    private static readonly HashSet<string> ValidParticipantStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "Active", "Delinquent", "Completed" };

    public async Task<TandaDto> CreateTandaAsync(
        CreateTandaDto dto,
        CancellationToken cancellationToken = default)
    {
        ValidateTandaValues(dto.Name, dto.TotalWeeks, dto.WeeklyAmount, dto.PenaltyAmount);

        var product = await db.TandaProducts
            .FirstOrDefaultAsync(p => p.Id == dto.ProductId && p.IsActive, cancellationToken);
        if (product is null)
            throw new InvalidOperationException("El producto especificado no existe o no está activo.");

        TandaTurnPlanner.ValidateCompleteAssignments(
            dto.TotalWeeks,
            dto.Participants.Select(p => p.AssignedTurn).ToList());

        if (dto.Participants.Any(p => p.WeeklyAmount is <= 0))
            throw new InvalidOperationException("El abono personalizado debe ser mayor a cero.");

        var clientIds = dto.Participants.Select(p => p.CustomerId).Distinct().ToList();
        var clients = await db.Clients
            .Where(c => clientIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        if (clientIds.Any(id => !clients.ContainsKey(id)))
            throw new InvalidOperationException("Una o más clientas seleccionadas ya no existen.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tanda = new Tanda
        {
            Id = Guid.NewGuid(),
            ProductId = dto.ProductId,
            Product = product,
            Name = dto.Name.Trim(),
            TotalWeeks = dto.TotalWeeks,
            WeeklyAmount = dto.WeeklyAmount,
            PenaltyAmount = dto.PenaltyAmount,
            StartDate = dto.StartDate.Date,
            Currency = string.IsNullOrWhiteSpace(dto.Currency) ? "MXN" : dto.Currency.Trim().ToUpperInvariant(),
            ItemCost = dto.ItemCost,
            ExchangeRate = dto.ExchangeRate,
            Status = "Active",
            AccessToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
        };

        foreach (var assignment in dto.Participants.OrderBy(p => p.AssignedTurn))
        {
            tanda.Participants.Add(new TandaParticipant
            {
                Id = Guid.NewGuid(),
                TandaId = tanda.Id,
                CustomerId = assignment.CustomerId,
                Client = clients[assignment.CustomerId],
                AssignedTurn = assignment.AssignedTurn,
                Status = "Active",
                Variant = CleanOptionalText(assignment.Variant),
                WeeklyAmount = assignment.WeeklyAmount,
                Currency = string.IsNullOrWhiteSpace(assignment.Currency) ? null : assignment.Currency.Trim().ToUpperInvariant(),
                ItemCost = assignment.ItemCost,
                ExchangeRate = assignment.ExchangeRate
            });
        }

        db.Tandas.Add(tanda);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return MapToTandaDto(tanda);
    }

    public async Task<TandaParticipantDto> AddParticipantAsync(
        AddParticipantDto dto,
        CancellationToken cancellationToken = default)
    {
        var tanda = await db.Tandas
            .Include(t => t.Participants)
            .FirstOrDefaultAsync(t => t.Id == dto.TandaId, cancellationToken)
            ?? throw new InvalidOperationException("La tanda especificada no existe.");

        ValidateTurn(dto.AssignedTurn, tanda.TotalWeeks);
        if (dto.WeeklyAmount is <= 0)
            throw new InvalidOperationException("El abono personalizado debe ser mayor a cero.");
        if (tanda.Participants.Count >= tanda.TotalWeeks)
            throw new InvalidOperationException("La tanda ya tiene ocupados todos sus lugares.");
        if (tanda.Participants.Any(p => p.AssignedTurn == dto.AssignedTurn))
            throw new InvalidOperationException($"El lugar {dto.AssignedTurn} ya está ocupado.");

        var client = await db.Clients.FindAsync([dto.CustomerId], cancellationToken)
            ?? throw new InvalidOperationException("La clienta seleccionada ya no existe.");

        var participant = new TandaParticipant
        {
            Id = Guid.NewGuid(),
            TandaId = dto.TandaId,
            Tanda = tanda,
            CustomerId = dto.CustomerId,
            Client = client,
            AssignedTurn = dto.AssignedTurn,
            Status = "Active",
            Variant = CleanOptionalText(dto.Variant),
            WeeklyAmount = dto.WeeklyAmount,
            Currency = string.IsNullOrWhiteSpace(dto.Currency) ? null : dto.Currency.Trim().ToUpperInvariant(),
            ItemCost = dto.ItemCost,
            ExchangeRate = dto.ExchangeRate
        };

        db.TandaParticipants.Add(participant);
        await db.SaveChangesAsync(cancellationToken);
        return MapToParticipantDto(participant, tanda);
    }

    public async Task<TandaParticipantDto> UpdateParticipantAsync(
        Guid participantId,
        UpdateTandaParticipantDto dto,
        CancellationToken cancellationToken = default)
    {
        var participant = await db.TandaParticipants
            .Include(p => p.Client)
            .Include(p => p.Payments)
            .Include(p => p.Tanda)
            .FirstOrDefaultAsync(p => p.Id == participantId, cancellationToken)
            ?? throw new InvalidOperationException("Participante no encontrado.");

        var tanda = participant.Tanda
            ?? throw new InvalidOperationException("La tanda del participante no existe.");
        ValidateTurn(dto.AssignedTurn, tanda.TotalWeeks);
        if (dto.WeeklyAmount is <= 0)
            throw new InvalidOperationException("El abono personalizado debe ser mayor a cero.");
        if (!ValidParticipantStatuses.Contains(dto.Status))
            throw new InvalidOperationException("El estado del participante no es válido.");

        var client = await db.Clients.FindAsync([dto.CustomerId], cancellationToken)
            ?? throw new InvalidOperationException("La clienta seleccionada ya no existe.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (dto.AssignedTurn != participant.AssignedTurn)
            await MoveParticipantToTurnAsync(participant, dto.AssignedTurn, cancellationToken);

        participant.CustomerId = dto.CustomerId;
        participant.Client = client;
        participant.Variant = CleanOptionalText(dto.Variant);
        participant.WeeklyAmount = dto.WeeklyAmount;
        participant.Currency = string.IsNullOrWhiteSpace(dto.Currency) ? null : dto.Currency.Trim().ToUpperInvariant();
        participant.ItemCost = dto.ItemCost;
        participant.ExchangeRate = dto.ExchangeRate;
        participant.Status = NormalizeParticipantStatus(dto.Status);
        participant.IsDelivered = dto.IsDelivered;
        participant.DeliveryDate = dto.IsDelivered
            ? (dto.DeliveryDate ?? DateTime.UtcNow).Date
            : null;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapToParticipantDto(participant, tanda);
    }

    public async Task<TandaPaymentDto> RegisterPaymentAsync(
        RegisterPaymentDto dto,
        CancellationToken cancellationToken = default)
    {
        var participant = await db.TandaParticipants
            .Include(p => p.Tanda)
            .FirstOrDefaultAsync(p => p.Id == dto.ParticipantId, cancellationToken)
            ?? throw new InvalidOperationException("Participante no encontrado.");

        ValidatePayment(dto.WeekNumber, dto.AmountPaid, dto.PenaltyPaid, participant.Tanda);

        var payment = new TandaPayment
        {
            Id = Guid.NewGuid(),
            ParticipantId = dto.ParticipantId,
            WeekNumber = dto.WeekNumber,
            AmountPaid = dto.AmountPaid,
            PenaltyPaid = dto.PenaltyPaid,
            PaymentDate = EnsureUtc(dto.PaymentDate ?? DateTime.UtcNow),
            IsVerified = dto.IsVerified,
            Notes = CleanOptionalText(dto.Notes)
        };

        db.TandaPayments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        return MapToPaymentDto(payment);
    }

    public async Task<TandaPaymentDto> UpdatePaymentAsync(
        Guid paymentId,
        UpdateTandaPaymentDto dto,
        CancellationToken cancellationToken = default)
    {
        var payment = await db.TandaPayments
            .Include(p => p.Participant)
                .ThenInclude(p => p!.Tanda)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw new InvalidOperationException("El registro de pago no existe.");

        ValidatePayment(dto.WeekNumber, dto.AmountPaid, dto.PenaltyPaid, payment.Participant?.Tanda);
        payment.WeekNumber = dto.WeekNumber;
        payment.AmountPaid = dto.AmountPaid;
        payment.PenaltyPaid = dto.PenaltyPaid;
        payment.PaymentDate = EnsureUtc(dto.PaymentDate);
        payment.IsVerified = dto.IsVerified;
        payment.Notes = CleanOptionalText(dto.Notes);

        await db.SaveChangesAsync(cancellationToken);
        return MapToPaymentDto(payment);
    }

    public async Task<TandaParticipantDto?> GetSundayDeliveryAsync(
        Guid tandaId,
        CancellationToken cancellationToken = default)
    {
        var tanda = await db.Tandas.FindAsync([tandaId], cancellationToken)
            ?? throw new InvalidOperationException("La tanda especificada no existe.");

        var currentWeek = CalculateCurrentWeek(tanda.StartDate);
        if (currentWeek < 1 || currentWeek > tanda.TotalWeeks)
            return null;

        var participant = await db.TandaParticipants
            .AsNoTracking()
            .Include(p => p.Client)
            .Include(p => p.Payments)
            .FirstOrDefaultAsync(
                p => p.TandaId == tandaId && p.AssignedTurn == currentWeek,
                cancellationToken);

        return participant is null ? null : MapToParticipantDto(participant, tanda);
    }

    public async Task UpdateParticipantTurnAsync(
        Guid participantId,
        int newTurn,
        CancellationToken cancellationToken = default)
    {
        var participant = await db.TandaParticipants
            .Include(p => p.Tanda)
            .FirstOrDefaultAsync(p => p.Id == participantId, cancellationToken)
            ?? throw new InvalidOperationException("Participante no encontrado.");

        ValidateTurn(newTurn, participant.Tanda?.TotalWeeks ?? 0);
        if (participant.AssignedTurn == newTurn)
            return;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await MoveParticipantToTurnAsync(participant, newTurn, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateParticipantVariantAsync(
        Guid participantId,
        string? variant,
        CancellationToken cancellationToken = default)
    {
        var participant = await db.TandaParticipants.FindAsync([participantId], cancellationToken)
            ?? throw new InvalidOperationException("Participante no encontrado.");
        participant.Variant = CleanOptionalText(variant);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ConfirmParticipantDeliveryAsync(
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        var participant = await db.TandaParticipants.FindAsync([participantId], cancellationToken)
            ?? throw new InvalidOperationException("Participante no encontrado.");
        participant.IsDelivered = true;
        participant.DeliveryDate = DateTime.UtcNow.Date;
        participant.Status = "Completed";
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveParticipantAsync(
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        var participant = await db.TandaParticipants
            .Include(p => p.Payments)
            .FirstOrDefaultAsync(p => p.Id == participantId, cancellationToken)
            ?? throw new InvalidOperationException("Participante no encontrado.");

        if (participant.Payments.Count > 0)
            db.TandaPayments.RemoveRange(participant.Payments);
        db.TandaParticipants.Remove(participant);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ProcessPenaltiesAsync(
        Guid tandaId,
        CancellationToken cancellationToken = default)
    {
        var tanda = await db.Tandas
            .Include(t => t.Participants)
                .ThenInclude(p => p.Payments)
            .FirstOrDefaultAsync(t => t.Id == tandaId, cancellationToken)
            ?? throw new InvalidOperationException("La tanda especificada no existe.");

        var currentWeek = CalculateCurrentWeek(tanda.StartDate);
        if (currentWeek < 1 || currentWeek > tanda.TotalWeeks)
            return;

        foreach (var participant in tanda.Participants.Where(p => !p.IsDelivered))
        {
            var weeklyAmount = participant.WeeklyAmount ?? tanda.WeeklyAmount;
            var amountPaid = participant.Payments
                .Where(p => p.WeekNumber == currentWeek && p.IsVerified)
                .Sum(p => p.AmountPaid);
            participant.Status = amountPaid >= weeklyAmount ? "Active" : "Delinquent";
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<TandaProductDto>> GetProductsAsync(
        CancellationToken cancellationToken = default)
    {
        var products = await db.TandaProducts
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
        return products.Select(MapToProductDto).ToList();
    }

    public async Task<TandaProductDto> CreateProductAsync(
        string name,
        decimal basePrice,
        CancellationToken cancellationToken = default)
    {
        var cleanName = name.Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new InvalidOperationException("El producto necesita un nombre.");
        if (basePrice < 0)
            throw new InvalidOperationException("El precio base no puede ser negativo.");

        var existing = await db.TandaProducts
            .FirstOrDefaultAsync(p => p.Name.ToLower() == cleanName.ToLower(), cancellationToken);
        if (existing is not null)
        {
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                await db.SaveChangesAsync(cancellationToken);
            }
            return MapToProductDto(existing);
        }

        var product = new TandaProduct
        {
            Id = Guid.NewGuid(),
            Name = cleanName,
            BasePrice = basePrice,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.TandaProducts.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return MapToProductDto(product);
    }

    public async Task<List<TandaDto>> GetTandasAsync(
        CancellationToken cancellationToken = default)
    {
        var tandas = await db.Tandas
            .AsNoTracking()
            .Include(t => t.Product)
            .Include(t => t.Participants).ThenInclude(p => p.Client)
            .Include(t => t.Participants).ThenInclude(p => p.Payments)
            .AsSplitQuery()
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        return tandas.Select(MapToTandaDto).ToList();
    }

    public async Task<TandaDto?> GetTandaByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var tanda = await db.Tandas
            .AsNoTracking()
            .Include(t => t.Product)
            .Include(t => t.Participants).ThenInclude(p => p.Client)
            .Include(t => t.Participants).ThenInclude(p => p.Payments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        return tanda is null ? null : MapToTandaDto(tanda);
    }

    public async Task<TandaViewDto?> GetTandaByTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var tanda = await db.Tandas
            .AsNoTracking()
            .Include(t => t.Product)
            .Include(t => t.Participants).ThenInclude(p => p.Client)
            .Include(t => t.Participants).ThenInclude(p => p.Payments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.AccessToken == token, cancellationToken);
        if (tanda is null)
            return null;

        var currentWeek = CalculateCurrentWeek(tanda.StartDate);
        return new TandaViewDto
        {
            Id = tanda.Id,
            Name = tanda.Name,
            ProductName = tanda.Product?.Name ?? "Producto Tanda",
            TotalWeeks = tanda.TotalWeeks,
            WeeklyAmount = tanda.WeeklyAmount,
            StartDate = tanda.StartDate,
            CurrentWeek = currentWeek,
            Participants = tanda.Participants.Select(p => new TandaParticipantViewDto
            {
                Id = p.Id,
                Name = AnonymizeName(p.Client?.Name ?? "Participante"),
                AssignedTurn = p.AssignedTurn,
                HasPaidCurrentWeek = p.Payments.Any(pay => pay.WeekNumber == currentWeek && pay.IsVerified),
                PaidWeeks = p.Payments.Where(pay => pay.IsVerified).Select(pay => pay.WeekNumber).Distinct().OrderBy(week => week).ToList(),
                IsWinnerThisWeek = p.AssignedTurn == currentWeek,
                IsDelivered = p.IsDelivered,
                Variant = p.Variant,
                WeeklyAmount = p.WeeklyAmount
            }).OrderBy(p => p.AssignedTurn).ToList()
        };
    }

    public async Task<TandaDto> UpdateTandaAsync(
        Guid id,
        UpdateTandaDto dto,
        CancellationToken cancellationToken = default)
    {
        ValidateTandaValues(dto.Name, dto.TotalWeeks, dto.WeeklyAmount, dto.PenaltyAmount);
        var tanda = await db.Tandas
            .Include(t => t.Product)
            .Include(t => t.Participants).ThenInclude(p => p.Client)
            .Include(t => t.Participants).ThenInclude(p => p.Payments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Tanda no encontrada.");

        if (dto.TotalWeeks < tanda.Participants.Count)
            throw new InvalidOperationException($"La tanda tiene {tanda.Participants.Count} participantes; no puede tener menos lugares.");

        TandaProduct? product = null;
        if (dto.ProductId.HasValue && dto.ProductId.Value != tanda.ProductId)
        {
            product = await db.TandaProducts.FirstOrDefaultAsync(
                p => p.Id == dto.ProductId.Value && p.IsActive,
                cancellationToken)
                ?? throw new InvalidOperationException("El producto seleccionado no existe o está inactivo.");
        }
        if (!string.IsNullOrWhiteSpace(dto.Status) && !ValidTandaStatuses.Contains(dto.Status))
            throw new InvalidOperationException("El estado de la tanda no es válido.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (dto.TotalWeeks < tanda.TotalWeeks)
            await RelocateOverflowParticipantsAsync(tanda, dto.TotalWeeks, cancellationToken);

        tanda.Name = dto.Name.Trim();
        tanda.TotalWeeks = dto.TotalWeeks;
        tanda.WeeklyAmount = dto.WeeklyAmount;
        tanda.PenaltyAmount = dto.PenaltyAmount;
        tanda.StartDate = dto.StartDate.Date;
        if (!string.IsNullOrWhiteSpace(dto.Currency))
            tanda.Currency = dto.Currency.Trim().ToUpperInvariant();
        tanda.ItemCost = dto.ItemCost;
        tanda.ExchangeRate = dto.ExchangeRate;
        if (product is not null)
        {
            tanda.ProductId = product.Id;
            tanda.Product = product;
        }
        if (!string.IsNullOrWhiteSpace(dto.Status))
            tanda.Status = NormalizeTandaStatus(dto.Status);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapToTandaDto(tanda);
    }

    public async Task UpdatePlacesAsync(
        Guid tandaId,
        IReadOnlyCollection<TandaPlaceAssignmentDto> assignments,
        CancellationToken cancellationToken = default)
    {
        var tanda = await db.Tandas.Include(t => t.Participants)
            .FirstOrDefaultAsync(t => t.Id == tandaId, cancellationToken)
            ?? throw new InvalidOperationException("Tanda no encontrada.");

        var plannedAssignments = assignments
            .Select(a => new TandaTurnPlanner.TandaPlaceAssignment(a.ParticipantId, a.AssignedTurn))
            .ToList();
        TandaTurnPlanner.ValidatePlaceAssignments(
            tanda.TotalWeeks,
            tanda.Participants.Select(p => p.Id).ToList(),
            plannedAssignments);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var participant in tanda.Participants)
            participant.AssignedTurn += 1000;
        await db.SaveChangesAsync(cancellationToken);

        var turnsByParticipant = assignments.ToDictionary(a => a.ParticipantId, a => a.AssignedTurn);
        foreach (var participant in tanda.Participants)
            participant.AssignedTurn = turnsByParticipant[participant.Id];
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeletePaymentAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        var payment = await db.TandaPayments.FindAsync([paymentId], cancellationToken)
            ?? throw new InvalidOperationException("El registro de pago no existe.");
        db.TandaPayments.Remove(payment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderParticipantsAsync(
        Guid tandaId,
        List<Guid> participantIdsInOrder,
        CancellationToken cancellationToken = default)
    {
        var assignments = participantIdsInOrder.Select((participantId, index) => new TandaPlaceAssignmentDto
        {
            ParticipantId = participantId,
            AssignedTurn = index + 1
        }).ToList();
        await UpdatePlacesAsync(tandaId, assignments, cancellationToken);
    }

    private async Task MoveParticipantToTurnAsync(TandaParticipant participant, int newTurn, CancellationToken cancellationToken)
    {
        var previousTurn = participant.AssignedTurn;
        var occupant = await db.TandaParticipants.FirstOrDefaultAsync(
            p => p.TandaId == participant.TandaId && p.AssignedTurn == newTurn && p.Id != participant.Id,
            cancellationToken);

        participant.AssignedTurn += 1000;
        await db.SaveChangesAsync(cancellationToken);
        if (occupant is not null)
        {
            occupant.AssignedTurn = previousTurn;
            await db.SaveChangesAsync(cancellationToken);
        }
        participant.AssignedTurn = newTurn;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RelocateOverflowParticipantsAsync(Tanda tanda, int newTotalWeeks, CancellationToken cancellationToken)
    {
        var participantsToMove = tanda.Participants.Where(p => p.AssignedTurn > newTotalWeeks)
            .OrderBy(p => p.AssignedTurn).ToList();
        if (participantsToMove.Count == 0)
            return;

        var occupiedTurns = tanda.Participants.Where(p => p.AssignedTurn <= newTotalWeeks)
            .Select(p => p.AssignedTurn).ToHashSet();
        var availableTurns = Enumerable.Range(1, newTotalWeeks)
            .Where(turn => !occupiedTurns.Contains(turn)).ToList();

        foreach (var participant in participantsToMove)
            participant.AssignedTurn += 1000;
        await db.SaveChangesAsync(cancellationToken);
        for (var index = 0; index < participantsToMove.Count; index++)
            participantsToMove[index].AssignedTurn = availableTurns[index];
        await db.SaveChangesAsync(cancellationToken);
    }

    private static TandaDto MapToTandaDto(Tanda tanda)
    {
        var participants = tanda.Participants?.OrderBy(p => p.AssignedTurn).ToList() ?? [];
        var expectedAmount = participants.Sum(p => (p.WeeklyAmount ?? tanda.WeeklyAmount) * tanda.TotalWeeks);
        var collectedAmount = participants.Sum(p => p.Payments.Where(payment => payment.IsVerified).Sum(payment => payment.AmountPaid));
        var paidInstallments = participants.Sum(p => CountPaidInstallments(p, tanda));
        var totalInstallments = participants.Count * tanda.TotalWeeks;

        return new TandaDto
        {
            Id = tanda.Id,
            ProductId = tanda.ProductId,
            Name = tanda.Name,
            TotalWeeks = tanda.TotalWeeks,
            WeeklyAmount = tanda.WeeklyAmount,
            PenaltyAmount = tanda.PenaltyAmount,
            StartDate = tanda.StartDate,
            Currency = tanda.Currency ?? "MXN",
            ItemCost = tanda.ItemCost,
            ExchangeRate = tanda.ExchangeRate,
            Status = tanda.Status,
            CreatedAt = tanda.CreatedAt,
            AccessToken = tanda.AccessToken,
            CurrentWeek = CalculateCurrentWeek(tanda.StartDate),
            ParticipantCount = participants.Count,
            AvailablePlaces = Math.Max(0, tanda.TotalWeeks - participants.Count),
            PaidInstallments = paidInstallments,
            TotalInstallments = totalInstallments,
            ExpectedAmount = expectedAmount,
            CollectedAmount = collectedAmount,
            BalanceDue = Math.Max(0, expectedAmount - collectedAmount),
            ProgressPercentage = expectedAmount <= 0 ? 0 : Math.Round(Math.Min(100, collectedAmount / expectedAmount * 100), 1),
            Product = tanda.Product is null ? null : MapToProductDto(tanda.Product),
            Participants = participants.Select(p => MapToParticipantDto(p, tanda)).ToList()
        };
    }

    private static TandaParticipantDto MapToParticipantDto(TandaParticipant participant, Tanda tanda)
    {
        var weeklyAmount = participant.WeeklyAmount ?? tanda.WeeklyAmount;
        var expectedAmount = weeklyAmount * tanda.TotalWeeks;
        var collectedAmount = participant.Payments.Where(payment => payment.IsVerified).Sum(payment => payment.AmountPaid);
        return new TandaParticipantDto
        {
            Id = participant.Id,
            TandaId = participant.TandaId,
            CustomerId = participant.CustomerId,
            CustomerName = participant.Client?.Name ?? participant.CustomerName,
            AssignedTurn = participant.AssignedTurn,
            Currency = participant.Currency,
            ItemCost = participant.ItemCost,
            ExchangeRate = participant.ExchangeRate,
            IsDelivered = participant.IsDelivered,
            DeliveryDate = participant.DeliveryDate,
            Status = participant.Status,
            Variant = participant.Variant,
            WeeklyAmount = participant.WeeklyAmount,
            ExpectedAmount = expectedAmount,
            CollectedAmount = collectedAmount,
            BalanceDue = Math.Max(0, expectedAmount - collectedAmount),
            PaidInstallments = CountPaidInstallments(participant, tanda),
            Payments = participant.Payments.OrderByDescending(p => p.PaymentDate).Select(MapToPaymentDto).ToList()
        };
    }

    private static int CountPaidInstallments(TandaParticipant participant, Tanda tanda)
    {
        var weeklyAmount = participant.WeeklyAmount ?? tanda.WeeklyAmount;
        return participant.Payments.Where(payment => payment.IsVerified)
            .GroupBy(payment => payment.WeekNumber)
            .Count(group => group.Sum(payment => payment.AmountPaid) >= weeklyAmount);
    }

    private static TandaPaymentDto MapToPaymentDto(TandaPayment payment) => new()
    {
        Id = payment.Id,
        ParticipantId = payment.ParticipantId,
        WeekNumber = payment.WeekNumber,
        AmountPaid = payment.AmountPaid,
        PenaltyPaid = payment.PenaltyPaid,
        PaymentDate = payment.PaymentDate,
        IsVerified = payment.IsVerified,
        Notes = payment.Notes
    };

    private static TandaProductDto MapToProductDto(TandaProduct product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        BasePrice = product.BasePrice,
        IsActive = product.IsActive,
        CreatedAt = product.CreatedAt
    };

    private static void ValidateTandaValues(string name, int totalWeeks, decimal weeklyAmount, decimal penaltyAmount)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("La tanda necesita un nombre.");
        if (totalWeeks is < 1 or > 52) throw new InvalidOperationException("La tanda debe tener entre 1 y 52 semanas.");
        if (weeklyAmount <= 0) throw new InvalidOperationException("El abono semanal debe ser mayor a cero.");
        if (penaltyAmount < 0) throw new InvalidOperationException("La penalización no puede ser negativa.");
    }

    private static void ValidateTurn(int turn, int totalWeeks)
    {
        if (turn < 1 || turn > totalWeeks)
            throw new InvalidOperationException($"El lugar debe estar entre 1 y {totalWeeks}.");
    }

    private static void ValidatePayment(int weekNumber, decimal amountPaid, decimal penaltyPaid, Tanda? tanda)
    {
        if (tanda is null) throw new InvalidOperationException("La tanda del participante no existe.");
        ValidateTurn(weekNumber, tanda.TotalWeeks);
        if (amountPaid <= 0) throw new InvalidOperationException("El monto pagado debe ser mayor a cero.");
        if (penaltyPaid < 0) throw new InvalidOperationException("La penalización pagada no puede ser negativa.");
    }

    private static string? CleanOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime EnsureUtc(DateTime dateTime) => dateTime.Kind switch
    {
        DateTimeKind.Utc => dateTime,
        DateTimeKind.Local => dateTime.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
    };

    private static string NormalizeTandaStatus(string status) =>
        ValidTandaStatuses.First(value => value.Equals(status, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeParticipantStatus(string status) =>
        ValidParticipantStatuses.First(value => value.Equals(status, StringComparison.OrdinalIgnoreCase));

    private static string AnonymizeName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Participante";
        if (parts.Length == 1) return parts[0];
        return $"{parts[0]} {char.ToUpperInvariant(parts[1][0])}.";
    }

    private static int CalculateCurrentWeek(DateTime startDate)
    {
        var days = (int)(DateTime.UtcNow.Date - startDate.Date).TotalDays;
        if (days < 0) return 0;
        if (days == 0) return 1;
        return ((days - 1) / 7) + 1;
    }
}

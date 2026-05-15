using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public interface IRaffleService
{
    Task<List<RaffleSummaryDto>> GetRafflesAsync(string? status = null);
    Task<RaffleDetailDto> GetRaffleByIdAsync(Guid id);
    Task<RaffleDetailDto> CreateRaffleAsync(CreateRaffleDto dto);
    Task<RaffleDetailDto> UpdateRaffleAsync(Guid id, UpdateRaffleDto dto);
    Task DeleteRaffleAsync(Guid id);
    Task<RaffleEvaluationResultDto> EvaluateRaffleAsync(Guid id);
    Task<List<RaffleDrawDto>> SelectWinnerAsync(Guid id, SelectWinnerDto dto);
    Task<RaffleDetailDto> AnnounceWinnerAsync(Guid id);
    Task<List<RaffleSummaryDto>> GetActiveRafflesAsync();
    Task<List<RaffleSummaryDto>> GetRaffleHistoryAsync();
    Task<TandaShuffleResultDto> ShuffleTandaTurnsAsync(Guid raffleId, SelectWinnerDto dto);
    Task<List<RaffleSummaryDto>> GetRafflesByTandaAsync(Guid tandaId);
}

public class RaffleService : IRaffleService
{
    private readonly AppDbContext _db;

    public RaffleService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<RaffleSummaryDto>> GetRafflesAsync(string? status = null)
    {
        var query = _db.Raffles
            .Include(r => r.Winner)
            .Include(r => r.Tanda)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);

        var raffles = await query
            .OrderByDescending(r => r.RaffleDate)
            .ToListAsync();

        return raffles.Select(MapToSummaryDto).ToList();
    }

    public async Task<RaffleDetailDto> GetRaffleByIdAsync(Guid id)
    {
        var raffle = await _db.Raffles
            .Include(r => r.Winner)
            .Include(r => r.Tanda)
            .Include(r => r.PrizeProduct)
            .Include(r => r.Participants)
                .ThenInclude(p => p.Client)
            .Include(r => r.Draws)
                .ThenInclude(d => d.Winner)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (raffle == null)
            throw new KeyNotFoundException("Sorteo no encontrado.");

        var entryCount = await _db.RaffleEntries.CountAsync(e => e.RaffleId == id);

        return MapToDetailDto(raffle, entryCount);
    }

    public async Task<RaffleDetailDto> CreateRaffleAsync(CreateRaffleDto dto)
    {
        var raffle = new Raffle
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Description = dto.Description,
            ImageUrl = dto.ImageUrl,
            SocialShareImageUrl = dto.SocialShareImageUrl,
            AnimationType = dto.AnimationType,
            PrizeType = dto.PrizeType,
            PrizeValue = dto.PrizeValue ?? 0,
            PrizeProductId = dto.PrizeProductId,
            PrizeDescription = dto.PrizeDescription,
            PrizeCurrency = dto.PrizeCurrency ?? "MXN",
            RequiredPurchases = dto.RequiredPurchases,
            EligibilityRule = dto.EligibilityRule,
            MinOrderTotal = dto.MinOrderTotal,
            MinLifetimeSpent = dto.MinLifetimeSpent,
            DateRangeStart = dto.DateRangeStart,
            DateRangeEnd = dto.DateRangeEnd,
            MaxEntriesPerClient = dto.MaxEntriesPerClient,
            ClientSegmentFilter = dto.ClientSegmentFilter,
            NewClientsOnly = dto.NewClientsOnly,
            FrequentClientsOnly = dto.FrequentClientsOnly,
            VipOnly = dto.VipOnly,
            ExcludeBlacklisted = dto.ExcludeBlacklisted,
            TandaId = dto.TandaId,
            ShuffleTandaTurns = dto.ShuffleTandaTurns,
            WinnerCount = dto.WinnerCount,
            RaffleDate = dto.RaffleDate,
            AutoDraw = dto.AutoDraw,
            NotifyWinner = dto.NotifyWinner,
            NotificationChannel = dto.NotificationChannel ?? "WhatsApp",
            WinnerMessageTemplate = dto.WinnerMessageTemplate,
            Status = "Draft",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Raffles.Add(raffle);
        await _db.SaveChangesAsync();

        return await GetRaffleByIdAsync(raffle.Id);
    }

    public async Task<RaffleDetailDto> UpdateRaffleAsync(Guid id, UpdateRaffleDto dto)
    {
        var raffle = await _db.Raffles
            .Include(r => r.Participants)
            .Include(r => r.Entries)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (raffle == null)
            throw new KeyNotFoundException("Sorteo no encontrado.");

        if (raffle.Status == "Completed" || raffle.Status == "Cancelled")
            throw new InvalidOperationException("No se puede modificar un sorteo completado o cancelado.");

        if (dto.Name != null) raffle.Name = dto.Name;
        if (dto.Description != null) raffle.Description = dto.Description;
        if (dto.ImageUrl != null) raffle.ImageUrl = dto.ImageUrl;
        if (dto.SocialShareImageUrl != null) raffle.SocialShareImageUrl = dto.SocialShareImageUrl;
        if (dto.AnimationType != null) raffle.AnimationType = dto.AnimationType;
        if (dto.PrizeType != null) raffle.PrizeType = dto.PrizeType;
        if (dto.PrizeValue.HasValue) raffle.PrizeValue = dto.PrizeValue.Value;
        if (dto.PrizeProductId.HasValue) raffle.PrizeProductId = dto.PrizeProductId.Value;
        if (dto.PrizeDescription != null) raffle.PrizeDescription = dto.PrizeDescription;
        if (dto.PrizeCurrency != null) raffle.PrizeCurrency = dto.PrizeCurrency;
        if (dto.RequiredPurchases.HasValue) raffle.RequiredPurchases = dto.RequiredPurchases.Value;
        if (dto.EligibilityRule != null) raffle.EligibilityRule = dto.EligibilityRule;
        if (dto.MinOrderTotal.HasValue) raffle.MinOrderTotal = dto.MinOrderTotal.Value;
        if (dto.MinLifetimeSpent.HasValue) raffle.MinLifetimeSpent = dto.MinLifetimeSpent.Value;
        if (dto.DateRangeStart.HasValue) raffle.DateRangeStart = dto.DateRangeStart.Value;
        if (dto.DateRangeEnd.HasValue) raffle.DateRangeEnd = dto.DateRangeEnd.Value;
        if (dto.MaxEntriesPerClient.HasValue) raffle.MaxEntriesPerClient = dto.MaxEntriesPerClient.Value;
        if (dto.ClientSegmentFilter != null) raffle.ClientSegmentFilter = dto.ClientSegmentFilter;
        if (dto.NewClientsOnly.HasValue) raffle.NewClientsOnly = dto.NewClientsOnly.Value;
        if (dto.FrequentClientsOnly.HasValue) raffle.FrequentClientsOnly = dto.FrequentClientsOnly.Value;
        if (dto.VipOnly.HasValue) raffle.VipOnly = dto.VipOnly.Value;
        if (dto.ExcludeBlacklisted.HasValue) raffle.ExcludeBlacklisted = dto.ExcludeBlacklisted.Value;
        if (dto.ExcludedClientIds != null) raffle.ExcludedClientIds = dto.ExcludedClientIds;
        if (dto.PreselectedWinnerIds != null) raffle.PreselectedWinnerIds = dto.PreselectedWinnerIds;
        if (dto.TandaId.HasValue) raffle.TandaId = dto.TandaId.Value;
        if (dto.ShuffleTandaTurns.HasValue) raffle.ShuffleTandaTurns = dto.ShuffleTandaTurns.Value;
        if (dto.WinnerCount.HasValue) raffle.WinnerCount = dto.WinnerCount.Value;
        if (dto.RaffleDate.HasValue) raffle.RaffleDate = dto.RaffleDate.Value;
        if (dto.AutoDraw.HasValue) raffle.AutoDraw = dto.AutoDraw.Value;
        if (dto.NotifyWinner.HasValue) raffle.NotifyWinner = dto.NotifyWinner.Value;
        if (dto.NotificationChannel != null) raffle.NotificationChannel = dto.NotificationChannel;
        if (dto.WinnerMessageTemplate != null) raffle.WinnerMessageTemplate = dto.WinnerMessageTemplate;
        if (dto.SocialTemplate != null) raffle.SocialTemplate = dto.SocialTemplate;
        if (dto.SocialBgColor != null) raffle.SocialBgColor = dto.SocialBgColor;
        if (dto.SocialTextColor != null) raffle.SocialTextColor = dto.SocialTextColor;
        if (dto.Status != null) raffle.Status = dto.Status;

        raffle.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await GetRaffleByIdAsync(raffle.Id);
    }

    public async Task DeleteRaffleAsync(Guid id)
    {
        var raffle = await _db.Raffles.FindAsync(id);
        if (raffle == null)
            throw new KeyNotFoundException("Sorteo no encontrado.");

        // Eliminar en cascada todo lo relacionado
        var participants = await _db.RaffleParticipants.Where(p => p.RaffleId == id).ToListAsync();
        var entries = await _db.RaffleEntries.Where(e => e.RaffleId == id).ToListAsync();
        var draws = await _db.RaffleDraws.Where(d => d.RaffleId == id).ToListAsync();

        if (draws.Count > 0) _db.RaffleDraws.RemoveRange(draws);
        if (entries.Count > 0) _db.RaffleEntries.RemoveRange(entries);
        if (participants.Count > 0) _db.RaffleParticipants.RemoveRange(participants);

        _db.Raffles.Remove(raffle);
        await _db.SaveChangesAsync();
    }

    public async Task<RaffleEvaluationResultDto> EvaluateRaffleAsync(Guid id)
    {
        var raffle = await _db.Raffles
            .Include(r => r.Participants)
            .Include(r => r.Entries)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (raffle == null)
            throw new KeyNotFoundException("Sorteo no encontrado.");

        var existingParticipantClientIds = raffle.Participants.Select(p => p.ClientId).ToHashSet();
        var existingEntryOrderIds = raffle.Entries.Select(e => e.OrderId).ToHashSet();
        var excludedIds = ParseExcludedClientIds(raffle.ExcludedClientIds);

        // Query base de órdenes entregadas
        var query = _db.Orders
            .Include(o => o.Client)
            .Where(o => o.Status == OrderStatus.Delivered);

        // Filtro de segmento
        query = ApplySegmentFilter(query, raffle);

        // Filtro de rango de fechas
        if (raffle.DateRangeStart.HasValue)
            query = query.Where(o => o.CreatedAt >= raffle.DateRangeStart.Value);

        if (raffle.DateRangeEnd.HasValue)
            query = query.Where(o => o.CreatedAt <= raffle.DateRangeEnd.Value);

        // Filtro de monto mínimo por orden
        if (raffle.MinOrderTotal.HasValue)
            query = query.Where(o => o.Total >= raffle.MinOrderTotal.Value);

        // Excluir clientas
        if (excludedIds.Count > 0)
            query = query.Where(o => !excludedIds.Contains(o.ClientId));

        var orders = await query.ToListAsync();

        // Calcular métricas por clienta
        var clientMetrics = orders
            .GroupBy(o => o.ClientId)
            .Select(g => (
                ClientId: g.Key,
                Client: g.First().Client,
                OrderCount: g.Count(),
                TotalSpent: g.Sum(o => o.Total),
                Orders: g.Where(o => !existingEntryOrderIds.Contains(o.Id)).ToList()
            ))
            .ToList();

        // Aplicar regla de elegibilidad
        var qualifiedClients = ApplyEligibilityRule(clientMetrics, raffle);

        // Aplicar filtro de lifetime spent
        if (raffle.MinLifetimeSpent.HasValue)
        {
            var clientIds = qualifiedClients.Select(c => c.ClientId).ToHashSet();
            var lifetimeSpent = await _db.Orders
                .Where(o => clientIds.Contains(o.ClientId) && o.Status == OrderStatus.Delivered)
                .GroupBy(o => o.ClientId)
                .Select(g => new { ClientId = g.Key, Total = g.Sum(o => o.Total) })
                .ToDictionaryAsync(x => x.ClientId, x => x.Total);

            qualifiedClients = qualifiedClients
                .Where(c => lifetimeSpent.GetValueOrDefault(c.ClientId, 0) >= raffle.MinLifetimeSpent.Value)
                .ToList();
        }

        var newEntries = new List<RaffleEntry>();
        var newParticipants = new List<RaffleParticipant>();

        foreach (var clientData in qualifiedClients)
        {
            if (!existingParticipantClientIds.Contains(clientData.ClientId))
            {
                var participant = new RaffleParticipant
                {
                    Id = Guid.NewGuid(),
                    RaffleId = id,
                    ClientId = clientData.ClientId,
                    QualificationDate = DateTime.UtcNow,
                    QualifyingOrders = clientData.OrderCount,
                    QualifyingTotalSpent = clientData.TotalSpent,
                    EntryCount = 1,
                    IsWinner = false,
                    Notified = false
                };

                newParticipants.Add(participant);
                existingParticipantClientIds.Add(clientData.ClientId);
            }

            foreach (var order in clientData.Orders)
            {
                var entry = new RaffleEntry
                {
                    Id = Guid.NewGuid(),
                    RaffleId = id,
                    ClientId = clientData.ClientId,
                    OrderId = order.Id,
                    EnteredAt = DateTime.UtcNow
                };

                newEntries.Add(entry);
            }
        }

        // Aplicar límite de entradas por clienta
        if (raffle.MaxEntriesPerClient.HasValue)
        {
            var maxEntries = raffle.MaxEntriesPerClient.Value;
            var clientEntryCounts = newEntries
                .GroupBy(e => e.ClientId)
                .ToDictionary(g => g.Key, g => g.Count());

            var entriesToRemove = newEntries
                .Where(e => clientEntryCounts[e.ClientId] > maxEntries)
                .GroupBy(e => e.ClientId)
                .SelectMany(g => g.Skip(Math.Max(0, g.Count() - maxEntries)))
                .ToList();

            foreach (var entry in entriesToRemove)
                newEntries.Remove(entry);
        }

        if (newParticipants.Count > 0)
            _db.RaffleParticipants.AddRange(newParticipants);

        if (newEntries.Count > 0)
            _db.RaffleEntries.AddRange(newEntries);

        await _db.SaveChangesAsync();

        var allParticipants = await _db.RaffleParticipants
            .Include(p => p.Client)
            .Where(p => p.RaffleId == id)
            .ToListAsync();

        var allEntries = await _db.RaffleEntries
            .Include(e => e.Client)
            .Include(e => e.Order)
            .Where(e => e.RaffleId == id)
            .ToListAsync();

        return new RaffleEvaluationResultDto
        {
            RaffleId = id,
            TotalQualified = allParticipants.Count,
            QualifiedParticipants = allParticipants.Select(MapToParticipantDto).ToList(),
            NewEntries = allEntries.Select(MapToEntryDto).ToList()
        };
    }

    public async Task<List<RaffleDrawDto>> SelectWinnerAsync(Guid id, SelectWinnerDto dto)
    {
        var raffle = await _db.Raffles
            .Include(r => r.Participants)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (raffle == null)
            throw new KeyNotFoundException("Sorteo no encontrado.");

        if (raffle.ShuffleTandaTurns)
            throw new InvalidOperationException("Este sorteo es para mezclar turnos de tanda. Usa el endpoint de shuffle.");

        if (raffle.Participants.Count == 0)
            throw new InvalidOperationException("No hay participantes calificados para este sorteo.");

        int countToSelect = dto.Count ?? raffle.WinnerCount;
        var alreadyWinners = raffle.Participants.Where(p => p.IsWinner).Select(p => p.ClientId).ToHashSet();
        var eligibleParticipants = raffle.Participants.Where(p => !p.IsWinner).ToList();

        if (eligibleParticipants.Count == 0 && countToSelect > 0)
            throw new InvalidOperationException("No hay participantes disponibles sin ganar. Todos ya fueron seleccionados.");

        if (countToSelect > eligibleParticipants.Count)
            countToSelect = eligibleParticipants.Count;

        var selectedClientIds = new List<int>();

        if (dto.SelectionMethod == "manual")
        {
            if (!dto.ManualWinnerClientId.HasValue)
                throw new InvalidOperationException("Se debe especificar un ganador para selección manual.");

            var isParticipant = eligibleParticipants.Any(p => p.ClientId == dto.ManualWinnerClientId.Value);
            if (!isParticipant)
                throw new InvalidOperationException("El cliente seleccionado no es participante calificado disponible.");

            selectedClientIds.Add(dto.ManualWinnerClientId.Value);
        }

        // Si hay preseleccionados, se agregan primero
        if (!string.IsNullOrWhiteSpace(raffle.PreselectedWinnerIds))
        {
            var preselectedIds = ParseExcludedClientIds(raffle.PreselectedWinnerIds);
            foreach (var manualId in preselectedIds)
            {
                if (selectedClientIds.Count >= countToSelect) break;

                var isParticipant = eligibleParticipants.Any(p => p.ClientId == manualId);
                if (isParticipant && !selectedClientIds.Contains(manualId))
                {
                    selectedClientIds.Add(manualId);
                }
            }
        }

        if (dto.SelectionMethod != "manual")
        {
            // Selección ponderada: clientas con más compras tienen más probabilidad
            var weightedList = new List<int>();
            foreach (var p in eligibleParticipants)
            {
                var weight = Math.Max(1, p.QualifyingOrders);
                for (int i = 0; i < weight; i++)
                    weightedList.Add(p.ClientId);
            }

            var random = new Random();
            while (selectedClientIds.Count < countToSelect && weightedList.Count > 0)
            {
                var idx = random.Next(weightedList.Count);
                var clientId = weightedList[idx];
                if (!selectedClientIds.Contains(clientId))
                    selectedClientIds.Add(clientId);
                
                // Remover todas las ocurrencias para no volver a seleccionarla
                weightedList.RemoveAll(id => id == clientId);
            }
        }

        var draws = new List<RaffleDrawDto>();

        foreach (var winnerClientId in selectedClientIds)
        {
            var winner = await _db.Clients.FindAsync(winnerClientId)
                ?? throw new KeyNotFoundException("Cliente ganador no encontrado.");

            var participant = raffle.Participants.FirstOrDefault(p => p.ClientId == winnerClientId);
            if (participant != null)
                participant.IsWinner = true;

            // Solo actualizamos el winner_id principal con el primer ganador
            if (raffle.WinnerId == null)
            {
                raffle.WinnerId = winnerClientId;
            }

            var draw = new RaffleDraw
            {
                Id = Guid.NewGuid(),
                RaffleId = id,
                DrawDate = DateTime.UtcNow,
                WinnerId = winnerClientId,
                SelectionMethod = dto.SelectionMethod,
                Notes = dto.Notes
            };

            _db.RaffleDraws.Add(draw);

            draws.Add(new RaffleDrawDto
            {
                Id = draw.Id,
                RaffleId = draw.RaffleId,
                DrawDate = draw.DrawDate,
                WinnerId = draw.WinnerId,
                Winner = new ClientDto(winner.Id, winner.Name, winner.Phone, winner.Address, winner.Tag.ToString(), 0, 0, winner.Type),
                SelectionMethod = draw.SelectionMethod,
                Notes = draw.Notes
            });
        }

        raffle.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return draws;
    }

    public async Task<TandaShuffleResultDto> ShuffleTandaTurnsAsync(Guid raffleId, SelectWinnerDto dto)
    {
        var raffle = await _db.Raffles
            .Include(r => r.Tanda)
                .ThenInclude(t => t!.Participants)
                    .ThenInclude(p => p.Client)
            .Include(r => r.Participants)
            .FirstOrDefaultAsync(r => r.Id == raffleId);

        if (raffle == null)
            throw new KeyNotFoundException("Sorteo no encontrado.");

        if (!raffle.TandaId.HasValue)
            throw new InvalidOperationException("Este sorteo no está vinculado a una tanda.");

        var tanda = raffle.Tanda;
        if (tanda == null)
            throw new KeyNotFoundException("Tanda no encontrada.");

        if (tanda.Participants.Count == 0)
            throw new InvalidOperationException("La tanda no tiene participantes.");

        // Obtener turnos actuales
        var currentAssignments = tanda.Participants
            .Where(p => p.Status == "Active")
            .Select(p => new TandaTurnAssignmentDto
            {
                ClientId = p.CustomerId,
                ClientName = p.Client != null ? p.Client.Name : "Desconocido",
                PreviousTurn = p.AssignedTurn,
                NewTurn = p.AssignedTurn
            })
            .ToList();

        if (currentAssignments.Count == 0)
            throw new InvalidOperationException("No hay participantes activos en la tanda.");

        // Mezclar turnos
        var random = new Random();
        var newTurns = Enumerable.Range(1, currentAssignments.Count)
            .OrderBy(x => random.Next())
            .ToList();

        // Asignar nuevos turnos
        for (int i = 0; i < currentAssignments.Count; i++)
        {
            currentAssignments[i].NewTurn = newTurns[i];
        }

        // Actualizar en la base de datos
        // Paso 1: Liberar turnos actuales con offset temporal para evitar colisiones en el índice único
        foreach (var p in tanda.Participants)
        {
            p.AssignedTurn += 1000;
        }
        await _db.SaveChangesAsync();

        // Paso 2: Asignar nuevos turnos
        foreach (var assignment in currentAssignments)
        {
            var participant = tanda.Participants.First(p => p.CustomerId == assignment.ClientId);
            participant.AssignedTurn = assignment.NewTurn;

            // Actualizar participante del sorteo si existe
            var raffleParticipant = raffle.Participants
                .FirstOrDefault(rp => rp.ClientId == assignment.ClientId);

            if (raffleParticipant != null)
            {
                raffleParticipant.AssignedTandaTurn = assignment.NewTurn;
                raffleParticipant.PreviousTandaTurn = assignment.PreviousTurn;
                raffleParticipant.IsWinner = true; // Todos "ganan" un nuevo turno
            }
        }

        // Registrar el sorteo
        var draw = new RaffleDraw
        {
            Id = Guid.NewGuid(),
            RaffleId = raffleId,
            DrawDate = DateTime.UtcNow,
            SelectionMethod = "tandaShuffle",
            IsTandaShuffle = true,
            TandaTurnsReshuffled = currentAssignments.Count,
            Notes = dto.Notes
        };

        _db.RaffleDraws.Add(draw);
        await _db.SaveChangesAsync();

        return new TandaShuffleResultDto
        {
            RaffleId = raffleId,
            TandaId = tanda.Id,
            TandaName = tanda.Name,
            ParticipantsShuffled = currentAssignments.Count,
            TurnAssignments = currentAssignments,
            ShuffleDate = DateTime.UtcNow
        };
    }

    public async Task<RaffleDetailDto> AnnounceWinnerAsync(Guid id)
    {
        var raffle = await _db.Raffles
            .Include(r => r.Winner)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (raffle == null)
            throw new KeyNotFoundException("Sorteo no encontrado.");

        if (raffle.WinnerId == null)
            throw new InvalidOperationException("No se ha seleccionado un ganador.");

        raffle.Status = "Completed";
        raffle.AnnouncedAt = DateTime.UtcNow;
        raffle.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return await GetRaffleByIdAsync(raffle.Id);
    }

    public async Task<List<RaffleSummaryDto>> GetActiveRafflesAsync()
    {
        var raffles = await _db.Raffles
            .Include(r => r.Winner)
            .Include(r => r.Tanda)
            .Where(r => r.Status == "Active" || r.Status == "Draft")
            .OrderBy(r => r.RaffleDate)
            .ToListAsync();

        return raffles.Select(MapToSummaryDto).ToList();
    }

    public async Task<List<RaffleSummaryDto>> GetRaffleHistoryAsync()
    {
        var raffles = await _db.Raffles
            .Include(r => r.Winner)
            .Include(r => r.Tanda)
            .Where(r => r.Status == "Completed" || r.Status == "Cancelled")
            .OrderByDescending(r => r.RaffleDate)
            .ToListAsync();

        return raffles.Select(MapToSummaryDto).ToList();
    }

    public async Task<List<RaffleSummaryDto>> GetRafflesByTandaAsync(Guid tandaId)
    {
        var raffles = await _db.Raffles
            .Include(r => r.Winner)
            .Include(r => r.Tanda)
            .Where(r => r.TandaId == tandaId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return raffles.Select(MapToSummaryDto).ToList();
    }

    // ── Métodos privados ──

    private IQueryable<Order> ApplySegmentFilter(IQueryable<Order> query, Raffle raffle)
    {
        return raffle.ClientSegmentFilter?.ToLower() switch
        {
            "new" => query.Where(o => o.Client.Type == "Nueva"),
            "frequent" => query.Where(o => o.Client.Type == "Frecuente"),
            "newandfrequent" => query.Where(o => o.Client.Type == "Nueva" || o.Client.Type == "Frecuente"),
            "vip" => query.Where(o => o.Client.Tag == ClientTag.Vip),
            "blacklist" => query.Where(o => o.Client.Tag == ClientTag.Blacklist),
            _ => query
        };
    }

    private List<(int ClientId, Models.Client Client, int OrderCount, decimal TotalSpent, List<Order> Orders)> ApplyEligibilityRule(
        List<(int ClientId, Models.Client Client, int OrderCount, decimal TotalSpent, List<Order> Orders)> metrics,
        Raffle raffle)
    {
        return raffle.EligibilityRule.ToLower() switch
        {
            "purchasecount" => metrics.Where(m => m.OrderCount >= raffle.RequiredPurchases).ToList(),
            "minspent" => metrics.Where(m => m.TotalSpent >= (raffle.MinOrderTotal ?? 0)).ToList(),
            "recentactivity" => metrics.Where(m => m.OrderCount >= 1).ToList(),
            _ => metrics.Where(m => m.OrderCount >= raffle.RequiredPurchases).ToList()
        };
    }

    private HashSet<int> ParseExcludedClientIds(string? excludedIds)
    {
        if (string.IsNullOrWhiteSpace(excludedIds))
            return new HashSet<int>();

        return excludedIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    // ── Mappers ──

    private RaffleDto MapToDto(Raffle raffle)
    {
        return new RaffleDto
        {
            Id = raffle.Id,
            Name = raffle.Name,
            Description = raffle.Description,
            ImageUrl = raffle.ImageUrl,
            SocialShareImageUrl = raffle.SocialShareImageUrl,
            AnimationType = raffle.AnimationType,
            PrizeType = raffle.PrizeType,
            PrizeValue = raffle.PrizeValue,
            PrizeProductId = raffle.PrizeProductId,
            PrizeProduct = raffle.PrizeProduct != null
                ? new TandaProductDto { Id = raffle.PrizeProduct.Id, Name = raffle.PrizeProduct.Name, BasePrice = raffle.PrizeProduct.BasePrice }
                : null,
            PrizeDescription = raffle.PrizeDescription,
            PrizeCurrency = raffle.PrizeCurrency,
            RequiredPurchases = raffle.RequiredPurchases,
            EligibilityRule = raffle.EligibilityRule,
            MinOrderTotal = raffle.MinOrderTotal,
            MinLifetimeSpent = raffle.MinLifetimeSpent,
            DateRangeStart = raffle.DateRangeStart,
            DateRangeEnd = raffle.DateRangeEnd,
            MaxEntriesPerClient = raffle.MaxEntriesPerClient,
            ClientSegmentFilter = raffle.ClientSegmentFilter,
            NewClientsOnly = raffle.NewClientsOnly,
            FrequentClientsOnly = raffle.FrequentClientsOnly,
            VipOnly = raffle.VipOnly,
            ExcludeBlacklisted = raffle.ExcludeBlacklisted,
            ExcludedClientIds = raffle.ExcludedClientIds,
            PreselectedWinnerIds = raffle.PreselectedWinnerIds,
            TandaId = raffle.TandaId,
            TandaName = raffle.Tanda?.Name,
            ShuffleTandaTurns = raffle.ShuffleTandaTurns,
            WinnerCount = raffle.WinnerCount,
            RaffleDate = raffle.RaffleDate,
            AutoDraw = raffle.AutoDraw,
            NotifyWinner = raffle.NotifyWinner,
            NotificationChannel = raffle.NotificationChannel,
            WinnerMessageTemplate = raffle.WinnerMessageTemplate,
            SocialTemplate = raffle.SocialTemplate,
            SocialBgColor = raffle.SocialBgColor,
            SocialTextColor = raffle.SocialTextColor,
            Status = raffle.Status,
            WinnerId = raffle.WinnerId,
            Winner = raffle.Winner != null
                ? new ClientDto(raffle.Winner.Id, raffle.Winner.Name, raffle.Winner.Phone, raffle.Winner.Address, raffle.Winner.Tag.ToString(), 0, 0, raffle.Winner.Type)
                : null,
            AnnouncedAt = raffle.AnnouncedAt,
            CreatedAt = raffle.CreatedAt,
            UpdatedAt = raffle.UpdatedAt,
            ParticipantCount = raffle.Participants?.Count ?? 0,
            EntryCount = raffle.Entries?.Count ?? 0
        };
    }

    private RaffleDetailDto MapToDetailDto(Raffle raffle, int? entryCount = null)
    {
        var dto = MapToDto(raffle);
        return new RaffleDetailDto
        {
            Id = dto.Id,
            Name = dto.Name,
            Description = dto.Description,
            ImageUrl = dto.ImageUrl,
            SocialShareImageUrl = dto.SocialShareImageUrl,
            AnimationType = dto.AnimationType,
            PrizeType = dto.PrizeType,
            PrizeValue = dto.PrizeValue,
            PrizeProductId = dto.PrizeProductId,
            PrizeProduct = dto.PrizeProduct,
            PrizeDescription = dto.PrizeDescription,
            PrizeCurrency = dto.PrizeCurrency,
            RequiredPurchases = dto.RequiredPurchases,
            EligibilityRule = dto.EligibilityRule,
            MinOrderTotal = dto.MinOrderTotal,
            MinLifetimeSpent = dto.MinLifetimeSpent,
            DateRangeStart = dto.DateRangeStart,
            DateRangeEnd = dto.DateRangeEnd,
            MaxEntriesPerClient = dto.MaxEntriesPerClient,
            ClientSegmentFilter = dto.ClientSegmentFilter,
            NewClientsOnly = dto.NewClientsOnly,
            FrequentClientsOnly = dto.FrequentClientsOnly,
            VipOnly = dto.VipOnly,
            ExcludeBlacklisted = dto.ExcludeBlacklisted,
            ExcludedClientIds = dto.ExcludedClientIds,
            PreselectedWinnerIds = dto.PreselectedWinnerIds,
            TandaId = dto.TandaId,
            TandaName = dto.TandaName,
            ShuffleTandaTurns = dto.ShuffleTandaTurns,
            WinnerCount = dto.WinnerCount,
            RaffleDate = dto.RaffleDate,
            AutoDraw = dto.AutoDraw,
            NotifyWinner = dto.NotifyWinner,
            NotificationChannel = dto.NotificationChannel,
            WinnerMessageTemplate = dto.WinnerMessageTemplate,
            SocialTemplate = dto.SocialTemplate,
            SocialBgColor = dto.SocialBgColor,
            SocialTextColor = dto.SocialTextColor,
            Status = dto.Status,
            WinnerId = dto.WinnerId,
            Winner = dto.Winner,
            AnnouncedAt = dto.AnnouncedAt,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt,
            ParticipantCount = dto.ParticipantCount,
            EntryCount = entryCount ?? dto.EntryCount,
            Participants = raffle.Participants?.Select(MapToParticipantDto).ToList() ?? new(),
            Entries = new List<RaffleEntryDto>(),
            Draws = raffle.Draws?.Select(MapToDrawDto).ToList() ?? new()
        };
    }

    private RaffleSummaryDto MapToSummaryDto(Raffle raffle)
    {
        return new RaffleSummaryDto
        {
            Id = raffle.Id,
            Name = raffle.Name,
            ImageUrl = raffle.ImageUrl,
            PrizeType = raffle.PrizeType,
            PrizeValue = raffle.PrizeValue,
            PrizeDescription = raffle.PrizeDescription,
            RaffleDate = raffle.RaffleDate,
            Status = raffle.Status,
            AnimationType = raffle.AnimationType,
            TandaId = raffle.TandaId,
            TandaName = raffle.Tanda?.Name,
            ShuffleTandaTurns = raffle.ShuffleTandaTurns,
            ParticipantCount = raffle.Participants?.Count ?? 0,
            WinnerCount = raffle.WinnerCount,
            WinnerId = raffle.WinnerId,
            WinnerName = raffle.Winner?.Name,
            WinnerNames = raffle.Participants?.Where(p => p.IsWinner).Select(p => p.Client?.Name).Where(n => n != null).ToList(),
            AnnouncedAt = raffle.AnnouncedAt
        };
    }

    private RaffleParticipantDto MapToParticipantDto(RaffleParticipant p)
    {
        return new RaffleParticipantDto
        {
            Id = p.Id,
            RaffleId = p.RaffleId,
            ClientId = p.ClientId,
            Client = new ClientDto(p.Client.Id, p.Client.Name, p.Client.Phone, p.Client.Address, p.Client.Tag.ToString(), 0, 0, p.Client.Type),
            QualificationDate = p.QualificationDate,
            QualifyingOrders = p.QualifyingOrders,
            QualifyingTotalSpent = p.QualifyingTotalSpent,
            EntryCount = p.EntryCount,
            IsWinner = p.IsWinner,
            AssignedTandaTurn = p.AssignedTandaTurn,
            PreviousTandaTurn = p.PreviousTandaTurn,
            Notified = p.Notified,
            NotifiedAt = p.NotifiedAt,
            NotificationChannelUsed = p.NotificationChannelUsed
        };
    }

    private RaffleEntryDto MapToEntryDto(RaffleEntry e)
    {
        return new RaffleEntryDto
        {
            Id = e.Id,
            RaffleId = e.RaffleId,
            ClientId = e.ClientId,
            Client = new ClientDto(e.Client.Id, e.Client.Name, e.Client.Phone, e.Client.Address, e.Client.Tag.ToString(), 0, 0, e.Client.Type),
            OrderId = e.OrderId,
            Order = new OrderSummaryDto(
                e.Order.Id,
                e.Order.Client.Name,
                e.Order.Status.ToString(),
                e.Order.Total,
                $"https://regibazar.com/pedido/{e.Order.AccessToken}",
                e.Order.Items.Count,
                e.Order.OrderType.ToString(),
                e.Order.CreatedAt,
                e.Order.OrderType.ToString(),
                e.Order.Client.Phone,
                e.Order.Client.Address,
                e.Order.PostponedAt,
                e.Order.PostponedNote,
                e.Order.Subtotal,
                e.Order.ShippingCost,
                e.Order.AccessToken,
                e.Order.ExpiresAt,
                e.Order.Items.Select(i => new OrderItemDto(i.Id, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal)).ToList()
            ),
            EnteredAt = e.EnteredAt
        };
    }

    private RaffleDrawDto MapToDrawDto(RaffleDraw d)
    {
        return new RaffleDrawDto
        {
            Id = d.Id,
            RaffleId = d.RaffleId,
            DrawDate = d.DrawDate,
            WinnerId = d.WinnerId,
            Winner = d.Winner != null
                ? new ClientDto(d.Winner.Id, d.Winner.Name, d.Winner.Phone, d.Winner.Address, d.Winner.Tag.ToString(), 0, 0, d.Winner.Type)
                : null,
            SelectionMethod = d.SelectionMethod,
            IsTandaShuffle = d.IsTandaShuffle,
            TandaTurnsReshuffled = d.TandaTurnsReshuffled,
            Notes = d.Notes
        };
    }
}

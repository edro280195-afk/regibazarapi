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

        var tanda = new Tanda
        {
            ProductId = dto.ProductId,
            Name = dto.Name,
            TotalWeeks = dto.TotalWeeks,
            WeeklyAmount = dto.WeeklyAmount,
            PenaltyAmount = dto.PenaltyAmount,
            StartDate = dto.StartDate,
            Status = "Active",
            AccessToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
        };

        _db.Tandas.Add(tanda);
        await _db.SaveChangesAsync();

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
            TandaId = dto.TandaId,
            CustomerId = dto.CustomerId,
            AssignedTurn = dto.AssignedTurn,
            Status = "Active",
            Variant = dto.Variant
        };

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
            bool hasPaidCurrentWeek = participant.Payments.Any(p => p.WeekNumber == currentWeek);
            
            if (!hasPaidCurrentWeek)
            {
                participant.Status = "Delinquent";
            }
        }

        await _db.SaveChangesAsync();
    }

    public async Task ShuffleParticipantsAsync(Guid tandaId)
    {
        var participants = await _db.TandaParticipants
            .Where(p => p.TandaId == tandaId)
            .ToListAsync();

        if (!participants.Any()) return;

        var random = new Random();
        var shuffledIndices = Enumerable.Range(1, participants.Count).OrderBy(x => random.Next()).ToList();

        for (int i = 0; i < participants.Count; i++)
        {
            participants[i].AssignedTurn = shuffledIndices[i];
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
            .FirstOrDefaultAsync(t => t.Id == id);

        return tanda != null ? MapToTandaDto(tanda) : null;
    }

    public async Task<TandaViewDto?> GetTandaByTokenAsync(string token)
    {
        var tanda = await _db.Tandas
            .Include(t => t.Product)
            .Include(t => t.Participants)
                .ThenInclude(p => p.Client)
            .Include(t => t.Participants)
                .ThenInclude(p => p.Payments)
            .FirstOrDefaultAsync(t => t.AccessToken == token);

        if (tanda == null) return null;

        int currentWeek = CalculateCurrentWeek(tanda.StartDate);

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
                Name = AnonymizeName(p.Client?.Name ?? "Participante"),
                AssignedTurn = p.AssignedTurn,
                HasPaidCurrentWeek = p.Payments.Any(pay => pay.WeekNumber == currentWeek),
                PaidWeeks = p.Payments.Select(pay => pay.WeekNumber).ToList(),
                IsWinnerThisWeek = p.AssignedTurn == currentWeek,
                IsDelivered = p.IsDelivered,
                Variant = p.Variant
            }).OrderBy(p => p.AssignedTurn).ToList()
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
        AssignedTurn = p.AssignedTurn,
        IsDelivered = p.IsDelivered,
        DeliveryDate = p.DeliveryDate,
        Status = p.Status,
        Variant = p.Variant,
        Payments = p.Payments?.Select(MapToPaymentDto).ToList()
    };

    private TandaPaymentDto MapToPaymentDto(TandaPayment pay) => new TandaPaymentDto
    {
        Id = pay.Id,
        ParticipantId = pay.ParticipantId,
        WeekNumber = pay.WeekNumber,
        AmountPaid = pay.AmountPaid,
        PenaltyPaid = pay.PenaltyPaid,
        PaymentDate = pay.PaymentDate,
        IsVerified = pay.IsVerified,
        Notes = pay.Notes
    };

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
}

using EntregasApi.DTOs;
using EntregasApi.Models;

namespace EntregasApi.Services;

public interface ITandaService
{
    Task<TandaDto> CreateTandaAsync(CreateTandaDto dto);
    Task<TandaParticipantDto> AddParticipantAsync(AddParticipantDto dto);
    Task<TandaPaymentDto> RegisterPaymentAsync(RegisterPaymentDto dto);
    Task<TandaParticipantDto?> GetSundayDeliveryAsync(Guid tandaId);
    Task UpdateParticipantTurnAsync(Guid participantId, int newTurn);
    Task UpdateParticipantVariantAsync(Guid participantId, string? variant);
    Task ConfirmParticipantDeliveryAsync(Guid participantId);
    Task RemoveParticipantAsync(Guid participantId);
    Task ProcessPenaltiesAsync(Guid tandaId);
    Task ShuffleParticipantsAsync(Guid tandaId);
    Task<TandaDto> UpdateTandaAsync(Guid id, UpdateTandaDto dto);
    
    // Catalogo de productos
    Task<List<TandaProductDto>> GetProductsAsync();
    Task<TandaProductDto> CreateProductAsync(string name, decimal basePrice);

    // Consultas de Tandas
    Task<List<TandaDto>> GetTandasAsync();
    Task<TandaDto?> GetTandaByIdAsync(Guid id);
    Task<TandaViewDto?> GetTandaByTokenAsync(string token);
}

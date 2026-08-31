using EntregasApi.DTOs;
using EntregasApi.Models;

namespace EntregasApi.Services;

public interface ITandaService
{
    Task<TandaDto> CreateTandaAsync(CreateTandaDto dto, CancellationToken cancellationToken = default);
    Task<TandaParticipantDto> AddParticipantAsync(AddParticipantDto dto, CancellationToken cancellationToken = default);
    Task<TandaParticipantDto> UpdateParticipantAsync(Guid participantId, UpdateTandaParticipantDto dto, CancellationToken cancellationToken = default);
    Task<TandaPaymentDto> RegisterPaymentAsync(RegisterPaymentDto dto, CancellationToken cancellationToken = default);
    Task<TandaPaymentDto> UpdatePaymentAsync(Guid paymentId, UpdateTandaPaymentDto dto, CancellationToken cancellationToken = default);
    Task<TandaParticipantDto?> GetSundayDeliveryAsync(Guid tandaId, CancellationToken cancellationToken = default);
    Task UpdateParticipantTurnAsync(Guid participantId, int newTurn, CancellationToken cancellationToken = default);
    Task UpdateParticipantVariantAsync(Guid participantId, string? variant, CancellationToken cancellationToken = default);
    Task ConfirmParticipantDeliveryAsync(Guid participantId, CancellationToken cancellationToken = default);
    Task RemoveParticipantAsync(Guid participantId, CancellationToken cancellationToken = default);
    Task ProcessPenaltiesAsync(Guid tandaId, CancellationToken cancellationToken = default);
    Task<TandaDto> UpdateTandaAsync(Guid id, UpdateTandaDto dto, CancellationToken cancellationToken = default);
    Task UpdatePlacesAsync(Guid tandaId, IReadOnlyCollection<TandaPlaceAssignmentDto> assignments, CancellationToken cancellationToken = default);
    
    // Catalogo de productos
    Task<List<TandaProductDto>> GetProductsAsync(CancellationToken cancellationToken = default);
    Task<TandaProductDto> CreateProductAsync(string name, decimal basePrice, CancellationToken cancellationToken = default);

    // Consultas de Tandas
    Task<List<TandaDto>> GetTandasAsync(CancellationToken cancellationToken = default);
    Task<TandaDto?> GetTandaByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TandaViewDto?> GetTandaByTokenAsync(string token, CancellationToken cancellationToken = default);
    Task DeletePaymentAsync(Guid paymentId, CancellationToken cancellationToken = default);
    Task ReorderParticipantsAsync(Guid tandaId, List<Guid> participantIdsInOrder, CancellationToken cancellationToken = default);
    Task<List<TandaPaymentProofAdminDto>> GetPaymentProofsAsync(Guid tandaId, string? status = null, CancellationToken cancellationToken = default);
    Task<TandaPaymentProofAdminDto> ReviewPaymentProofAsync(Guid proofId, ReviewTandaPaymentProofDto dto, string reviewer, CancellationToken cancellationToken = default);
    Task<TandaPaymentProofUploadResultDto> UploadPaymentProofAsync(string participantToken, int weekNumber, decimal amountClaimed, Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken = default);
}

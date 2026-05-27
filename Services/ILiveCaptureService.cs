using EntregasApi.DTOs;
using EntregasApi.Models;

namespace EntregasApi.Services;

public interface ILiveCaptureService
{
    Task<LiveSession> ImportAsync(string facebookUrl, string? title);
    Task<List<LiveSession>> GetAllAsync();
    Task<LiveSession?> GetByIdAsync(int id);
    Task<LiveReviewDto?> GetReviewAsync(int sessionId);
    Task ConfirmCandidateAsync(int candidateId, ConfirmCandidateRequest req);
    Task IgnoreCandidateAsync(int candidateId);
}

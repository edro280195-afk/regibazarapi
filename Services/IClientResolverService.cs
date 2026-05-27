using EntregasApi.DTOs;

namespace EntregasApi.Services;

public interface IClientResolverService
{
    /// <summary>
    /// Identifica candidatas a clienta dada una combinación de nombre/teléfono/dirección.
    /// Usa exact match en alias normalizados, exact match en teléfono normalizado y
    /// trigram similarity (pg_trgm) en nombre/alias. Devuelve top-3 con acción sugerida.
    /// </summary>
    Task<ResolveClientResponse> ResolveAsync(string name, string? phone, string? address);

    /// <summary>
    /// Agrega (o incrementa TimesSeen si ya existe) un alias a una clienta.
    /// </summary>
    Task<ClientAliasDto> AddAliasAsync(int clientId, string alias, EntregasApi.Models.ClientAliasSource source);

    /// <summary>
    /// Mergea sourceId dentro de targetId: reasigna órdenes, mueve aliases, agrega
    /// el nombre del source como alias del target, recalcula stats, elimina el source.
    /// </summary>
    Task MergeAsync(int sourceId, int targetId);

    /// <summary>
    /// Sugerencias de duplicados: pares por teléfono igual o nombre/dirección parecidos.
    /// </summary>
    Task<List<DuplicateSuggestionDto>> GetDuplicateSuggestionsAsync(int limit = 50);
}

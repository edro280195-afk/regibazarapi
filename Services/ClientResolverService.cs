using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EntregasApi.Services;

public class ClientResolverService : IClientResolverService
{
    private readonly AppDbContext _db;

    private const double TrigramThreshold = 0.5;
    private const double UseThreshold = 0.85;
    private const double UseMarginOverNext = 0.20;
    private const double CreateMaxThreshold = 0.50;
    private const int TopN = 3;

    public ClientResolverService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ResolveClientResponse> ResolveAsync(string name, string? phone, string? address)
    {
        var normalizedName = TextNormalizer.NormalizeName(name);
        var normalizedPhone = TextNormalizer.NormalizePhone(phone);
        var normalizedAddress = TextNormalizer.NormalizeAddress(address);

        if (string.IsNullOrEmpty(normalizedName) && normalizedPhone == null && normalizedAddress == null)
        {
            return new ResolveClientResponse(new List<ResolveCandidateDto>(), "create");
        }

        // Diccionario de score por ClientId, conservando la mejor señal y razón de match
        var scoreMap = new Dictionary<int, (double Score, string MatchedBy)>();

        // 1. Match exacto en alias (score 1.00)
        if (!string.IsNullOrEmpty(normalizedName))
        {
            var aliasMatches = await _db.ClientAliases
                .Where(a => a.NormalizedAlias == normalizedName)
                .Select(a => a.ClientId)
                .Distinct()
                .ToListAsync();

            foreach (var clientId in aliasMatches)
            {
                Upsert(scoreMap, clientId, 1.00, "alias");
            }
        }

        // 2. Match exacto en teléfono normalizado (score 0.95)
        if (normalizedPhone != null)
        {
            var phoneMatches = await _db.Clients
                .Where(c => c.NormalizedPhone == normalizedPhone)
                .Select(c => c.Id)
                .ToListAsync();

            foreach (var clientId in phoneMatches)
            {
                Upsert(scoreMap, clientId, 0.95, "phone");
            }
        }

        // 3. Trigram similarity sobre Clients.NormalizedName y ClientAliases.NormalizedAlias.
        //    Filtramos con TrigramsAreSimilar (operador % de pg_trgm) que SÍ usa el índice GIN,
        //    y después calculamos el score con TrigramsSimilarity para ranking.
        if (!string.IsNullOrEmpty(normalizedName))
        {
            var nameMatches = await _db.Clients
                .Where(c => EF.Functions.TrigramsAreSimilar(c.NormalizedName, normalizedName))
                .Select(c => new
                {
                    c.Id,
                    Sim = EF.Functions.TrigramsSimilarity(c.NormalizedName, normalizedName)
                })
                .OrderByDescending(x => x.Sim)
                .Take(20)
                .ToListAsync();

            foreach (var m in nameMatches.Where(x => x.Sim >= TrigramThreshold))
            {
                Upsert(scoreMap, m.Id, m.Sim, "name-fuzzy");
            }

            var aliasFuzzyMatches = await _db.ClientAliases
                .Where(a => EF.Functions.TrigramsAreSimilar(a.NormalizedAlias, normalizedName))
                .Select(a => new
                {
                    a.ClientId,
                    Sim = EF.Functions.TrigramsSimilarity(a.NormalizedAlias, normalizedName)
                })
                .OrderByDescending(x => x.Sim)
                .Take(20)
                .ToListAsync();

            foreach (var m in aliasFuzzyMatches.Where(x => x.Sim >= TrigramThreshold))
            {
                Upsert(scoreMap, m.ClientId, m.Sim, "alias-fuzzy");
            }
        }

        // 4. Boost +0.10 si dirección normalizada coincide por trigram con alguna candidata
        if (!string.IsNullOrEmpty(normalizedAddress) && scoreMap.Count > 0)
        {
            var candidateIds = scoreMap.Keys.ToList();
            var addressMatches = await _db.Clients
                .Where(c => candidateIds.Contains(c.Id)
                            && c.NormalizedAddress != null
                            && EF.Functions.TrigramsAreSimilar(c.NormalizedAddress, normalizedAddress))
                .Select(c => new
                {
                    c.Id,
                    Sim = EF.Functions.TrigramsSimilarity(c.NormalizedAddress!, normalizedAddress)
                })
                .ToListAsync();

            foreach (var m in addressMatches.Where(x => x.Sim >= 0.5))
            {
                if (scoreMap.TryGetValue(m.Id, out var current))
                {
                    scoreMap[m.Id] = (Math.Min(1.0, current.Score + 0.10), current.MatchedBy);
                }
            }
        }

        if (scoreMap.Count == 0)
        {
            return new ResolveClientResponse(new List<ResolveCandidateDto>(), "create");
        }

        var topIds = scoreMap
            .OrderByDescending(kv => kv.Value.Score)
            .Take(TopN)
            .Select(kv => kv.Key)
            .ToList();

        var clients = await _db.Clients
            .Where(c => topIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Phone,
                c.Address,
                c.Tag,
                c.Type,
                OrdersCount = c.Orders.Count(),
                TotalSpent = c.Orders
                    .Where(o => o.Status != OrderStatus.Canceled)
                    .Sum(o => (decimal?)o.Total) ?? 0m,
                BalanceDue = c.Orders
                    .Where(o => o.Status != OrderStatus.Canceled && o.Status != OrderStatus.Delivered)
                    .Sum(o => (decimal?)o.Total) ?? 0m,
                Aliases = c.Aliases.Select(a => a.Alias).ToList()
            })
            .ToListAsync();

        var candidates = topIds
            .Select(id =>
            {
                var c = clients.First(x => x.Id == id);
                var (score, matchedBy) = scoreMap[id];
                return new ResolveCandidateDto(
                    ClientId: c.Id,
                    Name: c.Name,
                    Phone: c.Phone,
                    Address: c.Address,
                    Tag: c.Tag.ToString(),
                    Type: c.Type,
                    OrdersCount: c.OrdersCount,
                    TotalSpent: c.TotalSpent,
                    Aliases: c.Aliases,
                    BalanceDue: c.BalanceDue,
                    Score: Math.Round(score, 3),
                    MatchedBy: matchedBy);
            })
            .ToList();

        var action = DetermineAction(candidates);
        return new ResolveClientResponse(candidates, action);
    }

    public async Task<ClientAliasDto> AddAliasAsync(int clientId, string alias, ClientAliasSource source)
    {
        var trimmed = (alias ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("Alias vacío", nameof(alias));

        var normalized = TextNormalizer.NormalizeName(trimmed);
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("Alias no contiene caracteres válidos", nameof(alias));

        // Si ya existe ese alias normalizado apuntando a este cliente, incrementar TimesSeen
        var existing = await _db.ClientAliases
            .FirstOrDefaultAsync(a => a.NormalizedAlias == normalized);

        if (existing != null)
        {
            if (existing.ClientId == clientId)
            {
                existing.TimesSeen += 1;
                await _db.SaveChangesAsync();
                return ToDto(existing);
            }
            // Conflicto: este alias normalizado ya pertenece a otra clienta. No tocamos
            // nada y devolvemos el existente para que el frontend decida.
            return ToDto(existing);
        }

        // No agregar si el alias normalizado coincide con el NormalizedName del cliente
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId);
        if (client == null) throw new InvalidOperationException("Cliente no existe");

        if (client.NormalizedName == normalized)
        {
            // Es el mismo nombre canónico, no agregamos alias redundante.
            return new ClientAliasDto(0, trimmed, source.ToString(), 0, DateTime.UtcNow);
        }

        var newAlias = new ClientAlias
        {
            ClientId = clientId,
            Alias = trimmed,
            NormalizedAlias = normalized,
            CreatedAt = DateTime.UtcNow,
            Source = source,
            TimesSeen = 1,
        };
        _db.ClientAliases.Add(newAlias);
        await _db.SaveChangesAsync();
        return ToDto(newAlias);
    }

    public async Task MergeAsync(int sourceId, int targetId)
    {
        if (sourceId == targetId) throw new ArgumentException("Source y target son la misma clienta");

        using var tx = await _db.Database.BeginTransactionAsync();

        var source = await _db.Clients
            .Include(c => c.Aliases)
            .FirstOrDefaultAsync(c => c.Id == sourceId)
            ?? throw new InvalidOperationException($"Cliente source {sourceId} no existe");

        var target = await _db.Clients
            .Include(c => c.Aliases)
            .FirstOrDefaultAsync(c => c.Id == targetId)
            ?? throw new InvalidOperationException($"Cliente target {targetId} no existe");

        // 1. Reasignar todas las órdenes del source al target
        await _db.Orders
            .Where(o => o.ClientId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ClientId, targetId));

        // 2. Reasignar transacciones de puntos
        await _db.LoyaltyTransactions
            .Where(t => t.ClientId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClientId, targetId));

        // 3. Mover aliases del source al target (evitando duplicar normalizados)
        var targetAliases = new HashSet<string>(target.Aliases.Select(a => a.NormalizedAlias));
        foreach (var alias in source.Aliases.ToList())
        {
            if (!targetAliases.Contains(alias.NormalizedAlias))
            {
                alias.ClientId = targetId;
                targetAliases.Add(alias.NormalizedAlias);
            }
            else
            {
                _db.ClientAliases.Remove(alias);
            }
        }

        // 4. Agregar el nombre canónico del source como alias del target (si no coincide)
        if (source.NormalizedName != target.NormalizedName &&
            !targetAliases.Contains(source.NormalizedName))
        {
            _db.ClientAliases.Add(new ClientAlias
            {
                ClientId = targetId,
                Alias = source.Name,
                NormalizedAlias = source.NormalizedName,
                CreatedAt = DateTime.UtcNow,
                Source = ClientAliasSource.Merge,
                TimesSeen = 1,
            });
        }

        // 5. Sumar puntos del source al target
        target.CurrentPoints += source.CurrentPoints;
        target.LifetimePoints += source.LifetimePoints;

        // 6. Borrar el source (las suscripciones push / chat caen con cascade en sus FKs propios)
        _db.Clients.Remove(source);

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<List<DuplicateSuggestionDto>> GetDuplicateSuggestionsAsync(int limit = 50)
    {
        var suggestions = new List<DuplicateSuggestionDto>();
        var seen = new HashSet<(int, int)>();

        // 1. Pares con mismo teléfono normalizado (alta confianza)
        var phoneGroups = await _db.Clients
            .Where(c => c.NormalizedPhone != null && c.NormalizedPhone != "")
            .GroupBy(c => c.NormalizedPhone)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(c => new
            {
                c.Id,
                c.Name,
                OrdersCount = c.Orders.Count()
            }).ToList())
            .Take(limit)
            .ToListAsync();

        foreach (var group in phoneGroups)
        {
            for (int i = 0; i < group.Count; i++)
            {
                for (int j = i + 1; j < group.Count; j++)
                {
                    var key = OrderKey(group[i].Id, group[j].Id);
                    if (seen.Add(key))
                    {
                        suggestions.Add(new DuplicateSuggestionDto(
                            LeftClientId: group[i].Id,
                            LeftName: group[i].Name,
                            LeftOrdersCount: group[i].OrdersCount,
                            RightClientId: group[j].Id,
                            RightName: group[j].Name,
                            RightOrdersCount: group[j].OrdersCount,
                            Reason: "same-phone",
                            Confidence: 0.95));
                    }
                }
            }
        }

        if (suggestions.Count >= limit) return suggestions.Take(limit).ToList();

        // 2. Pares con nombre normalizado parecido por trigram (similarity > 0.7)
        var clients = await _db.Clients
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.NormalizedName,
                OrdersCount = c.Orders.Count()
            })
            .ToListAsync();

        var remaining = limit - suggestions.Count;
        if (remaining <= 0) return suggestions;

        // Para mantenerse simple (volumen esperado <5,000 clientas) hacemos el cruce
        // en memoria limitado al top de candidatos por nombre normalizado.
        for (int i = 0; i < clients.Count && suggestions.Count < limit; i++)
        {
            for (int j = i + 1; j < clients.Count && suggestions.Count < limit; j++)
            {
                var key = OrderKey(clients[i].Id, clients[j].Id);
                if (seen.Contains(key)) continue;

                var sim = TrigramJaccard(clients[i].NormalizedName, clients[j].NormalizedName);
                if (sim >= 0.7)
                {
                    seen.Add(key);
                    suggestions.Add(new DuplicateSuggestionDto(
                        LeftClientId: clients[i].Id,
                        LeftName: clients[i].Name,
                        LeftOrdersCount: clients[i].OrdersCount,
                        RightClientId: clients[j].Id,
                        RightName: clients[j].Name,
                        RightOrdersCount: clients[j].OrdersCount,
                        Reason: "similar-name",
                        Confidence: sim));
                }
            }
        }

        return suggestions.OrderByDescending(s => s.Confidence).Take(limit).ToList();
    }

    private static string DetermineAction(List<ResolveCandidateDto> candidates)
    {
        if (candidates.Count == 0) return "create";
        var top = candidates[0].Score;
        if (top < CreateMaxThreshold) return "create";
        if (top >= UseThreshold)
        {
            if (candidates.Count == 1) return "use";
            var nextScore = candidates[1].Score;
            if (top - nextScore >= UseMarginOverNext) return "use";
        }
        return "choose";
    }

    private static void Upsert(Dictionary<int, (double Score, string MatchedBy)> map, int clientId, double score, string matchedBy)
    {
        if (!map.TryGetValue(clientId, out var current) || score > current.Score)
        {
            map[clientId] = (score, matchedBy);
        }
    }

    private static (int, int) OrderKey(int a, int b) => a < b ? (a, b) : (b, a);

    private static ClientAliasDto ToDto(ClientAlias a) =>
        new(a.Id, a.Alias, a.Source.ToString(), a.TimesSeen, a.CreatedAt);

    /// <summary>
    /// Aproximación simple de similarity por trigramas (Jaccard sobre conjuntos de 3-grams).
    /// Sirve para el cruce en memoria del análisis de duplicados. Para queries de resolver
    /// usamos pg_trgm directo en SQL.
    /// </summary>
    private static double TrigramJaccard(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var setA = Trigrams(a);
        var setB = Trigrams(b);
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var intersect = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)intersect / union;
    }

    private static HashSet<string> Trigrams(string s)
    {
        var padded = "  " + s + " ";
        var set = new HashSet<string>();
        for (int i = 0; i <= padded.Length - 3; i++)
        {
            set.Add(padded.Substring(i, 3));
        }
        return set;
    }
}

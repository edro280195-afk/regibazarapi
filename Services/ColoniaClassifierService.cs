using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EntregasApi.Services;

public class ColoniaClassifierService : IColoniaClassifierService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    // Matriz oficial de Regi Bazar en Nuevo Laredo
    // C. Rio Indo 1601, Voluntad y Trabajo 3, 88177 Nuevo Laredo, Tamps.
    public const double DepotLatitude = 27.50884;
    public const double DepotLongitude = -99.56885;
    public const string PickupName = "Pickup (Matriz Regi Bazar)";

    private record NldColonia(string Name, double Latitude, double Longitude, string Zone, string[] Keywords);

    private static readonly List<NldColonia> Catalog = new()
    {
        // ── ZONA PONIENTE (❤️ Corazón) ──
        new("Col. Voluntad y Trabajo", 27.5120, -99.5690, "Poniente", new[] { "voluntad y trabajo", "borreguera", "la borreguera", "voluntad", "vyt" }),
        new("Col. Francisco Villa", 27.4366, -99.6078, "Poniente", new[] { "francisco villa", "pancho villa", "fco villa" }),
        new("Col. Buena Vista", 27.5015, -99.5550, "Poniente", new[] { "buena vista", "buenavista" }),
        new("Col. Nueva Era", 27.4808, -99.5625, "Poniente", new[] { "nueva era" }),
        new("Villas de San Miguel", 27.4740, -99.6050, "Poniente", new[] { "villas de san miguel", "san miguel", "villas de sn miguel", "v. san miguel" }),
        new("Fracc. El Campanario", 27.4580, -99.5910, "Poniente", new[] { "campanario", "el campanario", "campanario y oradel" }),
        new("Col. Solidaridad", 27.4920, -99.5870, "Poniente", new[] { "solidaridad" }),
        new("Col. La Fe", 27.4790, -99.5620, "Poniente", new[] { "la fe", "fracc la fe" }),
        new("Col. Las Torres", 27.4730, -99.5480, "Poniente", new[] { "torres", "las torres" }),
        new("Reservas Territoriales", 27.4640, -99.5760, "Poniente", new[] { "reservas", "reservas territoriales" }),
        new("Col. 150 Aniversario", 27.4790, -99.6220, "Poniente", new[] { "150 aniversario", "aniversario" }),
        new("Fracc. El Progreso", 27.4700, -99.6150, "Poniente", new[] { "progreso", "el progreso" }),
        new("Col. Cavazos Lerma", 27.4390, -99.6050, "Poniente", new[] { "cavazos lerma", "cavazos", "manuel cavazos" }),
        new("Valles de San Agustín", 27.4560, -99.6120, "Poniente", new[] { "valles de san agustin", "san agustin", "san agustín" }),
        new("Valles de Anáhuac", 27.4950, -99.6200, "Poniente", new[] { "valles de anahuac", "anahuac", "anáhuac" }),

        // ── ZONA ORIENTE / CENTRO (⭐ Estrella) ──
        new("Sector Centro", 27.4950, -99.5075, "Oriente", new[] { "centro", "sector centro", "primer cuadro" }),
        new("Col. Madero", 27.4880, -99.5100, "Oriente", new[] { "madero", "fracc madero" }),
        new("Col. Viveros", 27.4940, -99.4990, "Oriente", new[] { "viveros" }),
        new("Col. Jardín", 27.4785, -99.5130, "Oriente", new[] { "jardin", "jardín" }),
        new("Col. Campestre", 27.4690, -99.5240, "Oriente", new[] { "campestre", "fracc campestre" }),
        new("Col. Juárez", 27.4840, -99.5290, "Oriente", new[] { "juarez", "juárez", "benito juarez", "benito juárez" }),
        new("Col. Guerrero", 27.4770, -99.5030, "Oriente", new[] { "guerrero" }),
        new("Col. Mirador", 27.4610, -99.5170, "Oriente", new[] { "mirador", "el mirador" }),
        new("Col. San Rafael", 27.4635, -99.5060, "Oriente", new[] { "san rafael" }),
        new("Col. Palacios", 27.4990, -99.5330, "Oriente", new[] { "palacios" }),
        new("Col. Victoria", 27.4910, -99.5220, "Oriente", new[] { "victoria" }),
        new("Col. Hidalgo", 27.4995, -99.5220, "Oriente", new[] { "hidalgo" }),
        new("Col. Militar", 27.4705, -99.5090, "Oriente", new[] { "militar", "cuartel militar" }),
        new("Col. Longoria", 27.4850, -99.5050, "Oriente", new[] { "longoria" }),
        new("Col. Hipódromo", 27.4950, -99.5400, "Oriente", new[] { "hipodromo", "hipódromo" }),
        new("Col. 20 de Noviembre", 27.4920, -99.5140, "Oriente", new[] { "20 de noviembre", "veinte de noviembre" }),
        new("Col. La Rosita", 27.4890, -99.5350, "Oriente", new[] { "rosita", "la rosita" }),

        // ── ZONA SUR (👍 Manita Arriba) ──
        new("Infonavit Fundadores", 27.4482, -99.5114, "Sur", new[] { "fundadores", "infonavit fundadores", "infonavit" }),
        new("Fracc. Los Fresnos", 27.4465, -99.5525, "Sur", new[] { "fresnos", "los fresnos" }),
        new("Col. Concordia", 27.4370, -99.5260, "Sur", new[] { "concordia", "la concordia", "haciendas de la concordia" }),
        new("Col. Santa Fe", 27.4470, -99.5110, "Sur", new[] { "santa fe" }),
        new("Col. Los Presidentes", 27.4290, -99.5360, "Sur", new[] { "presidentes", "los presidentes" }),
        new("Col. Roma", 27.4570, -99.5390, "Sur", new[] { "roma", "fracc roma" }),
        new("Col. Los Encinos", 27.4310, -99.5530, "Sur", new[] { "encinos", "los encinos" }),
        new("Col. Las Alamedas", 27.4600, -99.5560, "Sur", new[] { "alamedas", "las alamedas" }),
        new("Colinas del Sur", 27.4140, -99.5340, "Sur", new[] { "colinas del sur", "colinas" }),
        new("Fracc. Los Aztecas", 27.4800, -99.5360, "Sur", new[] { "aztecas", "los aztecas" }),
        new("Col. ISSSTE", 27.4700, -99.5180, "Sur", new[] { "issste", "fovissste" }),
        new("Fracc. San José", 27.4550, -99.5150, "Sur", new[] { "san jose", "san josé" }),
        new("Fracc. Las Américas", 27.4680, -99.5380, "Sur", new[] { "americas", "las americas", "las américas" }),
        new("Col. Alianza", 27.4860, -99.5530, "Sur", new[] { "alianza", "alianza para la produccion" }),

        // ── ZONA KILÓMETROS (👏 Aplauso) ──
        new("Valle Elizondo", 27.3450, -99.5550, "Kilometros", new[] { "valle elizondo", "elizondo" }),
        new("Km 10 a Km 18", 27.3800, -99.5300, "Kilometros", new[] { "km 10", "km 12", "km 13", "km 14", "km 15", "km 18", "kilometro", "kilometros" }),
        new("Fracc. Los Cántaros", 27.4180, -99.5390, "Kilometros", new[] { "cantaros", "los cantaros", "cántaros" }),
        new("Granjas Treviño", 27.3600, -99.5400, "Kilometros", new[] { "granjas trevino", "granjas treviño", "trevino", "treviño" }),
        new("Granjas Regina", 27.3550, -99.5450, "Kilometros", new[] { "granjas regina" })
    };

    private static readonly List<(string Keyword, NldColonia Colonia)> SortedKeywords = Catalog
        .SelectMany(c => c.Keywords.Select(kw => (Keyword: kw, Colonia: c)))
        .OrderByDescending(x => x.Keyword.Length)
        .ToList();

    public ColoniaClassifierService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public ColoniaMatch ClassifyOrder(Order order)
    {
        if (order.OrderType == OrderType.PickUp)
        {
            return new ColoniaMatch(PickupName, DepotLatitude, DepotLongitude, true, "Pickup");
        }

        var address = !string.IsNullOrWhiteSpace(order.AlternativeAddress)
            ? order.AlternativeAddress
            : order.Client?.Address;

        var lat = order.Client?.Latitude;
        var lng = order.Client?.Longitude;

        return ClassifyAddress(address, lat, lng, order.OrderType);
    }

    public ColoniaMatch ClassifyAddress(string? address, double? latitude = null, double? longitude = null, OrderType? orderType = null)
    {
        // Sin dirección capturada o explícitamente pickup -> Pickup en Matriz
        if (orderType == OrderType.PickUp || string.IsNullOrWhiteSpace(address))
        {
            return new ColoniaMatch(PickupName, DepotLatitude, DepotLongitude, true, "Pickup");
        }

        var normalized = Normalize(address);

        // Si menciona la matriz de Regi Bazar (C. Rio Indo 1601, Voluntad y Trabajo 3) o palabras de pickup
        if (IsPickupKeyword(normalized))
        {
            return new ColoniaMatch(PickupName, DepotLatitude, DepotLongitude, true, "Pickup");
        }

        // Búsqueda en el catálogo por palabras clave (priorizando frases más largas)
        foreach (var entry in SortedKeywords)
        {
            var normKw = Normalize(entry.Keyword);
            if (Regex.IsMatch(normalized, $@"\b{Regex.Escape(normKw)}\b", RegexOptions.IgnoreCase))
            {
                return new ColoniaMatch(entry.Colonia.Name, entry.Colonia.Latitude, entry.Colonia.Longitude, false, entry.Colonia.Zone);
            }
        }

        // Coincidencia por proximidad de coordenadas en Nuevo Laredo
        if (latitude.HasValue && longitude.HasValue && IsWithinNld(latitude.Value, longitude.Value))
        {
            var closest = Catalog
                .Select(c => new { Colonia = c, Distance = HaversineKm(latitude.Value, longitude.Value, c.Latitude, c.Longitude) })
                .OrderBy(x => x.Distance)
                .FirstOrDefault();

            if (closest != null && closest.Distance <= 2.8)
            {
                return new ColoniaMatch(closest.Colonia.Name, closest.Colonia.Latitude, closest.Colonia.Longitude, false, closest.Colonia.Zone);
            }
        }

        // Fallback
        return new ColoniaMatch("Otras Colonias (NLD)", 27.4861, -99.5150, false, "Oriente");
    }

    public async Task<ColoniaReportDto> GetColoniaReportAsync(DateTime startDate, DateTime endDateExclusive, int? salesPeriodId = null)
    {
        var query = _db.Orders
            .Include(o => o.Client)
            .AsNoTracking();

        if (salesPeriodId.HasValue && salesPeriodId.Value > 0)
        {
            query = query.Where(o => o.SalesPeriodId == salesPeriodId.Value);
        }
        else
        {
            query = query.Where(o => o.CreatedAt >= startDate && o.CreatedAt < endDateExclusive);
        }

        var orders = await query
            .Where(o => o.Status != OrderStatus.Canceled)
            .ToListAsync();

        if (orders.Count == 0)
        {
            return new ColoniaReportDto(
                TotalOrders: 0,
                TotalColoniaOrders: 0,
                TotalPickupOrders: 0,
                TopColoniaName: "Sin datos",
                TopColoniaPercentage: 0.0,
                Colonias: new List<ColoniaStatsDto>(),
                Zones: new List<ZoneSummaryDto>()
            );
        }

        var totalOrders = orders.Count;

        var classified = orders.Select(o => new
        {
            Order = o,
            Match = ClassifyOrder(o),
            ClientId = o.ClientId
        }).ToList();

        var grouped = classified
            .GroupBy(x => x.Match.Name)
            .Select(g =>
            {
                var first = g.First().Match;
                var count = g.Count();
                var pct = Math.Round((double)count / totalOrders * 100.0, 1);
                var clientCount = g.Select(x => x.ClientId).Distinct().Count();

                // Las coordenadas de la colonia se toman de su centroide geográfico verificado
                return new
                {
                    Name = g.Key,
                    Count = count,
                    Percentage = pct,
                    Latitude = first.Latitude,
                    Longitude = first.Longitude,
                    IsPickup = first.IsPickup,
                    ClientsCount = clientCount,
                    Zone = first.Zone
                };
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        var totalPickup = grouped.Where(x => x.IsPickup).Sum(x => x.Count);
        var totalColonia = totalOrders - totalPickup;

        // La colonia reina NUNCA debe ser 'Otras Colonias' ni 'Pickup'
        var topColonia = grouped.FirstOrDefault(x => !x.IsPickup && x.Name != "Otras Colonias (NLD)")
                         ?? grouped.FirstOrDefault(x => !x.IsPickup)
                         ?? grouped.FirstOrDefault();

        var topName = topColonia?.Name ?? "Sin datos";
        var topPct = topColonia?.Percentage ?? 0.0;

        // Clasificamos colonias válidas primero (con rango 1..N), y al final 'Otras Colonias'
        var rankedList = new List<ColoniaStatsDto>();
        int currentRank = 1;

        // 1. Colonias nombradas ordenadas por compras
        var namedColonias = grouped.Where(x => !x.IsPickup && x.Name != "Otras Colonias (NLD)").ToList();
        foreach (var c in namedColonias)
        {
            rankedList.Add(new ColoniaStatsDto(
                ColoniaName: c.Name,
                OrdersCount: c.Count,
                Percentage: c.Percentage,
                Latitude: c.Latitude,
                Longitude: c.Longitude,
                Rank: currentRank++,
                IsPickup: false,
                ClientsCount: c.ClientsCount,
                Zone: c.Zone
            ));
        }

        // 2. Otras Colonias (si existen, con rank 0 al final de colonias)
        var others = grouped.FirstOrDefault(x => x.Name == "Otras Colonias (NLD)");
        if (others != null)
        {
            rankedList.Add(new ColoniaStatsDto(
                ColoniaName: others.Name,
                OrdersCount: others.Count,
                Percentage: others.Percentage,
                Latitude: others.Latitude,
                Longitude: others.Longitude,
                Rank: currentRank++,
                IsPickup: false,
                ClientsCount: others.ClientsCount,
                Zone: "Oriente"
            ));
        }

        // 3. Pickup
        var pickupGroup = grouped.FirstOrDefault(x => x.IsPickup);
        if (pickupGroup != null)
        {
            rankedList.Add(new ColoniaStatsDto(
                ColoniaName: pickupGroup.Name,
                OrdersCount: pickupGroup.Count,
                Percentage: pickupGroup.Percentage,
                Latitude: DepotLatitude,
                Longitude: DepotLongitude,
                Rank: 0,
                IsPickup: true,
                ClientsCount: pickupGroup.ClientsCount,
                Zone: "Pickup"
            ));
        }

        // ── CÁLCULO DE RESUMEN POR ZONAS (PARTIDAS DE NUEVO LAREDO) ──
        var zoneDefs = new[]
        {
            (Key: "Poniente", Label: "Poniente", Emoji: "❤️"),
            (Key: "Oriente", Label: "Oriente / Centro", Emoji: "⭐"),
            (Key: "Sur", Label: "Sur", Emoji: "👍"),
            (Key: "Kilometros", Label: "Kilómetros", Emoji: "👏"),
            (Key: "Pickup", Label: "Pickup (Matriz)", Emoji: "🛍️")
        };

        var zoneSummaries = new List<ZoneSummaryDto>();
        foreach (var z in zoneDefs)
        {
            var inZone = rankedList.Where(c => c.Zone == z.Key).ToList();
            var zOrders = inZone.Sum(c => c.OrdersCount);
            var zClients = inZone.Sum(c => c.ClientsCount);
            var zPct = Math.Round((double)zOrders / totalOrders * 100.0, 1);
            var topInZone = inZone.Where(c => !c.IsPickup && c.ColoniaName != "Otras Colonias (NLD)").OrderByDescending(c => c.OrdersCount).FirstOrDefault()
                            ?? inZone.OrderByDescending(c => c.OrdersCount).FirstOrDefault();

            zoneSummaries.Add(new ZoneSummaryDto(
                Zone: z.Key,
                DisplayName: z.Label,
                Emoji: z.Emoji,
                OrdersCount: zOrders,
                Percentage: zPct,
                TopColoniaName: topInZone?.ColoniaName ?? "Sin datos",
                TopColoniaPercentage: topInZone?.Percentage ?? 0.0,
                ClientsCount: zClients
            ));
        }

        return new ColoniaReportDto(
            TotalOrders: totalOrders,
            TotalColoniaOrders: totalColonia,
            TotalPickupOrders: totalPickup,
            TopColoniaName: topName,
            TopColoniaPercentage: topPct,
            Colonias: rankedList,
            Zones: zoneSummaries
        );
    }

    private static bool IsPickupKeyword(string text)
    {
        return text.Contains("pickup") ||
               text.Contains("pick up") ||
               text.Contains("recoge") ||
               text.Contains("pasa por") ||
               text.Contains("pasan por") ||
               text.Contains("pasa a") ||
               text.Contains("en tienda") ||
               text.Contains("en matriz") ||
               text.Contains("sin direccion") ||
               text.Contains("pasar a regibazar") ||
               text.Contains("domicilio regibazar") ||
               text.Contains("regibazar matriz") ||
               text.Contains("rio indo") ||
               text.Contains("río indo") ||
               text.Contains("indo 1601");
    }

    private static bool IsWithinNld(double lat, double lng)
    {
        return lat >= 27.20 && lat <= 27.70 && lng >= -99.75 && lng <= -99.40;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    private static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().ToLowerInvariant().Trim();
    }
}

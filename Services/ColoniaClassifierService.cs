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

    // Depot fijo de Regi Bazar en Nuevo Laredo
    public const double DepotLatitude = 27.4861;
    public const double DepotLongitude = -99.5069;
    public const string PickupName = "Pickup (Matriz Regi Bazar)";

    private record NldColonia(string Name, double Latitude, double Longitude, string[] Keywords);

    private static readonly List<NldColonia> Catalog = new()
    {
        new("Col. Madero", 27.4880, -99.5100, new[] { "madero", "fracc madero" }),
        new("Col. Viveros", 27.4940, -99.4990, new[] { "viveros" }),
        new("Col. Jardín", 27.4785, -99.5130, new[] { "jardin", "jardín" }),
        new("Col. Campestre", 27.4690, -99.5240, new[] { "campestre", "fracc campestre" }),
        new("Sector Centro", 27.4950, -99.5075, new[] { "centro", "sector centro", "primer cuadro" }),
        new("Col. Juárez", 27.4840, -99.5290, new[] { "juarez", "juárez", "benito juarez", "benito juárez" }),
        new("Col. Guerrero", 27.4770, -99.5030, new[] { "guerrero" }),
        new("Col. Mirador", 27.4610, -99.5170, new[] { "mirador", "el mirador" }),
        new("Col. San Rafael", 27.4635, -99.5060, new[] { "san rafael" }),
        new("Infonavit Fundadores", 27.4520, -99.5310, new[] { "fundadores", "infonavit fundadores" }),
        new("Fracc. Los Fresnos", 27.4420, -99.5430, new[] { "fresnos", "los fresnos" }),
        new("Col. Las Torres", 27.4730, -99.5480, new[] { "torres", "las torres" }),
        new("Col. Buena Vista", 27.4850, -99.5440, new[] { "buena vista", "buenavista" }),
        new("Col. La Fe", 27.4790, -99.5620, new[] { "la fe", "fracc la fe" }),
        new("Col. Nueva Era", 27.4860, -99.5760, new[] { "nueva era" }),
        new("Col. Solidaridad", 27.4920, -99.5870, new[] { "solidaridad" }),
        new("Villas de San Miguel", 27.4740, -99.6050, new[] { "villas de san miguel", "san miguel" }),
        new("Fracc. El Campanario", 27.4580, -99.5910, new[] { "campanario", "el campanario" }),
        new("Col. Concordia", 27.4370, -99.5260, new[] { "concordia", "la concordia", "haciendas de la concordia" }),
        new("Col. Santa Fe", 27.4470, -99.5110, new[] { "santa fe" }),
        new("Col. Los Presidentes", 27.4290, -99.5360, new[] { "presidentes", "los presidentes" }),
        new("Col. Palacios", 27.4990, -99.5330, new[] { "palacios" }),
        new("Col. Victoria", 27.4910, -99.5220, new[] { "victoria" }),
        new("Col. Hidalgo", 27.4995, -99.5220, new[] { "hidalgo" }),
        new("Col. Militar", 27.4705, -99.5090, new[] { "militar", "cuartel militar" }),
        new("Col. Roma", 27.4570, -99.5390, new[] { "roma", "fracc roma" }),
        new("Col. Los Encinos", 27.4310, -99.5530, new[] { "encinos", "los encinos" }),
        new("Col. Las Alamedas", 27.4600, -99.5560, new[] { "alamedas", "las alamedas" }),
        new("Col. 150 Aniversario", 27.4790, -99.6220, new[] { "150 aniversario", "aniversario" }),
        new("Reservas Territoriales", 27.4640, -99.5760, new[] { "reservas", "reservas territoriales" }),
        new("Colinas del Sur", 27.4140, -99.5340, new[] { "colinas del sur", "colinas" }),
        new("Fracc. Los Aztecas", 27.4800, -99.5360, new[] { "aztecas", "los aztecas" }),
        new("Col. Francisco Villa", 27.4730, -99.5350, new[] { "francisco villa", "pancho villa" }),
        new("Col. Voluntad y Trabajo", 27.4880, -99.5650, new[] { "voluntad y trabajo", "borreguera", "la borreguera" }),
        new("Fracc. El Progreso", 27.4700, -99.6150, new[] { "progreso", "el progreso" }),
        new("Km 10 a Km 18", 27.3800, -99.5300, new[] { "km 10", "km 12", "km 13", "km 14", "km 15", "km 18", "kilometro" }),
        new("Fracc. Los Cántaros", 27.4180, -99.5390, new[] { "cantaros", "los cantaros", "cántaros" }),
        new("Col. Longoria", 27.4850, -99.5050, new[] { "longoria" }),
        new("Col. ISSSTE", 27.4700, -99.5180, new[] { "issste", "fovissste" }),
        new("Col. Hipódromo", 27.4950, -99.5400, new[] { "hipodromo", "hipódromo" }),
        new("Fracc. San José", 27.4550, -99.5150, new[] { "san jose", "san josé" }),
        new("Col. La Rosita", 27.4890, -99.5350, new[] { "rosita", "la rosita" }),
        new("Fracc. Las Américas", 27.4680, -99.5380, new[] { "americas", "las americas", "las américas" }),
        new("Col. Alianza", 27.4860, -99.5530, new[] { "alianza", "alianza para la produccion" }),
        new("Valles de San Agustín", 27.4560, -99.6120, new[] { "valles de san agustin", "san agustin", "san agustín" })
    };

    public ColoniaClassifierService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public ColoniaMatch ClassifyOrder(Order order)
    {
        // 1. Si es orden tipo Pickup explícita
        if (order.OrderType == OrderType.PickUp)
        {
            return new ColoniaMatch(PickupName, DepotLatitude, DepotLongitude, true);
        }

        // 2. Tomar dirección alternativa de orden o dirección de la clienta
        var address = !string.IsNullOrWhiteSpace(order.AlternativeAddress)
            ? order.AlternativeAddress
            : order.Client?.Address;

        var lat = order.Client?.Latitude;
        var lng = order.Client?.Longitude;

        return ClassifyAddress(address, lat, lng, order.OrderType);
    }

    public ColoniaMatch ClassifyAddress(string? address, double? latitude = null, double? longitude = null, OrderType? orderType = null)
    {
        // Si no hay dirección capturada o es explícitamente pickup -> Pickup en Matriz
        if (orderType == OrderType.PickUp || string.IsNullOrWhiteSpace(address))
        {
            return new ColoniaMatch(PickupName, DepotLatitude, DepotLongitude, true);
        }

        var normalized = Normalize(address);

        // Detectar palabras clave de Pickup en el texto de dirección
        if (IsPickupKeyword(normalized))
        {
            return new ColoniaMatch(PickupName, DepotLatitude, DepotLongitude, true);
        }

        // Búsqueda por palabras clave de colonias de Nuevo Laredo
        foreach (var c in Catalog)
        {
            foreach (var kw in c.Keywords)
            {
                var normKw = Normalize(kw);
                // Búsqueda de palabra o frase completa usando límites de palabra
                if (Regex.IsMatch(normalized, $@"\b{Regex.Escape(normKw)}\b", RegexOptions.IgnoreCase))
                {
                    return new ColoniaMatch(c.Name, c.Latitude, c.Longitude, false);
                }
            }
        }

        // Si no hubo coincidencia por texto pero tenemos coordenadas dentro de Nuevo Laredo
        if (latitude.HasValue && longitude.HasValue && IsWithinNld(latitude.Value, longitude.Value))
        {
            var closest = Catalog
                .Select(c => new { Colonia = c, Distance = HaversineKm(latitude.Value, longitude.Value, c.Latitude, c.Longitude) })
                .OrderBy(x => x.Distance)
                .FirstOrDefault();

            if (closest != null && closest.Distance <= 2.8)
            {
                return new ColoniaMatch(closest.Colonia.Name, closest.Colonia.Latitude, closest.Colonia.Longitude, false);
            }
        }

        // Fallback para direcciones con texto de Nuevo Laredo pero sin colonia identificada
        return new ColoniaMatch("Otras Colonias (NLD)", 27.4861, -99.5150, false);
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

        // Excluimos cancelados para medir compras reales
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
                Colonias: new List<ColoniaStatsDto>()
            );
        }

        var totalOrders = orders.Count;

        // Clasificar cada pedido
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

                return new
                {
                    Name = g.Key,
                    Count = count,
                    Percentage = pct,
                    Latitude = first.Latitude,
                    Longitude = first.Longitude,
                    IsPickup = first.IsPickup,
                    ClientsCount = clientCount
                };
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        var totalPickup = grouped.Where(x => x.IsPickup).Sum(x => x.Count);
        var totalColonia = totalOrders - totalPickup;

        // La colonia reina (Top #1 que no sea Pickup)
        var topColonia = grouped.FirstOrDefault(x => !x.IsPickup) ?? grouped.FirstOrDefault();
        var topName = topColonia?.Name ?? "Sin datos";
        var topPct = topColonia?.Percentage ?? 0.0;

        int currentRank = 1;
        var statsList = grouped.Select(g => new ColoniaStatsDto(
            ColoniaName: g.Name,
            OrdersCount: g.Count,
            Percentage: g.Percentage,
            Latitude: g.Latitude,
            Longitude: g.Longitude,
            Rank: g.IsPickup ? 0 : currentRank++,
            IsPickup: g.IsPickup,
            ClientsCount: g.ClientsCount
        )).ToList();

        return new ColoniaReportDto(
            TotalOrders: totalOrders,
            TotalColoniaOrders: totalColonia,
            TotalPickupOrders: totalPickup,
            TopColoniaName: topName,
            TopColoniaPercentage: topPct,
            Colonias: statsList
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
               text.Contains("regibazar matriz");
    }

    private static bool IsWithinNld(double lat, double lng)
    {
        return lat >= 27.20 && lat <= 27.70 && lng >= -99.75 && lng <= -99.40;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371; // Radio de la Tierra en km
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

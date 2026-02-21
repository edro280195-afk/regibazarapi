using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebPush;
using System.Text.Json;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PushController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public PushController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    // ═══════════════════════════════════════════
    //  SUSCRIPCIÓN (Ahora con Role)
    // ═══════════════════════════════════════════

    [HttpPost("subscribe")]
    [AllowAnonymous]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest req)
    {
        var existing = await _db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint);

        if (existing == null)
        {
            var sub = new PushSubscriptionModel
            {
                Endpoint = req.Endpoint,
                P256dh = req.Keys.P256dh,
                Auth = req.Keys.Auth,
                Role = req.Role ?? "client",
                ClientId = req.ClientId,
                DriverRouteToken = req.DriverRouteToken,
                LastUsedAt = DateTime.UtcNow
            };

            _db.PushSubscriptions.Add(sub);
        }
        else
        {
            existing.P256dh = req.Keys.P256dh;
            existing.Auth = req.Keys.Auth;
            existing.LastUsedAt = DateTime.UtcNow;

            // Actualizar role/identifiers si vienen
            if (!string.IsNullOrEmpty(req.Role)) existing.Role = req.Role;
            if (req.ClientId.HasValue) existing.ClientId = req.ClientId;
            if (!string.IsNullOrEmpty(req.DriverRouteToken)) existing.DriverRouteToken = req.DriverRouteToken;
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true });
    }

    [HttpDelete("unsubscribe")]
    [AllowAnonymous]
    public async Task<IActionResult> Unsubscribe([FromQuery] string endpoint)
    {
        var sub = await _db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint);
        if (sub != null)
        {
            _db.PushSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    // ═══════════════════════════════════════════
    //  ENVÍO DIRIGIDO POR ROL
    // ═══════════════════════════════════════════

    /// <summary>
    /// Envía push a una clienta específica por su ClientId.
    /// Útil para: chat del chofer→clienta, cambios de estado, proximidad.
    /// </summary>
    [HttpPost("send/client/{clientId}")]
    [AllowAnonymous] // En producción podrías validar con token interno
    public async Task<IActionResult> SendToClient(int clientId, [FromBody] NotificationPayload payload)
    {
        var subs = await _db.PushSubscriptions
            .Where(s => s.Role == "client" && s.ClientId == clientId)
            .ToListAsync();

        var result = await SendToSubscriptions(subs, payload);
        return Ok(result);
    }

    /// <summary>
    /// Envía push al chofer de una ruta específica por su DriverRouteToken.
    /// Útil para: chat del admin→chofer, chat de clienta→chofer.
    /// </summary>
    [HttpPost("send/driver/{routeToken}")]
    [AllowAnonymous]
    public async Task<IActionResult> SendToDriver(string routeToken, [FromBody] NotificationPayload payload)
    {
        var subs = await _db.PushSubscriptions
            .Where(s => s.Role == "driver" && s.DriverRouteToken == routeToken)
            .ToListAsync();

        var result = await SendToSubscriptions(subs, payload);
        return Ok(result);
    }

    /// <summary>
    /// Envía push a TODOS los admins.
    /// Útil para: chat del chofer→admin, alertas de entregas fallidas.
    /// </summary>
    [HttpPost("send/admins")]
    [AllowAnonymous]
    public async Task<IActionResult> SendToAdmins([FromBody] NotificationPayload payload)
    {
        var subs = await _db.PushSubscriptions
            .Where(s => s.Role == "admin")
            .ToListAsync();

        var result = await SendToSubscriptions(subs, payload);
        return Ok(result);
    }

    /// <summary>
    /// Endpoint de prueba: envía a TODOS.
    /// </summary>
    [HttpPost("test")]
    [Authorize]
    public async Task<IActionResult> TestNotification([FromBody] NotificationPayload payload)
    {
        var subs = await _db.PushSubscriptions.ToListAsync();
        if (!subs.Any()) return NotFound("No hay suscripciones activas.");

        var result = await SendToSubscriptions(subs, payload);
        return Ok(result);
    }

    // ═══════════════════════════════════════════
    //  HELPER INTERNO DE ENVÍO
    // ═══════════════════════════════════════════

    private async Task<object> SendToSubscriptions(List<PushSubscriptionModel> subs, NotificationPayload payload)
    {
        if (!subs.Any())
            return new { success = 0, failed = 0, message = "No hay suscripciones para este destino." };

        var subject = _config["VapidDetails:Subject"] ?? "mailto:info@regibazar.com";
        var publicKey = _config["VapidDetails:PublicKey"];
        var privateKey = _config["VapidDetails:PrivateKey"];

        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(privateKey))
            return new { success = 0, failed = 0, message = "VAPID Keys no configuradas." };

        var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
        var webPushClient = new WebPushClient();

        int successCount = 0;
        int failCount = 0;

        foreach (var sub in subs)
        {
            try
            {
                var pushSubscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                var jsonPayload = JsonSerializer.Serialize(new
                {
                    notification = new
                    {
                        title = payload.Title,
                        body = payload.Body,
                        icon = payload.Icon ?? "/assets/icons/icon-192x192.png",
                        badge = "/assets/icons/icon-72x72.png",
                        vibrate = new[] { 200, 100, 200 },
                        tag = payload.Tag,
                        data = new
                        {
                            url = payload.Url ?? "/",
                            type = payload.Type
                        }
                    }
                });

                await webPushClient.SendNotificationAsync(pushSubscription, jsonPayload, vapidDetails);
                sub.LastUsedAt = DateTime.UtcNow;
                successCount++;
            }
            catch (WebPushException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Gone ||
                    ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Suscripción expirada → eliminar
                    _db.PushSubscriptions.Remove(sub);
                }
                failCount++;
            }
            catch
            {
                failCount++;
            }
        }

        await _db.SaveChangesAsync();
        return new { success = successCount, failed = failCount };
    }
}

// ═══════════════════════════════════════════
//  DTOs
// ═══════════════════════════════════════════

public class PushSubscriptionRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public PushKeys Keys { get; set; } = new();
    public int? ClientId { get; set; }
    public string? Role { get; set; }                // "client" | "driver" | "admin"
    public string? DriverRouteToken { get; set; }     // Token de ruta (solo para chofer)
}

public class PushKeys
{
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
}

public class NotificationPayload
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Url { get; set; }
    public string? Tag { get; set; }      // Agrupa notificaciones (reemplaza en vez de apilar)
    public string? Type { get; set; }     // "chat", "status", "proximity" - para que el SW sepa qué hacer
}
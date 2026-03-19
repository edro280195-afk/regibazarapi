using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using WebPush;
using System.Text.Json;

namespace EntregasApi.Services;

public interface IPushNotificationService
{
    Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null, string? tag = null);
    Task SendNotificationToDriverAsync(string routeToken, string title, string message, string? url = null, string? tag = null);
    Task SendNotificationToAdminsAsync(string title, string message, string? url = null, string? tag = null);

    // Helpers específicos — WebPush (clientes web/PWA)
    Task NotifyClientDriverEnRouteAsync(int clientId, string? driverName = null);
    Task NotifyClientDriverNearbyAsync(int clientId, int distanceMeters);
    Task NotifyClientDeliveredAsync(int clientId);
    Task NotifyChatMessageAsync(string targetRole, int? clientId, string? routeToken, string senderName, string messageText);

    // FCM — App Android nativa (repartidores)
    Task NotifyDriversNewRouteAsync(string routeName, string driverToken, int deliveryCount);
    Task NotifyDriverFcmAsync(string driverRouteToken, string title, string body, Dictionary<string, string>? data = null);
}

public class PushNotificationService : IPushNotificationService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PushNotificationService> _logger;
    private readonly IFcmService _fcm;

    public PushNotificationService(AppDbContext db, IConfiguration config, ILogger<PushNotificationService> logger, IFcmService fcm)
    {
        _db = db;
        _config = config;
        _logger = logger;
        _fcm = fcm;
    }

    // ═══════════════════════════════════════════
    //  ENVÍO POR ROL
    // ═══════════════════════════════════════════

    public async Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null, string? tag = null)
    {
        var subscriptions = await _db.PushSubscriptions
            .Where(s => s.Role == "client" && s.ClientId == clientId)
            .ToListAsync();

        await SendToSubscriptionsAsync(subscriptions, title, message, url, tag);
    }

    public async Task SendNotificationToDriverAsync(string routeToken, string title, string message, string? url = null, string? tag = null)
    {
        var subscriptions = await _db.PushSubscriptions
            .Where(s => s.Role == "driver" && s.DriverRouteToken == routeToken)
            .ToListAsync();

        await SendToSubscriptionsAsync(subscriptions, title, message, url, tag);
    }

    public async Task SendNotificationToAdminsAsync(string title, string message, string? url = null, string? tag = null)
    {
        var subscriptions = await _db.PushSubscriptions
            .Where(s => s.Role == "admin")
            .ToListAsync();

        await SendToSubscriptionsAsync(subscriptions, title, message, url, tag);
    }

    // ═══════════════════════════════════════════
    //  HELPERS ESPECÍFICOS
    // ═══════════════════════════════════════════

    public Task NotifyClientDriverEnRouteAsync(int clientId, string? driverName = null)
    {
        return SendNotificationToClientAsync(
            clientId,
            "🚗 ¡Tu pedido va en camino!",
            $"{driverName ?? "El repartidor"} salió hacia tu domicilio. ¡Prepárate! 💕",
            tag: "driver-en-route"
        );
    }

    public Task NotifyClientDriverNearbyAsync(int clientId, int distanceMeters)
    {
        var distText = distanceMeters < 100
            ? "a menos de 100 metros"
            : $"a {distanceMeters} metros";

        return SendNotificationToClientAsync(
            clientId,
            "📍 ¡El repartidor está muy cerca!",
            $"Tu repartidor se encuentra {distText} de tu domicilio. ¡Ya casi llega! 🎉",
            tag: "driver-nearby"
        );
    }

    public Task NotifyClientDeliveredAsync(int clientId)
    {
        return SendNotificationToClientAsync(
            clientId,
            "💝 ¡Pedido entregado!",
            "¡Tu pedido ha sido entregado! Gracias por tu compra 🌸",
            tag: "delivered"
        );
    }

    public Task NotifyChatMessageAsync(string targetRole, int? clientId, string? routeToken, string senderName, string messageText)
    {
        var preview = messageText.Length > 80 ? messageText[..80] + "..." : messageText;

        return targetRole switch
        {
            "client" when clientId.HasValue =>
                SendNotificationToClientAsync(clientId.Value, "💬 Mensaje de tu repartidor", preview, tag: "chat-driver"),

            "driver" when !string.IsNullOrEmpty(routeToken) =>
                SendNotificationToDriverAsync(routeToken, $"🌸 Mensaje de {senderName}", preview, tag: "chat-client"),

            "admin" =>
                SendNotificationToAdminsAsync("💬 Mensaje del chofer", preview, tag: "chat-admin"),

            _ => Task.CompletedTask
        };
    }

    // ═══════════════════════════════════════════
    //  FCM — APP ANDROID NATIVA
    // ═══════════════════════════════════════════

    /// <summary>
    /// Notifica a TODOS los repartidores Android que hay una nueva ruta disponible.
    /// </summary>
    public async Task NotifyDriversNewRouteAsync(string routeName, string driverToken, int deliveryCount)
    {
        var allTokens = await _db.FcmTokens
            .Where(t => t.Role == "driver")
            .Select(t => new { t.Token, t.DriverRouteToken })
            .ToListAsync();

        _logger.LogInformation("FCM nueva ruta [{Route}]: {Total} tokens de driver en BD. RouteToken={DriverToken}",
            routeName, allTokens.Count, driverToken);

        if (allTokens.Count == 0)
        {
            _logger.LogWarning("FCM nueva ruta: no hay tokens de driver registrados. El repartidor debe abrir la app para registrarse.");
            return;
        }

        // Primero intentar notificación dirigida al driver específico de esta ruta
        var targetedTokens = allTokens.Where(t => t.DriverRouteToken == driverToken).Select(t => t.Token).ToList();
        var broadcastTokens = allTokens.Select(t => t.Token).ToList();

        _logger.LogInformation("FCM nueva ruta: {Targeted} token(s) con RouteToken coincidente, {Total} en broadcast.",
            targetedTokens.Count, broadcastTokens.Count);

        await _fcm.SendToTokensAsync(
            broadcastTokens, // broadcast a todos los drivers registrados
            title: "🚗 Nueva ruta asignada",
            body: $"{routeName} — {deliveryCount} entregas listas para iniciar",
            data: new Dictionary<string, string>
            {
                { "type", "new_route" },
                { "driverToken", driverToken },
                { "deliveryCount", deliveryCount.ToString() }
            }
        );
    }

    /// <summary>
    /// Notifica al chofer de una ruta específica por su token de ruta.
    /// </summary>
    public async Task NotifyDriverFcmAsync(string driverRouteToken, string title, string body, Dictionary<string, string>? data = null)
    {
        var tokens = await _db.FcmTokens
            .Where(t => t.Role == "driver" && t.DriverRouteToken == driverRouteToken)
            .Select(t => t.Token)
            .ToListAsync();

        foreach (var token in tokens)
        {
            await _fcm.SendToTokenAsync(token, title, body, data);
        }
    }

    // ═══════════════════════════════════════════
    //  CORE DE ENVÍO
    // ═══════════════════════════════════════════

    private async Task SendToSubscriptionsAsync(List<PushSubscriptionModel> subscriptions, string title, string message, string? url, string? tag = null)
    {
        if (!subscriptions.Any()) return;

        var subject = _config["VapidDetails:Subject"] ?? "mailto:info@regibazar.com";
        var publicKey = _config["VapidDetails:PublicKey"];
        var privateKey = _config["VapidDetails:PrivateKey"];

        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(privateKey))
        {
            _logger.LogWarning("VAPID Keys not configured in appsettings.json.");
            return;
        }

        var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
        var webPushClient = new WebPushClient();

        var jsonPayload = JsonSerializer.Serialize(new
        {
            notification = new
            {
                title,
                body = message,
                icon = "/assets/icons/icon-192x192.png",
                badge = "/assets/icons/icon-72x72.png",
                vibrate = new[] { 200, 100, 200 },
                tag = tag ?? "general",
                data = new { url = url ?? "/" }
            }
        });

        foreach (var sub in subscriptions)
        {
            try
            {
                var pushSubscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await webPushClient.SendNotificationAsync(pushSubscription, jsonPayload, vapidDetails);
                sub.LastUsedAt = DateTime.UtcNow;
            }
            catch (WebPushException exception)
            {
                var statusCode = exception.StatusCode;
                if (statusCode == System.Net.HttpStatusCode.Gone || statusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _db.PushSubscriptions.Remove(sub);
                }
                _logger.LogWarning($"Push failed for Endpoint {sub.Endpoint}: {exception.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending push notification.");
            }
        }

        await _db.SaveChangesAsync();
    }
}
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

    // Helpers específicos
    Task NotifyClientDriverEnRouteAsync(int clientId, string? driverName = null);
    Task NotifyClientDriverNearbyAsync(int clientId, int distanceMeters);
    Task NotifyClientDeliveredAsync(int clientId);
    Task NotifyChatMessageAsync(string targetRole, int? clientId, string? routeToken, string senderName, string messageText);
}

public class PushNotificationService : IPushNotificationService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<PushNotificationService> _logger;

    public PushNotificationService(AppDbContext db, IConfiguration config, ILogger<PushNotificationService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
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
using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using WebPush;
using System.Text.Json;

namespace EntregasApi.Services;

public interface IPushNotificationService
{
    Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null);
    Task SendNotificationToAdminsAsync(string title, string message, string? url = null);
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

    public async Task SendNotificationToClientAsync(int clientId, string title, string message, string? url = null)
    {
        var subscriptions = await _db.PushSubscriptions
            .Where(s => s.ClientId == clientId)
            .ToListAsync();

        await SendToSubscriptionsAsync(subscriptions, title, message, url);
    }

    public async Task SendNotificationToAdminsAsync(string title, string message, string? url = null)
    {
        var subscriptions = await _db.PushSubscriptions
            .Where(s => s.UserId != null) // admin = user (or you can use your own logic)
            .ToListAsync();

        await SendToSubscriptionsAsync(subscriptions, title, message, url);
    }

    private async Task SendToSubscriptionsAsync(List<PushSubscriptionModel> subscriptions, string title, string message, string? url)
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
                vibrate = new[] { 100, 50, 100 },
                data = new { url = url ?? "/" }
            }
        });

        foreach (var sub in subscriptions)
        {
            try
            {
                var pushSubscription = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await webPushClient.SendNotificationAsync(pushSubscription, jsonPayload, vapidDetails);
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

        await _db.SaveChangesAsync(); // clean up dead endpoints
    }
}

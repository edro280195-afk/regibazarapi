using EntregasApi.Data;
using EntregasApi.DTOs;
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
    
    // We expect the VAPID details in appsettings.json:
    // "VapidDetails": {
    //    "Subject": "mailto:test@example.com",
    //    "PublicKey": "...",
    //    "PrivateKey": "..."
    // }

    public PushController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [HttpPost("subscribe")]
    [AllowAnonymous] // Allowing anon to subscribe for now, optionally require auth
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest req)
    {
        // Extract clientId from Authorization header if present, or from req
        var existing = await _db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint);

        if (existing == null)
        {
            var sub = new PushSubscriptionModel
            {
                Endpoint = req.Endpoint,
                P256dh = req.Keys.P256dh,
                Auth = req.Keys.Auth,
            };

            // If it's a client, link it
            if (req.ClientId.HasValue) 
            {
                sub.ClientId = req.ClientId;
            }

            _db.PushSubscriptions.Add(sub);
        }
        else
        {
            // update keys
            existing.P256dh = req.Keys.P256dh;
            existing.Auth = req.Keys.Auth;
            if (req.ClientId.HasValue) existing.ClientId = req.ClientId;
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

    [HttpPost("test")]
    [Authorize] // Only admins can test broadcast for now
    public async Task<IActionResult> TestNotification([FromBody] NotificationPayload payload)
    {
        var subscriptions = await _db.PushSubscriptions.ToListAsync();
        if (!subscriptions.Any()) return NotFound("No hay suscripciones activas.");

        var subject = _config["VapidDetails:Subject"] ?? "mailto:info@regibazar.com";
        var publicKey = _config["VapidDetails:PublicKey"];
        var privateKey = _config["VapidDetails:PrivateKey"];

        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(privateKey))
            return StatusCode(500, "VAPID Keys no configuradas en el servidor");

        var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
        var webPushClient = new WebPushClient();

        int successCount = 0;
        int failCount = 0;

        foreach (var sub in subscriptions)
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
                        icon = "/assets/icons/icon-192x192.png",
                        vibrate = new[] { 100, 50, 100 },
                        data = new { url = payload.Url ?? "/" }
                    }
                });

                await webPushClient.SendNotificationAsync(pushSubscription, jsonPayload, vapidDetails);
                successCount++;
            }
            catch (WebPushException exception)
            {
                var statusCode = exception.StatusCode;
                if (statusCode == System.Net.HttpStatusCode.Gone || statusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Subscription has expired or is no longer valid
                    _db.PushSubscriptions.Remove(sub);
                }
                failCount++;
            }
            catch (Exception)
            {
                failCount++;
            }
        }
        
        await _db.SaveChangesAsync();

        return Ok(new { success = successCount, failed = failCount });
    }
}

public class PushSubscriptionRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public PushKeys Keys { get; set; } = new();
    public int? ClientId { get; set; }
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
    public string? Url { get; set; }
}

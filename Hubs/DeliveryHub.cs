using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.Models;

namespace EntregasApi.Hubs;

public class DeliveryHub : Hub
{
    private readonly AppDbContext _db;

    public DeliveryHub(AppDbContext db)
    {
        _db = db;
    }

    // ─── ADMIN ───
    public async Task JoinAdminGroup()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "Admins");
    }

    // ─── DRIVER ───
    public async Task JoinRoute(string driverToken)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Route_{driverToken}");
    }

    // Reports location to Admins and Clients watching the same route
    public async Task ReportLocation(string driverToken, double lat, double lng)
    {
        // Broadcast to admins
        await Clients.Group("Admins").SendAsync("ReceiveLocation", driverToken, lat, lng);
        
        // Broadcast to clients watching this route
        await Clients.Group($"Tracking_{driverToken}").SendAsync("LocationUpdate", new { latitude = lat, longitude = lng });
    }

    // ─── CLIENT ───
    public async Task JoinOrder(string accessToken)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Order_{accessToken}");
        
        // Find if this order is in a route to also subscribe to route GPS
        var order = await _db.Orders
            .Include(o => o.Delivery)
            .ThenInclude(d => d.DeliveryRoute)
            .FirstOrDefaultAsync(o => o.AccessToken == accessToken);

        if (order?.Delivery?.DeliveryRoute != null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Tracking_{order.Delivery.DeliveryRoute.DriverToken}");
        }
    }

    // ─── CHAT ───
    public async Task SendMessage(string groupName, object message)
    {
        await Clients.Group(groupName).SendAsync("ReceiveMessage", message);
    }
}

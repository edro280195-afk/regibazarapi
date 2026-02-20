using Microsoft.AspNetCore.SignalR;

namespace EntregasApi.Hubs;

public class TrackingHub : Hub
{
    // 1. Clientas se unen para ver GPS
    public async Task JoinOrder(string accessToken)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"order_{accessToken}");
    }

    // 2. Admin se une para escuchar notificaciones globales
    public async Task JoinAdminGroup()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "Admins");
    }

    // 3. Chofer/Admin se unen al radio de una ruta específica
    public async Task JoinRoute(string driverToken)
    {
        // Nota: Asegúrate de que tu Angular manda llamar 'JoinRoute', no 'JoinRouteGroup'
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Route_{driverToken}");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
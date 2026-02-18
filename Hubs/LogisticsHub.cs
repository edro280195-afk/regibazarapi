using Microsoft.AspNetCore.SignalR;

namespace EntregasApi.Hubs;

public class LogisticsHub : Hub
{
    // El chofer o el admin se unen al grupo de una ruta específica para escuchar eventos
    public async Task JoinRouteGroup(string routeId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Route_{routeId}");
    }

    // El chofer sale del grupo (opcional, SignalR lo maneja al desconectar, pero útil para control)
    public async Task LeaveRouteGroup(string routeId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Route_{routeId}");
    }
}
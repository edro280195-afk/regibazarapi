using Microsoft.AspNetCore.SignalR;

namespace EntregasApi.Hubs;

public class TrackingHub : Hub
{
    /// <summary>
    /// Las clientas se suscriben a su pedido para recibir updates de GPS.
    /// Se llama desde el frontend: connection.invoke("JoinOrder", accessToken)
    /// </summary>
    public async Task JoinOrder(string accessToken)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"order_{accessToken}");
    }

    public async Task LeaveOrder(string accessToken)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"order_{accessToken}");
    }

    /// <summary>
    /// El panel admin se suscribe para ver todas las actualizaciones.
    /// </summary>
    public async Task JoinAdmin()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "admin");
    }

    /// <summary>
    /// El repartidor se suscribe a su ruta.
    /// </summary>
    public async Task JoinRoute(string driverToken)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"route_{driverToken}");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}

using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace EntregasApi.Hubs;

public class PosHub : Hub
{
    public async Task JoinOrderGroup(int orderId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"order_{orderId}");
    }

    public async Task LeaveOrderGroup(int orderId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"order_{orderId}");
    }

    public async Task JoinNodrizaGroup()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "PosNodriza");
    }
}

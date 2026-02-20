using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace EntregasApi.Hubs
{
    public class OrderHub : Hub
    {
        // 1. El panel de tu esposa llamará a esto al abrir la página
        public async Task JoinAdminGroup()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "Admins");
        }

        // 2. (Opcional) La clienta podría unirse a un grupo con su token 
        // por si el admin le quiere mandar un mensaje de regreso.
        public async Task JoinOrderGroup(string accessToken)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"order_{accessToken}");
        }
    }
}

using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using EntregasApi.Hubs;
using EntregasApi.DTOs;

namespace EntregasApi.Services;

public class PosService : IPosService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<PosHub> _hub;

    public PosService(AppDbContext db, IHubContext<PosHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    public async Task<Order> ScanItemAsync(int orderId, string sku)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) throw new Exception("Orden no encontrada");

        var product = await _db.Products.FirstOrDefaultAsync(p => p.SKU == sku);
        if (product == null) throw new Exception("Producto no encontrado");
        if (!product.IsActive) throw new Exception("Producto inactivo");
        if (product.Stock <= 0) throw new Exception("Sin stock disponible");

        var existingItem = order.Items.FirstOrDefault(i => i.ProductId == product.Id);
        if (existingItem != null)
        {
            existingItem.Quantity += 1;
            existingItem.LineTotal = existingItem.Quantity * existingItem.UnitPrice;
        }
        else
        {
            order.Items.Add(new OrderItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = 1,
                UnitPrice = product.Price,
                LineTotal = product.Price
            });
        }

        order.Subtotal = order.Items.Sum(i => i.LineTotal);
        if (order.OrderType == OrderType.POS_Tienda) order.ShippingCost = 0;
        order.Total = Math.Max(0, order.Subtotal + order.ShippingCost - order.DiscountAmount);

        await _db.SaveChangesAsync();

        // Actualizar Satélites (Celulares)
        await _hub.Clients.Group($"order_{orderId}").SendAsync("ItemAddedToOrder", new {
            OrderId = order.Id,
            Sku = sku,
            Total = order.Total,
            ProductName = product.Name,
            Quantity = existingItem != null ? existingItem.Quantity : 1
        });

        // Actualizar Nodriza (iPad)
        await _hub.Clients.Group("PosNodriza").SendAsync("OrderCreated", new {
            Id = order.Id,
            ClientId = order.ClientId,
            ClientName = order.Client?.Name,
            OrderType = (int)order.OrderType,
            Status = (int)order.Status,
            Subtotal = order.Subtotal,
            ShippingCost = order.ShippingCost,
            DiscountAmount = order.DiscountAmount,
            Total = order.Total,
            CreatedAt = order.CreatedAt,
            Items = order.Items.Select(i => new {
                i.Id,
                i.OrderId,
                i.ProductId,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.LineTotal
            }).ToList()
        });

        return order;
    }

    public async Task<Order> RemoveItemAsync(int orderItemId)
    {
        var item = await _db.OrderItems
            .Include(i => i.Order)
            .ThenInclude(o => o.Items)
            .FirstOrDefaultAsync(i => i.Id == orderItemId);

        if (item == null) throw new Exception("Item no encontrado");

        var order = item.Order;
        order.Items.Remove(item);

        order.Subtotal = order.Items.Sum(i => i.LineTotal);
        if (order.OrderType == OrderType.POS_Tienda) order.ShippingCost = 0;
        order.Total = Math.Max(0, order.Subtotal + order.ShippingCost - order.DiscountAmount);

        await _db.SaveChangesAsync();

        await _hub.Clients.Group($"order_{order.Id}").SendAsync("ItemRemovedFromOrder", new {
            OrderId = order.Id,
            OrderItemId = orderItemId,
            Total = order.Total
        });

        await _hub.Clients.Group("PosNodriza").SendAsync("OrderCreated", new {
            Id = order.Id,
            ClientId = order.ClientId,
            ClientName = order.Client?.Name,
            OrderType = (int)order.OrderType,
            Status = (int)order.Status,
            Subtotal = order.Subtotal,
            ShippingCost = order.ShippingCost,
            DiscountAmount = order.DiscountAmount,
            Total = order.Total,
            CreatedAt = order.CreatedAt,
            Items = order.Items.Select(i => new {
                i.Id,
                i.OrderId,
                i.ProductId,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.LineTotal
            }).ToList()
        });

        return order;
    }

    public async Task<CashRegisterSession> OpenSessionAsync(int userId, decimal initialCash)
    {
        var openSession = await _db.CashRegisterSessions
            .FirstOrDefaultAsync(s => s.Status == SessionStatus.Open);
        
        if (openSession != null) return openSession;

        var session = new CashRegisterSession
        {
            UserId = userId,
            InitialCash = initialCash,
            OpeningTime = DateTime.UtcNow,
            Status = SessionStatus.Open
        };

        _db.CashRegisterSessions.Add(session);
        await _db.SaveChangesAsync();

        return session;
    }

    public async Task<CashRegisterSession?> GetActiveSessionAsync()
    {
        return await _db.CashRegisterSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Status == SessionStatus.Open);
    }

    public async Task<List<Order>> GetPendingOrdersAsync()
    {
        var activeSession = await GetActiveSessionAsync();
        
        var query = _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Where(o => o.Status == OrderStatus.Pending && o.OrderType == OrderType.POS_Tienda);

        if (activeSession != null)
        {
            // Solo mostramos pedidos capturados "al momento" (desde que se abrió la caja)
            query = query.Where(o => o.CreatedAt >= activeSession.OpeningTime);
        }

        return await query
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }

    public async Task<Order> CreatePosOrderAsync(string clientName)
    {
        var activeSession = await GetActiveSessionAsync();
        if (activeSession == null) throw new Exception("No hay una sesión de caja abierta");

        // 1. Buscar o crear cliente
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Name.ToLower() == clientName.ToLower());
        if (client == null)
        {
            client = new Client { Name = clientName, Tag = ClientTag.None };
            _db.Clients.Add(client);
            await _db.SaveChangesAsync();
        }

        // 2. Crear la orden
        var order = new Order
        {
            ClientId = client.Id,
            Client = client,
            OrderType = OrderType.POS_Tienda,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            Subtotal = 0,
            Total = 0,
            ShippingCost = 0, // En tienda no hay envío
            AccessToken = Guid.NewGuid().ToString("N").Substring(0, 12),
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // 3. Notificar a la Nodriza (iPad)
        await _hub.Clients.Group("PosNodriza").SendAsync("OrderCreated", new {
            Id = order.Id,
            ClientId = client.Id,
            ClientName = client.Name,
            OrderType = (int)order.OrderType,
            Status = (int)order.Status,
            Subtotal = order.Subtotal,
            ShippingCost = order.ShippingCost,
            DiscountAmount = order.DiscountAmount,
            Total = order.Total,
            CreatedAt = order.CreatedAt,
            Items = new List<object>() // Orden nueva, sin items aún
        });

        return order;
    }

    public async Task<Order> AddManualItemAsync(int orderId, string productName, decimal price, int quantity)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.Client)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) throw new Exception("Orden no encontrada");

        order.Items.Add(new OrderItem
        {
            ProductName = productName,
            Quantity = quantity,
            UnitPrice = price,
            LineTotal = price * quantity
        });

        order.Subtotal = order.Items.Sum(i => i.LineTotal);
        if (order.OrderType == OrderType.POS_Tienda) order.ShippingCost = 0;
        order.Total = Math.Max(0, order.Subtotal + order.ShippingCost - order.DiscountAmount);

        await _db.SaveChangesAsync();

        await NotifyNodrizaAsync(order);
        return order;
    }

    public async Task<Order> RemoveItemByNameAsync(int orderId, string productName)
    {
        var order = await _db.Orders.Include(o => o.Items).Include(o => o.Client).FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null) throw new Exception("Orden no encontrada");

        var item = order.Items.OrderByDescending(i => i.Id)
            .FirstOrDefault(i => i.ProductName.ToLower().Contains(productName.ToLower()));

        if (item != null)
        {
            order.Items.Remove(item);
            order.Subtotal = order.Items.Sum(i => i.LineTotal);
            if (order.OrderType == OrderType.POS_Tienda) order.ShippingCost = 0;
            order.Total = Math.Max(0, order.Subtotal + order.ShippingCost - order.DiscountAmount);
            await _db.SaveChangesAsync();
            await NotifyNodrizaAsync(order);
        }

        return order;
    }

    public async Task<Order> ClearPosOrderAsync(int orderId)
    {
        var order = await _db.Orders.Include(o => o.Items).Include(o => o.Client).FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null) throw new Exception("Orden no encontrada");

        order.Items.Clear();
        order.Subtotal = 0;
        order.DiscountAmount = 0;
        if (order.OrderType == OrderType.POS_Tienda) order.ShippingCost = 0;
        order.Total = 0;

        await _db.SaveChangesAsync();
        await NotifyNodrizaAsync(order);
        return order;
    }

    public async Task<Order> ApplyDiscountAsync(int orderId, decimal discountAmount)
    {
        var order = await _db.Orders.Include(o => o.Items).Include(o => o.Client).FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null) throw new Exception("Orden no encontrada");

        order.DiscountAmount = discountAmount;
        order.Total = Math.Max(0, order.Subtotal + order.ShippingCost - order.DiscountAmount);

        await _db.SaveChangesAsync();
        await NotifyNodrizaAsync(order);
        return order;
    }

    private async Task NotifyNodrizaAsync(Order order)
    {
        await _hub.Clients.Group("PosNodriza").SendAsync("OrderCreated", new {
            Id = order.Id,
            ClientId = order.ClientId,
            ClientName = order.Client?.Name,
            OrderType = (int)order.OrderType,
            Status = (int)order.Status,
            Subtotal = order.Subtotal,
            ShippingCost = order.ShippingCost,
            DiscountAmount = order.DiscountAmount,
            Total = order.Total,
            CreatedAt = order.CreatedAt,
            Items = order.Items.Select(i => new {
                i.Id,
                i.OrderId,
                i.ProductId,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.LineTotal
            }).ToList()
        });
    }


    public async Task<CashRegisterSession> CloseSessionAsync(int sessionId, decimal actualCash)
    {
        var session = await _db.CashRegisterSessions
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null) throw new Exception("Sesión no encontrada");
        if (session.Status == SessionStatus.Closed) throw new Exception("La sesión ya está cerrada");

        var cashPayments = session.Payments.Where(p => p.Method == "Efectivo").Sum(p => p.Amount);
        
        session.FinalCashExpected = session.InitialCash + cashPayments;
        session.FinalCashActual = actualCash;
        session.ClosingTime = DateTime.UtcNow;
        session.Status = SessionStatus.Closed;

        await _db.SaveChangesAsync();

        return session;
    }

    public async Task<OrderPayment> PayPosOrderAsync(int orderId, int sessionId, decimal amountReceived, string paymentMethod)
    {
        var session = await _db.CashRegisterSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.Status == SessionStatus.Open);
        if (session == null) throw new Exception("Sesión de caja no válida o cerrada");

        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        
        if (order == null) throw new Exception("Orden no encontrada");

        // Regla de Negocio: Descuento de inventario en el momento del pago
        foreach(var item in order.Items)
        {
            if (item.ProductId.HasValue)
            {
                var product = await _db.Products.FindAsync(item.ProductId.Value);
                if (product != null)
                {
                    if (product.Stock < item.Quantity)
                        throw new Exception($"Stock insuficiente para {product.Name}");

                    product.Stock -= item.Quantity;
                }
            }
        }

        var payment = new OrderPayment
        {
            OrderId = orderId,
            CashRegisterSessionId = sessionId,
            Amount = amountReceived,
            Method = paymentMethod,
            Date = DateTime.UtcNow,
            RegisteredBy = "POS_Tienda"
        };

        _db.OrderPayments.Add(payment);

        order.Status = OrderStatus.Delivered; // Al pagarse en tienda se asume entregada

        await _db.SaveChangesAsync();

        await _hub.Clients.Group("PosNodriza").SendAsync("OrderPaid", new {
            OrderId = order.Id,
            Amount = amountReceived,
            Method = paymentMethod
        });

        return payment;
    }
}

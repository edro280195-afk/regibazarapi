using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using GenAiClient = Google.GenAI.Client;

namespace EntregasApi.Services;

public interface ICamiService
{
    Task<string> ChatAsync(CamiChatRequest request);
}

public class CamiService : ICamiService
{
    private readonly AppDbContext _db;
    private readonly GenAiClient _gemini;
    private readonly ILogger<CamiService> _logger;

    private const string MODEL = "gemini-2.5-flash";

    private const string SYSTEM_INSTRUCTION = @"
Eres C.A.M.I., la asistente inteligente de Regi Bazar. Tienes acceso completo al sistema ERP del negocio.
Puedes consultar y operar: pedidos, clientas, rutas, finanzas, proveedores y lealtad.

REGLAS DE CONDUCTA:
- Respuestas cortas, empáticas, sin viñetas, sin markdown, directas al grano.
- Habla en español mexicano, tono amigable y profesional, como una asistente ejecutiva muy capaz.
- Antes de crear o modificar datos importantes, confirma brevemente lo que vas a hacer.
- Si no encuentras algo, dilo con naturalidad y ofrece alternativas.
- Cuando muestres datos, sé selectiva: solo lo más relevante, no vuelques toda la data.
- Si el usuario menciona una clienta por apodo o nombre incompleto, usa buscar_pedidos o listar_clientas para encontrarla. No asumas IDs si no estás segura, mejor busca.
- El sistema ahora cuenta con búsqueda difusa (fuzzy search), por lo que si el nombre no coincide exactamente, el sistema intentará encontrar la mejor coincidencia.
- Estados de pedidos: Pending=Pendiente, Confirmed=Confirmado, InRoute=En Ruta, Delivered=Entregado, NotDelivered=No Entregado, Canceled=Cancelado, Postponed=Pospuesto.
- Tipos de pedido: Delivery=A domicilio, PickUp=Recoger en tienda.
- Tipos de clienta: Nueva (expira próximo lunes), Frecuente (expira en 2 semanas).
- Métodos de pago: Efectivo, Transferencia, Deposito, Tarjeta.
- Para cancelar o posponer un pedido, el motivo es obligatorio.
- Al entregar un pedido, se otorgan puntos de lealtad: Total / 10.
- Los envíos a domicilio tienen costo de 60 MXN por defecto. PickUp es gratis.
- El cargo por envío se puede personalizar.
- El sistema usa hora de Ciudad de México (CST).
";

    // ── DEFINICIÓN DE HERRAMIENTAS (MODO ESTRICTO) ──────────────────────────
    private static readonly List<Tool> TOOLS = new()
    {
        new Tool
        {
            FunctionDeclarations = new List<FunctionDeclaration>
            {
                // ─ CONSULTAS ─
                new FunctionDeclaration
                {
                    Name = "consultar_resumen_negocio",
                    Description = "Obtiene el resumen general del negocio...",
                    Parameters = new Schema { Type = "OBJECT", Properties = new Dictionary<string, Schema>() }
                },
                new FunctionDeclaration
                {
                    Name = "buscar_pedidos",
                    Description = "Busca y filtra pedidos del sistema...",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "estado", new Schema { Type = "STRING", Description = "Filtro por estado: Pending, Confirmed, InRoute, Delivered, NotDelivered, Canceled, Postponed." } },
                            { "busqueda", new Schema { Type = "STRING", Description = "Texto a buscar..." } },
                            { "limite", new Schema { Type = "INTEGER", Description = "Máximo de resultados." } }
                        }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "obtener_pedido",
                    Description = "Obtiene los detalles completos de un pedido específico por su ID...",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "id", new Schema { Type = "INTEGER", Description = "ID numérico del pedido." } }
                        },
                        Required = new List<string> { "id" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "listar_clientas",
                    Description = "Lista las clientas registradas...",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "busqueda", new Schema { Type = "STRING", Description = "Nombre o teléfono..." } },
                            { "limite", new Schema { Type = "INTEGER", Description = "Máximo de resultados." } }
                        }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "obtener_clienta",
                    Description = "Obtiene los detalles de una clienta...",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "id", new Schema { Type = "INTEGER", Description = "ID de la clienta." } }
                        },
                        Required = new List<string> { "id" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "listar_rutas",
                    Description = "Lista las rutas de reparto recientes...",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "limite", new Schema { Type = "INTEGER", Description = "Máximo de rutas." } }
                        }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "consultar_finanzas",
                    Description = "Consulta el reporte financiero...",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "fecha_inicio", new Schema { Type = "STRING", Description = "YYYY-MM-DD" } },
                            { "fecha_fin", new Schema { Type = "STRING", Description = "YYYY-MM-DD" } }
                        },
                        Required = new List<string> { "fecha_inicio", "fecha_fin" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "listar_proveedores",
                    Description = "Lista los proveedores...",
                    Parameters = new Schema { Type = "OBJECT", Properties = new Dictionary<string, Schema>() }
                },
                new FunctionDeclaration
                {
                    Name = "consultar_lealtad",
                    Description = "Consulta los puntos de lealtad...",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "clienta_id", new Schema { Type = "INTEGER", Description = "ID de la clienta." } }
                        },
                        Required = new List<string> { "clienta_id" }
                    }
                },

                // ─ ACCIONES ─
                new FunctionDeclaration
                {
                    Name = "crear_pedido",
                    Description = "Crea un nuevo pedido...",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "nombre_clienta", new Schema { Type = "STRING", Description = "Nombre completo..." } },
                            { "telefono", new Schema { Type = "STRING", Description = "Teléfono..." } },
                            { "direccion", new Schema { Type = "STRING", Description = "Dirección..." } },
                            { "tipo_clienta", new Schema { Type = "STRING", Description = "Nueva o Frecuente." } },
                            { "tipo_envio", new Schema { Type = "STRING", Description = "Delivery o PickUp." } },
                            { "costo_envio", new Schema { Type = "NUMBER", Description = "Costo de envío en MXN." } },
                            { "items", new Schema
                                {
                                    Type = "ARRAY",
                                    Description = "Lista de productos.",
                                    Items = new Schema
                                    {
                                        Type = "OBJECT",
                                        Properties = new Dictionary<string, Schema>
                                        {
                                            { "producto", new Schema { Type = "STRING" } },
                                            { "cantidad", new Schema { Type = "INTEGER" } },
                                            { "precio", new Schema { Type = "NUMBER" } }
                                        },
                                        Required = new List<string> { "producto", "cantidad", "precio" }
                                    }
                                }
                            }
                        },
                        Required = new List<string> { "nombre_clienta", "items" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "agregar_item_pedido",
                    Description = "Agrega un producto a un pedido existente.",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "pedido_id", new Schema { Type = "INTEGER" } },
                            { "producto", new Schema { Type = "STRING" } },
                            { "cantidad", new Schema { Type = "INTEGER" } },
                            { "precio", new Schema { Type = "NUMBER" } }
                        },
                        Required = new List<string> { "pedido_id", "producto", "cantidad", "precio" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "cambiar_estado_pedido",
                    Description = "Cambia el estado de un pedido.",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "pedido_id", new Schema { Type = "INTEGER" } },
                            { "estado", new Schema { Type = "STRING", Description = "Pending, Confirmed, InRoute, Delivered, NotDelivered, Canceled, Postponed." } },
                            { "motivo", new Schema { Type = "STRING" } },
                            { "fecha_postergacion", new Schema { Type = "STRING" } }
                        },
                        Required = new List<string> { "pedido_id", "estado" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "registrar_pago",
                    Description = "Registra un pago para un pedido específico.",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "pedido_id", new Schema { Type = "INTEGER" } },
                            { "monto", new Schema { Type = "NUMBER" } },
                            { "metodo", new Schema { Type = "STRING", Description = "Efectivo, Transferencia, Deposito o Tarjeta." } },
                            { "notas", new Schema { Type = "STRING" } }
                        },
                        Required = new List<string> { "pedido_id", "monto", "metodo" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "crear_clienta",
                    Description = "Registra una nueva clienta.",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "nombre", new Schema { Type = "STRING" } },
                            { "telefono", new Schema { Type = "STRING" } },
                            { "direccion", new Schema { Type = "STRING" } },
                            { "tipo", new Schema { Type = "STRING", Description = "Nueva o Frecuente." } }
                        },
                        Required = new List<string> { "nombre" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "crear_ruta",
                    Description = "Crea una nueva ruta de reparto asignando pedidos.",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "ids_pedidos", new Schema
                                {
                                    Type = "ARRAY",
                                    Items = new Schema { Type = "INTEGER" }
                                }
                            }
                        },
                        Required = new List<string> { "ids_pedidos" }
                    }
                },
                new FunctionDeclaration
                {
                    Name = "liquidar_ruta",
                    Description = "Completa/liquida una ruta de reparto.",
                    Parameters = new Schema
                    {
                        Type = "OBJECT",
                        Properties = new Dictionary<string, Schema>
                        {
                            { "ruta_id", new Schema { Type = "INTEGER" } }
                        },
                        Required = new List<string> { "ruta_id" }
                    }
                }
            }
        }
    };

    public CamiService(AppDbContext db, IConfiguration config, ILogger<CamiService> logger)
    {
        _db = db;
        _logger = logger;
        var apiKey = config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Falta Gemini:ApiKey en appsettings.json");
        _gemini = new GenAiClient(apiKey: apiKey);
    }

    // ── BUCLE PRINCIPAL DE CONVERSACIÓN ─────────────────────────────────────
    public async Task<string> ChatAsync(CamiChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewMessage))
            return "No te escuché bien. ¿Me repites?";

        var config = new GenerateContentConfig
        {
            SystemInstruction = new Content
            {
                Role = "system",
                Parts = new List<Part> { new Part { Text = SYSTEM_INSTRUCTION } }
            },
            Tools = TOOLS,
            Temperature = 0.7f
        };

        // Construir historial completo
        var allContents = new List<Content>();
        foreach (var msg in request.History)
        {
            allContents.Add(new Content
            {
                Role = msg.Role,
                Parts = new List<Part> { new Part { Text = msg.Text } }
            });
        }
        allContents.Add(new Content
        {
            Role = "user",
            Parts = new List<Part> { new Part { Text = request.NewMessage } }
        });

        // Loop de function calling (máx. 5 rondas para evitar loops infinitos)
        for (int round = 0; round < 5; round++)
        {
            var response = await _gemini.Models.GenerateContentAsync(MODEL, allContents, config);
            var candidate = response.Candidates?[0];
            var modelContent = candidate?.Content;

            if (modelContent == null)
                return "No tengo una respuesta en este momento. Inténtalo de nuevo.";

            var functionCallParts = modelContent.Parts?
                .Where(p => p.FunctionCall != null)
                .ToList() ?? new List<Part>();

            // Sin function calls → respuesta final de texto
            if (functionCallParts.Count == 0)
                return response.Text?.Trim() ?? "Listo.";

            // Añadir la respuesta del modelo (con function calls) al historial
            allContents.Add(modelContent);

            // Ejecutar cada función y recopilar resultados
            var resultParts = new List<Part>();
            foreach (var fcPart in functionCallParts)
            {
                var fc = fcPart.FunctionCall!;
                _logger.LogInformation("CAMI ejecutando función: {FnName}", fc.Name);

                JsonElement functionResult;
                try
                {
                    // Convert Dictionary to JsonElement for ExecuteFunctionAsync
                    var argsElement = fc.Args != null ? ToJson(fc.Args) : (JsonElement?)null;
                    functionResult = await ExecuteFunctionAsync(fc.Name!, argsElement);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error ejecutando {FnName}", fc.Name);
                    functionResult = ToJson(new { error = ex.Message });
                }

                resultParts.Add(new Part
                {
                    FunctionResponse = new FunctionResponse
                    {
                        Name = fc.Name!,
                        // Convert JsonElement back to Dictionary for FunctionResponse
                        Response = JsonSerializer.Deserialize<Dictionary<string, object>>(functionResult.GetRawText())!
                    }
                });
            }

            // Añadir resultados al historial como turno "user"
            allContents.Add(new Content
            {
                Role = "user",
                Parts = resultParts
            });
        }

        return "Alcancé el límite de operaciones en esta consulta. Por favor repite tu solicitud.";
    }

    // ── DESPACHADOR DE FUNCIONES ─────────────────────────────────────────────
    private async Task<JsonElement> ExecuteFunctionAsync(string name, JsonElement? args)
    {
        return name switch
        {
            "consultar_resumen_negocio" => await ConsultarResumenNegocioAsync(),
            "buscar_pedidos"            => await BuscarPedidosAsync(args),
            "obtener_pedido"            => await ObtenerPedidoAsync(args),
            "listar_clientas"           => await ListarClientasAsync(args),
            "obtener_clienta"           => await ObtenerClientaAsync(args),
            "listar_rutas"              => await ListarRutasAsync(args),
            "consultar_finanzas"        => await ConsultarFinanzasAsync(args),
            "listar_proveedores"        => await ListarProveedoresAsync(),
            "consultar_lealtad"         => await ConsultarLealtadAsync(args),
            "crear_pedido"              => await CrearPedidoAsync(args),
            "agregar_item_pedido"       => await AgregarItemPedidoAsync(args),
            "cambiar_estado_pedido"     => await CambiarEstadoPedidoAsync(args),
            "registrar_pago"            => await RegistrarPagoAsync(args),
            "crear_clienta"             => await CrearClientaAsync(args),
            "crear_ruta"                => await CrearRutaAsync(args),
            "liquidar_ruta"             => await LiquidarRutaAsync(args),
            _                           => ToJson(new { error = $"Función desconocida: {name}" })
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FUNCIONES DE CONSULTA (READ)
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<JsonElement> ConsultarResumenNegocioAsync()
    {
        var mexicoZone = GetMexicoZone();
        var nowMx = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoZone);
        var todayStart = TimeZoneInfo.ConvertTimeToUtc(nowMx.Date, mexicoZone);
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(nowMx.Year, nowMx.Month, 1), mexicoZone);

        var orders = await _db.Orders
            .Include(o => o.Payments)
            .Include(o => o.Client)
            .Where(o => o.Status != OrderStatus.Canceled)
            .ToListAsync();

        var totalHoy = orders
            .Where(o => o.CreatedAt >= todayStart)
            .Sum(o => o.Total);

        var cobradoHoy = orders
            .SelectMany(o => o.Payments)
            .Where(p => p.Date >= todayStart)
            .Sum(p => p.Amount);

        var pendientes = orders.Count(o => o.Status == OrderStatus.Pending);
        var enRuta     = orders.Count(o => o.Status == OrderStatus.InRoute);
        var entregadosMes = orders.Count(o => o.Status == OrderStatus.Delivered && o.CreatedAt >= monthStart);
        var porCobrar  = orders
            .Where(o => o.Status is OrderStatus.Pending or OrderStatus.Confirmed or OrderStatus.InRoute)
            .Sum(o => o.BalanceDue);

        var totalClientes = await _db.Clients.CountAsync();
        var rutasActivas  = await _db.DeliveryRoutes.CountAsync(r => r.Status == RouteStatus.Active || r.Status == RouteStatus.Pending);

        return ToJson(new
        {
            fecha         = nowMx.ToString("dd/MM/yyyy HH:mm"),
            pedidosPendientes = pendientes,
            pedidosEnRuta = enRuta,
            entregadosEsteMes = entregadosMes,
            facturadoHoy  = totalHoy,
            cobradoHoy    = cobradoHoy,
            saldoPorCobrar = porCobrar,
            totalClientas = totalClientes,
            rutasActivas  = rutasActivas
        });
    }

    private async Task<JsonElement> BuscarPedidosAsync(JsonElement? args)
    {
        var estado  = GetStr(args, "estado");
        var busqueda = GetStr(args, "busqueda");
        var limite  = GetInt(args, "limite", 10);

        var query = _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .AsQueryable();

        if (!string.IsNullOrEmpty(estado) && Enum.TryParse<OrderStatus>(estado, out var statusEnum))
            query = query.Where(o => o.Status == statusEnum);

        if (!string.IsNullOrEmpty(busqueda))
        {
            var busqLower = busqueda.ToLower();
            if (int.TryParse(busqueda, out int orderId))
                query = query.Where(o => o.Id == orderId || o.Client.Name.ToLower().Contains(busqLower));
            else
                query = query.Where(o => o.Client.Name.ToLower().Contains(busqLower) ||
                                         (o.Client.Phone != null && o.Client.Phone.Contains(busqueda)));
        }

        var results = await query
            .OrderByDescending(o => o.CreatedAt)
            .Take(Math.Clamp(limite * 2, 1, 100)) // Tomamos un poco más para el fuzzy rank
            .Select(o => new
            {
                id       = o.Id,
                clienta  = o.Client.Name,
                telefono = o.Client.Phone,
                estado   = o.Status.ToString(),
                tipo     = o.OrderType.ToString(),
                total    = o.Total,
                pagado   = o.Payments.Sum(p => p.Amount) + o.AdvancePayment,
                saldo    = o.Total - (o.Payments.Sum(p => p.Amount) + o.AdvancePayment),
                items    = o.Items.Count,
                creado   = o.CreatedAt.ToString("dd/MM/yyyy")
            })
            .ToListAsync();

        // Aplicar ranking difuso si hay términos de búsqueda y pocos resultados exactos
        if (!string.IsNullOrEmpty(busqueda) && results.Count == 0)
        {
            var allOrders = await _db.Orders
                .Include(o => o.Client)
                .Where(o => o.Status != OrderStatus.Canceled)
                .OrderByDescending(o => o.CreatedAt)
                .Take(100)
                .ToListAsync();

            var fuzzyResults = allOrders
                .Select(o => new { Order = o, Score = CalculateSimilarity(o.Client.Name.ToLower(), busqueda.ToLower()) })
                .Where(x => x.Score > 0.45) // Umbral razonable
                .OrderByDescending(x => x.Score)
                .Take(limite)
                .Select(x => new
                {
                    id       = x.Order.Id,
                    clienta  = x.Order.Client.Name,
                    telefono = x.Order.Client.Phone,
                    estado   = x.Order.Status.ToString(),
                    tipo     = x.Order.OrderType.ToString(),
                    total    = x.Order.Total,
                    pagado   = x.Order.AmountPaid,
                    saldo    = x.Order.BalanceDue,
                    items    = x.Order.Items?.Count ?? 0,
                    creado   = x.Order.CreatedAt.ToString("dd/MM/yyyy")
                })
                .ToList();

            if (fuzzyResults.Any())
                return ToJson(new { pedidos = fuzzyResults, total = fuzzyResults.Count, advertencia = "Coincidencias aproximadas encontradas." });
        }

        var pedidos = results.Take(limite).ToList();
        return ToJson(new { pedidos, total = pedidos.Count });
    }

    private async Task<JsonElement> ObtenerPedidoAsync(JsonElement? args)
    {
        var id = GetInt(args, "id");
        var order = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
            return ToJson(new { error = $"No encontré el pedido #{id}." });

        return ToJson(new
        {
            id       = order.Id,
            clienta  = order.Client.Name,
            telefono = order.Client.Phone,
            direccion = order.Client.Address,
            estado   = order.Status.ToString(),
            tipo     = order.OrderType.ToString(),
            subtotal = order.Subtotal,
            envio    = order.ShippingCost,
            total    = order.Total,
            pagado   = order.AmountPaid,
            saldo    = order.BalanceDue,
            creado   = order.CreatedAt.ToString("dd/MM/yyyy HH:mm"),
            expira   = order.ExpiresAt.ToString("dd/MM/yyyy"),
            items    = order.Items.Select(i => new { i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal }),
            pagos    = order.Payments.Select(p => new { p.Amount, p.Method, fecha = p.Date.ToString("dd/MM/yyyy"), p.Notes })
        });
    }

    private async Task<JsonElement> ListarClientasAsync(JsonElement? args)
    {
        var busqueda = GetStr(args, "busqueda");
        var limite   = GetInt(args, "limite", 20);

        var query = _db.Clients
            .Include(c => c.Orders)
            .AsQueryable();

        if (!string.IsNullOrEmpty(busqueda))
        {
            var busqLower = busqueda.ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(busqLower) ||
                                     (c.Phone != null && c.Phone.Contains(busqueda)));
        }

        var results = await query
            .OrderByDescending(c => c.Orders.Where(o => o.Status != OrderStatus.Canceled).Sum(o => (decimal?)o.Total) ?? 0)
            .Take(Math.Clamp(limite * 2, 1, 100))
            .Select(c => new
            {
                id        = c.Id,
                nombre    = c.Name,
                telefono  = c.Phone,
                tipo      = c.Type,
                tag       = c.Tag.ToString(),
                puntos    = c.CurrentPoints,
                pedidos   = c.Orders.Count(o => o.Status != OrderStatus.Canceled),
                gastado   = c.Orders.Where(o => o.Status != OrderStatus.Canceled).Sum(o => (decimal?)o.Total) ?? 0
            })
            .ToListAsync();

        // Fuzzy fallback for listing clients
        if (!string.IsNullOrEmpty(busqueda) && results.Count < 3)
        {
            var allClients = await _db.Clients
                .Include(c => c.Orders)
                .OrderByDescending(c => c.Orders.Count)
                .Take(200)
                .ToListAsync();

            var fuzzyClients = allClients
                .Select(c => new { Client = c, Score = CalculateSimilarity(c.Name.ToLower(), busqueda.ToLower()) })
                .Where(x => x.Score > 0.5)
                .OrderByDescending(x => x.Score)
                .Take(limite)
                .Select(c => new
                {
                    id        = c.Client.Id,
                    nombre    = c.Client.Name,
                    telefono  = c.Client.Phone,
                    tipo      = c.Client.Type,
                    tag       = c.Client.Tag.ToString(),
                    puntos    = c.Client.CurrentPoints,
                    pedidos   = c.Client.Orders.Count(o => o.Status != OrderStatus.Canceled),
                    gastado   = c.Client.Orders.Where(o => o.Status != OrderStatus.Canceled).Sum(o => (decimal?)o.Total) ?? 0
                })
                .ToList();

            // Merge fuzzy results if not already present
            foreach (var f in fuzzyClients)
            {
                if (!results.Any(r => r.id == f.id)) results.Add(f);
            }
        }

        var clientas = results.Take(limite).ToList();
        return ToJson(new { clientas, total = clientas.Count });
    }

    private async Task<JsonElement> ObtenerClientaAsync(JsonElement? args)
    {
        var id = GetInt(args, "id");
        var client = await _db.Clients
            .Include(c => c.Orders).ThenInclude(o => o.Items)
            .Include(c => c.Orders).ThenInclude(o => o.Payments)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (client == null)
            return ToJson(new { error = $"No encontré la clienta con ID {id}." });

        var pedidos = client.Orders
            .Where(o => o.Status != OrderStatus.Canceled)
            .OrderByDescending(o => o.CreatedAt)
            .Take(5)
            .Select(o => new
            {
                id     = o.Id,
                estado = o.Status.ToString(),
                total  = o.Total,
                saldo  = o.BalanceDue,
                fecha  = o.CreatedAt.ToString("dd/MM/yyyy")
            });

        return ToJson(new
        {
            id        = client.Id,
            nombre    = client.Name,
            telefono  = client.Phone,
            direccion = client.Address,
            tipo      = client.Type,
            tag       = client.Tag.ToString(),
            puntos    = client.CurrentPoints,
            puntosVidaTotal = client.LifetimePoints,
            instrucciones = client.DeliveryInstructions,
            totalPedidos = client.Orders.Count(o => o.Status != OrderStatus.Canceled),
            totalGastado = client.Orders.Where(o => o.Status != OrderStatus.Canceled).Sum(o => (decimal?)o.Total) ?? 0,
            ultimosPedidos = pedidos
        });
    }

    private async Task<JsonElement> ListarRutasAsync(JsonElement? args)
    {
        var limite = GetInt(args, "limite", 5);
        var rutas = await _db.DeliveryRoutes
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o.Client)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(limite, 1, 20))
            .Select(r => new
            {
                id      = r.Id,
                nombre  = r.Name,
                estado  = r.Status.ToString(),
                pedidos = r.Deliveries.Count,
                creada  = r.CreatedAt.ToString("dd/MM/yyyy HH:mm"),
                entregas = r.Deliveries.Select(d => new
                {
                    orderId  = d.OrderId,
                    clienta  = d.Order.Client.Name,
                    estadoEntrega = d.Status.ToString()
                })
            })
            .ToListAsync();

        return ToJson(new { rutas, total = rutas.Count });
    }

    private async Task<JsonElement> ConsultarFinanzasAsync(JsonElement? args)
    {
        if (!DateTime.TryParse(GetStr(args, "fecha_inicio"), out var start))
            start = DateTime.UtcNow.AddMonths(-1);
        if (!DateTime.TryParse(GetStr(args, "fecha_fin"), out var end))
            end = DateTime.UtcNow;

        end = end.AddDays(1).AddTicks(-1); // fin de día

        var totalFacturado = await _db.Orders
            .Where(o => o.CreatedAt >= start && o.CreatedAt <= end && o.Status != OrderStatus.Canceled)
            .SumAsync(o => (decimal?)o.Total) ?? 0;

        var totalCobrado = await _db.OrderPayments
            .Where(p => p.Date >= start && p.Date <= end)
            .SumAsync(p => (decimal?)p.Amount) ?? 0;

        var totalInvertido = await _db.Investments
            .Where(i => i.Date >= start && i.Date <= end)
            .SumAsync(i => (decimal?)i.Amount) ?? 0;

        var totalGastos = await _db.DriverExpenses
            .Where(e => e.Date >= start && e.Date <= end)
            .SumAsync(e => (decimal?)e.Amount) ?? 0;

        return ToJson(new
        {
            periodo      = $"{start:dd/MM/yyyy} - {end:dd/MM/yyyy}",
            facturado    = totalFacturado,
            cobrado      = totalCobrado,
            porCobrar    = totalFacturado - totalCobrado,
            inversiones  = totalInvertido,
            gastos       = totalGastos,
            utilidadNeta = totalCobrado - totalInvertido - totalGastos,
            flujoEfectivo = totalCobrado - totalInvertido - totalGastos
        });
    }

    private async Task<JsonElement> ListarProveedoresAsync()
    {
        var proveedores = await _db.Suppliers
            .Include(s => s.Investments)
            .OrderByDescending(s => s.Investments.Sum(i => (decimal?)i.Amount) ?? 0)
            .Select(s => new
            {
                id          = s.Id,
                nombre      = s.Name,
                contacto    = s.ContactName,
                telefono    = s.Phone,
                totalInvertido = s.Investments.Sum(i => (decimal?)i.Amount) ?? 0,
                numInversiones = s.Investments.Count
            })
            .ToListAsync();

        return ToJson(new { proveedores, total = proveedores.Count });
    }

    private async Task<JsonElement> ConsultarLealtadAsync(JsonElement? args)
    {
        var clientaId = GetInt(args, "clienta_id");
        var client = await _db.Clients.FindAsync(clientaId);
        if (client == null)
            return ToJson(new { error = $"No encontré la clienta con ID {clientaId}." });

        var tier = client.LifetimePoints switch
        {
            >= 300 => "Clienta Diamante 💎",
            >= 100 => "Clienta Rose Gold 🌹",
            _      => "Clienta Pink 🩷"
        };

        return ToJson(new
        {
            clienta          = client.Name,
            puntosActuales   = client.CurrentPoints,
            puntosVidaTotal  = client.LifetimePoints,
            tier             = tier,
            puntosParaSiguienteTier = client.LifetimePoints < 100 ? 100 - client.LifetimePoints :
                                      client.LifetimePoints < 300 ? 300 - client.LifetimePoints : 0
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FUNCIONES DE ACCIÓN (WRITE)
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<JsonElement> CrearPedidoAsync(JsonElement? args)
    {
        var nombreClienta = GetStr(args, "nombre_clienta") ?? throw new ArgumentException("nombre_clienta es requerido.");
        var telefono      = GetStr(args, "telefono");
        var direccion     = GetStr(args, "direccion");
        var tipoClienta   = GetStr(args, "tipo_clienta") ?? "Nueva";
        var tipoEnvioStr  = GetStr(args, "tipo_envio") ?? "Delivery";
        var costoEnvioRaw = GetDecimal(args, "costo_envio", -1);

        var orderType = tipoEnvioStr.Equals("PickUp", StringComparison.OrdinalIgnoreCase)
            ? OrderType.PickUp : OrderType.Delivery;

        // Extraer items
        var items = new List<(string Producto, int Cantidad, decimal Precio)>();
        if (args.HasValue && args.Value.TryGetProperty("items", out var itemsEl))
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                var prod = item.TryGetProperty("producto", out var p) ? p.GetString() ?? "" : "";
                var cant = item.TryGetProperty("cantidad", out var c) ? c.GetInt32() : 1;
                var prec = item.TryGetProperty("precio", out var pr) ? pr.GetDecimal() : 0;
                if (!string.IsNullOrEmpty(prod)) items.Add((prod, cant, prec));
            }
        }
        if (items.Count == 0)
            return ToJson(new { error = "El pedido debe tener al menos un producto." });

        // Buscar o crear clienta
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Name.ToLower() == nombreClienta.ToLower());
        if (client == null)
        {
            client = new Models.Client
            {
                Name    = nombreClienta,
                Phone   = telefono,
                Address = direccion,
                Type    = tipoClienta
            };
            _db.Clients.Add(client);
            await _db.SaveChangesAsync();
        }
        else
        {
            if (telefono != null) client.Phone   = telefono;
            if (direccion != null) client.Address = direccion;
        }

        // Verificar si existe pedido pendiente (merge)
        var existing = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.ClientId == client.Id &&
                                      (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Confirmed));

        var settings = await _db.AppSettings.FindAsync(1) ?? new AppSettings();
        var activePeriodId = (await _db.SalesPeriods.FirstOrDefaultAsync(p => p.IsActive))?.Id;

        if (existing != null)
        {
            // MERGE
            foreach (var (prod, cant, prec) in items)
            {
                existing.Items.Add(new OrderItem
                {
                    ProductName = prod,
                    Quantity    = cant,
                    UnitPrice   = prec,
                    LineTotal   = prec * cant
                });
            }
            existing.Subtotal  = existing.Items.Sum(i => i.LineTotal);
            existing.Total     = existing.Subtotal + existing.ShippingCost;
            existing.CreatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return ToJson(new
            {
                accion   = "merge",
                mensaje  = $"Productos agregados al pedido existente #{existing.Id} de {client.Name}.",
                pedidoId = existing.Id,
                total    = existing.Total
            });
        }

        // NUEVO PEDIDO
        var mexicoZone = GetMexicoZone();
        var mexicoTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, mexicoZone);
        int daysUntilMonday = (8 - (int)mexicoTime.DayOfWeek) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;
        var localExpiration = mexicoTime.Date.AddDays(daysUntilMonday);
        if (client.Type == "Frecuente") localExpiration = localExpiration.AddDays(7);
        var expirationUtc = TimeZoneInfo.ConvertTimeToUtc(localExpiration, mexicoZone);

        var shippingCost = costoEnvioRaw >= 0 ? costoEnvioRaw
            : (orderType == OrderType.PickUp ? 0m : settings.DefaultShippingCost);

        var newOrder = new Order
        {
            ClientId      = client.Id,
            AccessToken   = Guid.NewGuid().ToString("N")[..16],
            ShippingCost  = shippingCost,
            ExpiresAt     = expirationUtc,
            Status        = OrderStatus.Pending,
            OrderType     = orderType,
            CreatedAt     = DateTime.UtcNow,
            SalesPeriodId = activePeriodId,
            Items         = new List<OrderItem>()
        };

        foreach (var (prod, cant, prec) in items)
        {
            newOrder.Items.Add(new OrderItem
            {
                ProductName = prod,
                Quantity    = cant,
                UnitPrice   = prec,
                LineTotal   = prec * cant
            });
        }
        newOrder.Subtotal = newOrder.Items.Sum(i => i.LineTotal);
        newOrder.Total    = newOrder.Subtotal + newOrder.ShippingCost;

        _db.Orders.Add(newOrder);
        await _db.SaveChangesAsync();

        return ToJson(new
        {
            accion   = "creado",
            mensaje  = $"Pedido #{newOrder.Id} creado para {client.Name}.",
            pedidoId = newOrder.Id,
            clientaId = client.Id,
            total    = newOrder.Total,
            expira   = expirationUtc.ToString("dd/MM/yyyy")
        });
    }

    private async Task<JsonElement> AgregarItemPedidoAsync(JsonElement? args)
    {
        var pedidoId = GetInt(args, "pedido_id");
        var producto = GetStr(args, "producto") ?? throw new ArgumentException("producto es requerido.");
        var cantidad = GetInt(args, "cantidad", 1);
        var precio   = GetDecimal(args, "precio", 0);

        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == pedidoId);
        if (order == null)
            return ToJson(new { error = $"No encontré el pedido #{pedidoId}." });

        var lineTotal = precio * cantidad;
        order.Items.Add(new OrderItem
        {
            OrderId     = pedidoId,
            ProductName = producto,
            Quantity    = cantidad,
            UnitPrice   = precio,
            LineTotal   = lineTotal
        });
        order.Subtotal = order.Items.Sum(i => i.LineTotal);
        order.Total    = order.Subtotal + order.ShippingCost;

        await _db.SaveChangesAsync();
        return ToJson(new
        {
            mensaje  = $"Agregado '{producto}' al pedido #{pedidoId}.",
            nuevoTotal = order.Total
        });
    }

    private async Task<JsonElement> CambiarEstadoPedidoAsync(JsonElement? args)
    {
        var pedidoId = GetInt(args, "pedido_id");
        var estadoStr = GetStr(args, "estado") ?? throw new ArgumentException("estado es requerido.");
        var motivo    = GetStr(args, "motivo");
        var fechaStr  = GetStr(args, "fecha_postergacion");

        if (!Enum.TryParse<OrderStatus>(estadoStr, out var nuevoEstado))
            return ToJson(new { error = $"Estado inválido: '{estadoStr}'. Usa: Pending, Confirmed, InRoute, Delivered, NotDelivered, Canceled, Postponed." });

        if (nuevoEstado == OrderStatus.Canceled && string.IsNullOrEmpty(motivo))
            return ToJson(new { error = "Para cancelar un pedido, el motivo es obligatorio." });
        if (nuevoEstado == OrderStatus.Postponed && string.IsNullOrEmpty(motivo))
            return ToJson(new { error = "Para posponer un pedido, el motivo es obligatorio." });
        if (nuevoEstado == OrderStatus.Postponed && string.IsNullOrEmpty(fechaStr))
            return ToJson(new { error = "Para posponer un pedido, la fecha de postergación es obligatoria." });

        var order = await _db.Orders
            .Include(o => o.Client)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == pedidoId);

        if (order == null)
            return ToJson(new { error = $"No encontré el pedido #{pedidoId}." });

        var estadoAnterior = order.Status;
        order.Status = nuevoEstado;

        if (nuevoEstado == OrderStatus.Canceled || nuevoEstado == OrderStatus.Postponed)
            order.PostponedNote = motivo;

        if (nuevoEstado == OrderStatus.Postponed && DateTime.TryParse(fechaStr, out var fechaPostergacion))
            order.PostponedAt = fechaPostergacion;

        // Lógica de lealtad
        var puntosCalculados = (int)(order.Total / 10);
        if (nuevoEstado == OrderStatus.Delivered && estadoAnterior != OrderStatus.Delivered)
        {
            order.Client.CurrentPoints += puntosCalculados;
            order.Client.LifetimePoints += puntosCalculados;
            _db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                ClientId = order.ClientId,
                Points   = puntosCalculados,
                Reason   = $"Pedido #{order.Id} entregado",
                Date     = DateTime.UtcNow
            });
        }
        else if (estadoAnterior == OrderStatus.Delivered && nuevoEstado != OrderStatus.Delivered)
        {
            order.Client.CurrentPoints = Math.Max(0, order.Client.CurrentPoints - puntosCalculados);
        }

        await _db.SaveChangesAsync();

        return ToJson(new
        {
            mensaje   = $"Pedido #{pedidoId} cambiado de {estadoAnterior} a {nuevoEstado}.",
            pedidoId  = pedidoId,
            estadoAnterior = estadoAnterior.ToString(),
            estadoNuevo    = nuevoEstado.ToString(),
            puntosOtorgados = nuevoEstado == OrderStatus.Delivered ? puntosCalculados : 0
        });
    }

    private async Task<JsonElement> RegistrarPagoAsync(JsonElement? args)
    {
        var pedidoId = GetInt(args, "pedido_id");
        var monto    = GetDecimal(args, "monto", 0);
        var metodo   = GetStr(args, "metodo") ?? "Efectivo";
        var notas    = GetStr(args, "notas");

        if (monto <= 0)
            return ToJson(new { error = "El monto del pago debe ser mayor a cero." });

        var order = await _db.Orders.Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == pedidoId);
        if (order == null)
            return ToJson(new { error = $"No encontré el pedido #{pedidoId}." });

        var payment = new OrderPayment
        {
            OrderId      = pedidoId,
            Amount       = monto,
            Method       = metodo,
            Date         = DateTime.UtcNow,
            RegisteredBy = "Admin",
            Notes        = notas
        };
        _db.OrderPayments.Add(payment);
        await _db.SaveChangesAsync();

        var pagado = order.Payments.Sum(p => p.Amount) + monto + order.AdvancePayment;
        var saldo  = order.Total - pagado;

        return ToJson(new
        {
            mensaje   = $"Pago de ${monto:F2} ({metodo}) registrado en pedido #{pedidoId}.",
            pagoId    = payment.Id,
            totalPagado = pagado,
            saldoPendiente = saldo,
            liquidado  = saldo <= 0
        });
    }

    private async Task<JsonElement> CrearClientaAsync(JsonElement? args)
    {
        var nombre    = GetStr(args, "nombre") ?? throw new ArgumentException("nombre es requerido.");
        var telefono  = GetStr(args, "telefono");
        var direccion = GetStr(args, "direccion");
        var tipo      = GetStr(args, "tipo") ?? "Nueva";

        var existe = await _db.Clients.AnyAsync(c => c.Name.ToLower() == nombre.ToLower());
        if (existe)
            return ToJson(new { error = $"Ya existe una clienta con el nombre '{nombre}'." });

        var client = new Models.Client
        {
            Name    = nombre,
            Phone   = telefono,
            Address = direccion,
            Type    = tipo
        };
        _db.Clients.Add(client);
        await _db.SaveChangesAsync();

        return ToJson(new
        {
            mensaje   = $"Clienta '{nombre}' registrada con éxito.",
            clientaId = client.Id,
            tipo      = client.Type
        });
    }

    private async Task<JsonElement> CrearRutaAsync(JsonElement? args)
    {
        var idsPedidos = GetIntList(args, "ids_pedidos");
        if (idsPedidos.Count == 0)
            return ToJson(new { error = "Debes proporcionar al menos un ID de pedido." });

        var orders = await _db.Orders
            .Include(o => o.Client)
            .Where(o => idsPedidos.Contains(o.Id))
            .ToListAsync();

        var invalidos = orders.Where(o => o.OrderType != OrderType.Delivery ||
            o.Status is not (OrderStatus.Pending or OrderStatus.Confirmed or OrderStatus.Shipped))
            .Select(o => o.Id).ToList();

        if (invalidos.Any())
            return ToJson(new { error = $"Los pedidos {string.Join(", ", invalidos.Select(i => "#" + i))} no son elegibles (deben ser Delivery en estado Pendiente o Confirmado)." });

        var route = new DeliveryRoute
        {
            Name        = $"Ruta {DateTime.Now:dd/MM HH:mm}",
            DriverToken = Guid.NewGuid().ToString("N")[..20],
            Status      = RouteStatus.Pending,
            CreatedAt   = DateTime.UtcNow
        };
        _db.DeliveryRoutes.Add(route);
        await _db.SaveChangesAsync();

        int sort = 0;
        foreach (var order in orders)
        {
            order.Status = OrderStatus.InRoute;
            order.DeliveryRouteId = route.Id;
            _db.Deliveries.Add(new Delivery
            {
                OrderId         = order.Id,
                DeliveryRouteId = route.Id,
                SortOrder       = sort++,
                Status          = DeliveryStatus.Pending
            });
        }
        await _db.SaveChangesAsync();

        return ToJson(new
        {
            mensaje  = $"Ruta #{route.Id} creada con {orders.Count} pedidos.",
            rutaId   = route.Id,
            nombre   = route.Name,
            pedidos  = orders.Select(o => new { o.Id, clienta = o.Client.Name })
        });
    }

    private async Task<JsonElement> LiquidarRutaAsync(JsonElement? args)
    {
        var rutaId = GetInt(args, "ruta_id");
        var route  = await _db.DeliveryRoutes
            .Include(r => r.Deliveries).ThenInclude(d => d.Order).ThenInclude(o => o.Client)
            .FirstOrDefaultAsync(r => r.Id == rutaId);

        if (route == null)
            return ToJson(new { error = $"No encontré la ruta #{rutaId}." });

        route.Status      = RouteStatus.Completed;
        route.CompletedAt = DateTime.UtcNow;

        var entregados = 0;
        foreach (var delivery in route.Deliveries)
        {
            if (delivery.Order.Status == OrderStatus.InRoute)
            {
                delivery.Order.Status = OrderStatus.Delivered;
                delivery.Status       = DeliveryStatus.Delivered;
                delivery.DeliveredAt  = DateTime.UtcNow;
                entregados++;

                // Puntos de lealtad
                var puntos = (int)(delivery.Order.Total / 10);
                delivery.Order.Client.CurrentPoints  += puntos;
                delivery.Order.Client.LifetimePoints += puntos;
                if (puntos > 0)
                {
                    _db.LoyaltyTransactions.Add(new LoyaltyTransaction
                    {
                        ClientId = delivery.Order.ClientId,
                        Points   = puntos,
                        Reason   = $"Pedido #{delivery.OrderId} entregado (ruta #{rutaId})",
                        Date     = DateTime.UtcNow
                    });
                }
            }
        }

        await _db.SaveChangesAsync();

        return ToJson(new
        {
            mensaje   = $"Ruta #{rutaId} liquidada. {entregados} pedidos marcados como entregados.",
            rutaId    = rutaId,
            entregados = entregados
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    private static string? GetStr(JsonElement? args, string key)
    {
        if (!args.HasValue) return null;
        if (!args.Value.TryGetProperty(key, out var val)) return null;
        return val.ValueKind == JsonValueKind.String ? val.GetString() : val.ToString();
    }

    private static int GetInt(JsonElement? args, string key, int defaultVal = 0)
    {
        if (!args.HasValue) return defaultVal;
        if (!args.Value.TryGetProperty(key, out var val)) return defaultVal;
        return val.TryGetInt32(out int i) ? i : defaultVal;
    }

    private static decimal GetDecimal(JsonElement? args, string key, decimal defaultVal = 0)
    {
        if (!args.HasValue) return defaultVal;
        if (!args.Value.TryGetProperty(key, out var val)) return defaultVal;
        return val.TryGetDecimal(out decimal d) ? d : defaultVal;
    }

    private static List<int> GetIntList(JsonElement? args, string key)
    {
        if (!args.HasValue) return new();
        if (!args.Value.TryGetProperty(key, out var arr)) return new();
        return arr.EnumerateArray()
            .Where(e => e.TryGetInt32(out _))
            .Select(e => e.GetInt32())
            .ToList();
    }

    private static JsonElement ToJson(object data) =>
        JsonSerializer.SerializeToElement(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

    private static double CalculateSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return 0;
        if (source == target) return 1.0;

        int n = source.Length;
        int m = target.Length;
        int[,] d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return 1.0 - (double)d[n, m] / Math.Max(source.Length, target.Length);
    }

    private static TimeZoneInfo GetMexicoZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Monterrey"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time"); }
    }
}

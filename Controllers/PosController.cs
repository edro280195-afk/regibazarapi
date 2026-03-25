using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EntregasApi.Data;
using EntregasApi.DTOs;
using EntregasApi.Models;
using EntregasApi.Services;

namespace EntregasApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PosController : ControllerBase
{
    private readonly IPosService _posService;
    private readonly IGeminiService _gemini;
    private readonly IGoogleTtsService _tts;
    private readonly IConfiguration _config;
    private readonly string FrontendUrl;

    public PosController(IPosService posService, IConfiguration config, IGeminiService gemini, IGoogleTtsService tts)
    {
        _posService = posService;
        _config = config;
        _gemini = gemini;
        _tts = tts;
        FrontendUrl = config["App:FrontendUrl"] ?? "https://regibazar.com";
    }

    [HttpPost("session/open")]
    public async Task<IActionResult> OpenSession([FromBody] OpenSessionRequest request)
    {
        try 
        {
            var session = await _posService.OpenSessionAsync(request.UserId, (decimal)request.InitialCash);
            return Ok(session);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("session/order")]
    public async Task<ActionResult<OrderSummaryDto>> CreateOrder([FromBody] CreatePosOrderRequest req)
    {
        try
        {
            var order = await _posService.CreatePosOrderAsync(req.ClientName);
            return Ok(ExcelService.MapToSummary(order, order.Client, FrontendUrl));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("session/close")]
    public async Task<IActionResult> CloseSession([FromBody] CloseSessionRequest request)
    {
        try
        {
            var session = await _posService.CloseSessionAsync(request.SessionId, (decimal)request.ActualCash);
            return Ok(session);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("session/active")]
    public async Task<IActionResult> GetActiveSession()
    {
        var session = await _posService.GetActiveSessionAsync();
        if (session == null) return NotFound("No hay sesión activa");
        return Ok(session);
    }

    [HttpGet("orders/pending")]
    public async Task<ActionResult<List<OrderSummaryDto>>> GetPendingOrders()
    {
        var orders = await _posService.GetPendingOrdersAsync();
        // Reutilizamos el mapeo estándar de la app
        var summaries = orders.Select(o => ExcelService.MapToSummary(o, o.Client, FrontendUrl)).ToList();
        return Ok(summaries);
    }

    [HttpPost("payment")]
    public async Task<IActionResult> ProcessPayment([FromBody] PaymentRequest request)
    {
        try
        {
            var payment = await _posService.PayPosOrderAsync(request.OrderId, request.SessionId, (decimal)request.Amount, request.Method);
            return Ok(payment);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("scan")]
    public async Task<IActionResult> ScanItem([FromBody] ScanItemRequest request)
    {
        try
        {
            var order = await _posService.ScanItemAsync(request.OrderId, request.Sku);
            return Ok(ExcelService.MapToSummary(order, order.Client, FrontendUrl));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("voice-command")]
    public async Task<ActionResult<PosVoiceResponse>> VoiceCommand([FromBody] PosVoiceRequest request)
    {
        try
        {
            // 1. Dejar que Cami (Gemini) analice el audio/texto
            var aiResult = await _gemini.ParsePosVoiceAsync(request.Text);

            // 2. Ejecutar las acciones en el sistema (Solo si Gemini las detectó)
            int? currentOrderId = request.OrderId;

            foreach (var action in aiResult.Actions)
            {
                switch (action.Type.ToUpper())
                {
                    case "SET_CLIENT":
                        if (!string.IsNullOrEmpty(action.ClientName))
                        {
                            var order = await _posService.CreatePosOrderAsync(action.ClientName);
                            currentOrderId = order.Id;
                        }
                        break;

                    case "ADD_ITEM":
                        if (currentOrderId.HasValue && !string.IsNullOrEmpty(action.ProductName))
                        {
                            var price = action.Price ?? 0m;
                            var qty = action.Quantity ?? 1;
                            await _posService.AddManualItemAsync(currentOrderId.Value, action.ProductName, price, qty);
                        }
                        break;

                    case "REMOVE_ITEM":
                        if (currentOrderId.HasValue && !string.IsNullOrEmpty(action.ProductName))
                        {
                            await _posService.RemoveItemByNameAsync(currentOrderId.Value, action.ProductName);
                        }
                        break;

                    case "CLEAR_CART":
                        if (currentOrderId.HasValue)
                        {
                            await _posService.ClearPosOrderAsync(currentOrderId.Value);
                        }
                        break;

                    case "APPLY_DISCOUNT":
                        if (currentOrderId.HasValue && action.Price.HasValue)
                        {
                            await _posService.ApplyDiscountAsync(currentOrderId.Value, action.Price.Value);
                        }
                        break;
                }
            }

            // 3. Generar Audio de Cami
            string? audio = null;
            try { audio = await _tts.SynthesizeAsync(aiResult.Message); } catch { }

            return Ok(new PosVoiceResponse(aiResult.Message, audio, aiResult.Actions));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }
}

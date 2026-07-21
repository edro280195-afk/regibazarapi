using EntregasApi.Data;
using EntregasApi.Hubs;
using EntregasApi.Services;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OfficeOpenXml;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ── 1. Credenciales de Google Cloud (C.A.M.I. TTS) ──
// Usamos ContentRootPath para que la ruta sea dinámica y no rompa en el servidor Linux de producción
var camiCredPath = Path.Combine(builder.Environment.ContentRootPath, "cami-voz-v2.json");
if (File.Exists(camiCredPath))
{
    Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", camiCredPath);
    Console.WriteLine("✅ Credenciales de C.A.M.I. cargadas correctamente.");
}
else
{
    Console.WriteLine("⚠️ CUIDADO: No se encontró cami-voz-v2.json en la raíz.");
}

// ── 2. Firebase Admin SDK (FCM para Notificaciones Push Android) ──
try
{
    // Primero intentamos leer la ruta desde tu appsettings.json
    var firebaseCredPath = builder.Configuration["Firebase:ServiceAccountPath"];

    // Si no está configurada ahí, buscamos el archivo "firebase-adminsdk.json" directo en la raíz
    if (string.IsNullOrEmpty(firebaseCredPath))
    {
        firebaseCredPath = Path.Combine(builder.Environment.ContentRootPath, "firebase-adminsdk.json");
    }

    if (File.Exists(firebaseCredPath))
    {
        FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromFile(firebaseCredPath)
        });
        Console.WriteLine("🔥 Motor de Firebase (Push) conectado con éxito.");
    }
    else
    {
        // Intentar con GOOGLE_APPLICATION_CREDENTIALS como fallback
        FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.GetApplicationDefault()
        });
        Console.WriteLine("🔥 Motor de Firebase conectado (Fallback default).");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Firebase] No se pudo inicializar Firebase Admin SDK: {ex.Message}");
    Console.WriteLine("[Firebase] Las notificaciones FCM estarán deshabilitadas.");
}

// EPPlus license (NonCommercial)
ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

// ── HTTP Client (Mercado Pago y otras llamadas externas) ──
builder.Services.AddHttpClient();

// ── Database ──
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── Authentication ──
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };

        // Permitir JWT via query string para SignalR
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Bodega puede operar exclusivamente las cajas y existencias; administraciÃ³n conserva acceso completo.
    options.AddPolicy("InventoryAccess", policy => policy.RequireRole("Admin", "Bodega"));
});

// ── Services ──
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IExcelService, ExcelService>();
builder.Services.AddScoped<ISuppliersService, SuppliersService>();
builder.Services.AddScoped<IExpenseService, ExpenseService>();
builder.Services.AddScoped<IPushNotificationService, PushNotificationService>();
// 🔥 Aquí está el oro que te decía. Ya tienes la inyección lista.
builder.Services.AddSingleton<IFcmService, FcmService>();
builder.Services.AddScoped<ISalesPeriodService, SalesPeriodService>();
builder.Services.AddScoped<IGeminiService, GeminiService>();
builder.Services.AddScoped<ICamiService, CamiService>();
builder.Services.AddScoped<IGoogleTtsService, GoogleTtsService>();
// ElevenLabs reemplaza a Google TTS como motor principal de CAMI. Google se
// mantiene registrado para que ElevenLabs lo use como fallback automático
// si la API key no está configurada o ElevenLabs falla.
builder.Services.AddScoped<IElevenLabsTtsService, ElevenLabsTtsService>();
builder.Services.AddScoped<IRouteOptimizerService, RouteOptimizerService>();
builder.Services.AddScoped<IGeocodingService, GeocodingService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IPosService, PosService>();
builder.Services.AddScoped<ITandaService, TandaService>();
builder.Services.AddScoped<IRaffleService, RaffleService>();
builder.Services.AddSingleton<ICloudinaryService, CloudinaryService>();
builder.Services.AddSingleton<ILabelTemplateDesignValidator, LabelTemplateDesignValidator>();
builder.Services.AddScoped<IClientResolverService, ClientResolverService>();
builder.Services.AddScoped<ILiveCaptureService, LiveCaptureService>();

// ── Recordatorios de bolsas pendientes (background) ──
builder.Services.AddHostedService<PackagesReminderService>();

// ── Notificaciones inteligentes al admin (por vencer, saldos, pulso semanal) ──
builder.Services.AddHostedService<AdminNotificationsService>();

// ── SignalR ──
builder.Services.AddSignalR();

// ── CORS ──
// 1. Definir la política
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:4200",
                "https://regibazar.com",
                "https://www.regibazar.com",
                "http://localhost",
                "https://localhost",
                "capacitor://localhost"
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials(); // Necesario para SignalR
    });
});

// ── Controllers + Swagger ──
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Entregas API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "JWT token",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        BearerFormat = "JWT",
        Scheme = "bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ── Static files for uploaded evidence ──
builder.Services.AddDirectoryBrowser();

var app = builder.Build();

// ── Migrate DB on startup ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    // Backfill de los campos normalizados de Client para clientas existentes que se
    // crearon antes de la migración AddClientAliasesAndFuzzy. La normalización vive
    // en C# (sin diacríticos, lowercase, etc.) y replicarla en SQL puro dejaría
    // resultados distintos, así que se hace acá una sola vez.
    var pending = await db.Clients
        .Where(c => c.NormalizedName == "" || c.NormalizedName == null)
        .ToListAsync();
    if (pending.Count > 0)
    {
        Console.WriteLine($"⚙️  Backfill de NormalizedName/Phone/Address para {pending.Count} clientas...");
        foreach (var c in pending)
        {
            c.NormalizedName = TextNormalizer.NormalizeName(c.Name);
            c.NormalizedPhone = TextNormalizer.NormalizePhone(c.Phone);
            c.NormalizedAddress = TextNormalizer.NormalizeAddress(c.Address);
        }
        await db.SaveChangesAsync();
        Console.WriteLine("✅ Backfill de clientas completado.");
    }

    // Migración única: el "abono" legacy (Order.AdvancePayment) se mueve al libro de pagos
    // (OrderPayment) para que ese dinero cuadre en reportes/finanzas. Idempotente: una vez
    // migrado queda AdvancePayment = 0 y no se vuelve a procesar.
#pragma warning disable CS0618 // Uso intencional del campo legacy solo para migrarlo
    var ordersWithAdvance = await db.Orders.Where(o => o.AdvancePayment > 0).ToListAsync();
    if (ordersWithAdvance.Count > 0)
    {
        Console.WriteLine($"⚙️  Migrando abono legacy de {ordersWithAdvance.Count} pedidos al libro de pagos...");
        foreach (var o in ordersWithAdvance)
        {
            db.OrderPayments.Add(new EntregasApi.Models.OrderPayment
            {
                OrderId = o.Id,
                Amount = o.AdvancePayment,
                Method = "Abono",
                Date = o.CreatedAt,
                RegisteredBy = "Sistema",
                Notes = "Abono inicial (migrado del campo legacy)"
            });
            o.AdvancePayment = 0;
        }
        await db.SaveChangesAsync();
        Console.WriteLine("✅ Migración de abonos legacy completada.");
    }
#pragma warning restore CS0618

    // Seed del catálogo de premios de RegiPuntos (idempotente: solo si está vacío).
    if (!await db.LoyaltyRewards.AnyAsync())
    {
        Console.WriteLine("⚙️  Sembrando catálogo de premios RegiPuntos...");
        db.LoyaltyRewards.AddRange(
            new EntregasApi.Models.LoyaltyReward { Name = "$50 de descuento", Description = "Aplica $50 menos en tu próximo pedido.", PointsCost = 100, Type = EntregasApi.Models.LoyaltyRewardType.FixedDiscount, Value = 50m, Icon = "💸", SortOrder = 1 },
            new EntregasApi.Models.LoyaltyReward { Name = "Envío gratis", Description = "Te invitamos el envío de tu pedido.", PointsCost = 150, Type = EntregasApi.Models.LoyaltyRewardType.FreeShipping, Value = 0m, Icon = "🚚", SortOrder = 2 },
            new EntregasApi.Models.LoyaltyReward { Name = "$100 de descuento", Description = "Aplica $100 menos en tu próximo pedido.", PointsCost = 200, Type = EntregasApi.Models.LoyaltyRewardType.FixedDiscount, Value = 100m, Icon = "💰", SortOrder = 3 },
            new EntregasApi.Models.LoyaltyReward { Name = "Regalito sorpresa", Description = "Una sorpresita de Regi Bazar en tu pedido.", PointsCost = 300, Type = EntregasApi.Models.LoyaltyRewardType.Gift, Value = 0m, Icon = "🎁", SortOrder = 4 }
        );
        await db.SaveChangesAsync();
        Console.WriteLine("✅ Catálogo de premios sembrado.");
    }
}

// ── Middleware pipeline ──
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 1. Primero enrutar
app.UseRouting();

// 2. LUEGO aplicar la política de CORS
app.UseCors("AllowAll");

// 3. Servir fotos de evidencia
try
{
    var uploadsDir = Path.Combine(app.Environment.ContentRootPath, "uploads");
    if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(uploadsDir),
        RequestPath = "/uploads"
    });
}
catch (Exception ex)
{
    Console.WriteLine($"Error initializing uploads directory: {ex.Message}");
}

// 4. Autenticación y Autorización
app.UseAuthentication();
app.UseAuthorization();

// 5. Mapear endpoints
app.MapControllers();
app.MapHub<DeliveryHub>("/hubs/delivery");
app.MapHub<DeliveryHub>("/deliveryHub");
app.MapHub<TrackingHub>("/hubs/tracking");
app.MapHub<OrderHub>("/hubs/orders");
app.MapHub<LogisticsHub>("/hubs/logistics");
app.MapHub<PosHub>("/hubs/pos");

app.Run();

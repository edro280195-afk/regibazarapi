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

builder.Services.AddAuthorization();

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
builder.Services.AddScoped<IRouteOptimizerService, RouteOptimizerService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddSingleton<ICloudinaryService, CloudinaryService>();

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
//using (var scope = app.Services.CreateScope())
//{
//    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
//    await db.Database.MigrateAsync();
//}

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
app.MapHub<TrackingHub>("/hubs/tracking");
app.MapHub<OrderHub>("/hubs/orders");
app.MapHub<LogisticsHub>("/hubs/logistics");

app.Run();
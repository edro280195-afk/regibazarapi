using Microsoft.EntityFrameworkCore;
using EntregasApi.Models;

namespace EntregasApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // Tablas existentes
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<DeliveryRoute> DeliveryRoutes => Set<DeliveryRoute>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryEvidence> DeliveryEvidences => Set<DeliveryEvidence>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Investment> Investments => Set<Investment>();
    public DbSet<DriverExpense> DriverExpenses => Set<DriverExpense>();

    // --- NUEVAS TABLAS ---
    public DbSet<CashRegisterSession> CashRegisterSessions => Set<CashRegisterSession>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<TandaProduct> TandaProducts => Set<TandaProduct>();
    public DbSet<Tanda> Tandas => Set<Tanda>();
    public DbSet<TandaParticipant> TandaParticipants => Set<TandaParticipant>();
    public DbSet<TandaPayment> TandaPayments => Set<TandaPayment>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<LoyaltyTransaction> LoyaltyTransactions => Set<LoyaltyTransaction>();
    public DbSet<PushSubscriptionModel> PushSubscriptions => Set<PushSubscriptionModel>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<SalesPeriod> SalesPeriods => Set<SalesPeriod>();
    public DbSet<OrderPackage> OrderPackages => Set<OrderPackage>();
    public DbSet<FcmToken> FcmTokens => Set<FcmToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // --- UNIQUE CONSTRAINTS ---
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<Order>()
            .HasIndex(o => o.AccessToken)
            .IsUnique();

        modelBuilder.Entity<DeliveryRoute>()
            .HasIndex(r => r.DriverToken)
            .IsUnique();

        modelBuilder.Entity<Client>()
            .HasIndex(c => c.Name)
            .IsUnique();

        modelBuilder.Entity<TandaParticipant>()
            .HasIndex(tp => new { tp.TandaId, tp.AssignedTurn })
            .IsUnique()
            .HasDatabaseName("IX_TandaParticipant_Tanda_Turn");

        // --- RELACIONES & CONFIGURACIONES ---

        // One-to-one: Order -> Delivery
        modelBuilder.Entity<Order>()
            .HasOne(o => o.Delivery)
            .WithOne(d => d.Order)
            .HasForeignKey<Delivery>(d => d.OrderId);

        // Order -> Payments (1:N)
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Payments)
            .WithOne(p => p.Order)
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPayment>(entity =>
        {
            entity.HasIndex(p => p.OrderId);
            entity.HasIndex(p => p.Date);

            entity.HasOne(p => p.CashRegisterSession)
                  .WithMany(s => s.Payments)
                  .HasForeignKey(p => p.CashRegisterSessionId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.SKU)
            .IsUnique();
        // Order -> Packages (1:N)
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Packages)
            .WithOne(p => p.Order)
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderPackage>(entity =>
        {
            entity.HasIndex(p => p.QrCodeValue).IsUnique();
            entity.HasIndex(p => p.OrderId); // Útil cuando pidas "Todas las bolsas de la orden X"
        });

        // Proveedores e Inversiones
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasIndex(s => s.Name);
            entity.HasMany(s => s.Investments)
                  .WithOne(i => i.Supplier)
                  .HasForeignKey(i => i.SupplierId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // SalesPeriods
        modelBuilder.Entity<SalesPeriod>(entity =>
        {
            entity.HasIndex(p => p.IsActive);

            entity.HasMany(p => p.Orders)
                  .WithOne(o => o.SalesPeriod)
                  .HasForeignKey(o => o.SalesPeriodId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(p => p.Investments)
                  .WithOne(i => i.SalesPeriod)
                  .HasForeignKey(i => i.SalesPeriodId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Investment>(entity =>
        {
            entity.HasIndex(i => i.SupplierId);
            entity.HasIndex(i => i.SalesPeriodId);
            entity.HasIndex(i => i.Date);
        });

        // Gastos de Chofer
        modelBuilder.Entity<DriverExpense>(entity =>
        {
            entity.HasIndex(e => e.DeliveryRouteId);
            entity.HasIndex(e => e.Date);
            entity.HasOne(e => e.DeliveryRoute)
                  .WithMany()
                  .HasForeignKey(e => e.DeliveryRouteId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Chat
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasOne(m => m.DeliveryRoute)
                  .WithMany(r => r.ChatMessages)
                  .HasForeignKey(m => m.DeliveryRouteId)
                  .OnDelete(DeleteBehavior.SetNull); // Keep messages if route is deleted

            entity.HasOne(m => m.Delivery)
                  .WithMany()
                  .HasForeignKey(m => m.DeliveryId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Loyalty (Puntos)
        modelBuilder.Entity<LoyaltyTransaction>()
            .HasOne(t => t.Client)
            .WithMany() // Si quieres lista en Client, agrégala allá, si no déjalo así
            .HasForeignKey(t => t.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        // --- DATA SEEDING (Configuración inicial) ---
        modelBuilder.Entity<AppSettings>().HasData(new AppSettings
        {
            Id = 1,
            DefaultShippingCost = 60m,
            LinkExpirationHours = 72
        });

        modelBuilder.Entity<FcmToken>(entity =>
        {
            entity.ToTable("FcmTokens");
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => e.Role);
            entity.HasIndex(e => e.DriverRouteToken);
            entity.Property(e => e.Role).HasDefaultValue("driver");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");
        });

        modelBuilder.Entity<PushSubscriptionModel>(entity =>
        {
            entity.ToTable("PushSubscriptions");

            entity.HasIndex(e => e.Endpoint)
                  .IsUnique();

            entity.HasIndex(e => new { e.Role, e.ClientId })
                  .HasDatabaseName("IX_PushSub_Role_ClientId");

            entity.HasIndex(e => new { e.Role, e.DriverRouteToken })
                  .HasDatabaseName("IX_PushSub_Role_DriverToken");

            entity.Property(e => e.Role)
                  .HasDefaultValue("client");

            entity.Property(e => e.CreatedAt)
                  .HasDefaultValueSql("NOW()");
        });
    }
}
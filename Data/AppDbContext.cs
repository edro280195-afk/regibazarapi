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
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<LoyaltyTransaction> LoyaltyTransactions => Set<LoyaltyTransaction>();

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

        // --- RELACIONES & CONFIGURACIONES ---

        // One-to-one: Order -> Delivery
        modelBuilder.Entity<Order>()
            .HasOne(o => o.Delivery)
            .WithOne(d => d.Order)
            .HasForeignKey<Delivery>(d => d.OrderId);

        // Proveedores e Inversiones
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasIndex(s => s.Name);
            entity.HasMany(s => s.Investments)
                  .WithOne(i => i.Supplier)
                  .HasForeignKey(i => i.SupplierId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Investment>(entity =>
        {
            entity.HasIndex(i => i.SupplierId);
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
        modelBuilder.Entity<ChatMessage>()
            .HasOne(m => m.DeliveryRoute)
            .WithMany(r => r.ChatMessages)
            .HasForeignKey(m => m.DeliveryRouteId)
            .OnDelete(DeleteBehavior.Cascade);

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
    }
}
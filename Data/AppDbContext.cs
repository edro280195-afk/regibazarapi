using Microsoft.EntityFrameworkCore;
using EntregasApi.Models;

namespace EntregasApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Unique constraints
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

        // One-to-one: Order -> Delivery
        modelBuilder.Entity<Order>()
            .HasOne(o => o.Delivery)
            .WithOne(d => d.Order)
            .HasForeignKey<Delivery>(d => d.OrderId);

        // Seed default settings
        modelBuilder.Entity<AppSettings>().HasData(new AppSettings
        {
            Id = 1,
            DefaultShippingCost = 60m,
            LinkExpirationHours = 72
        });

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
    }
}

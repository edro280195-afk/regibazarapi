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
    public DbSet<LoyaltyReward> LoyaltyRewards => Set<LoyaltyReward>();
    public DbSet<PushSubscriptionModel> PushSubscriptions => Set<PushSubscriptionModel>();
    public DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();
    public DbSet<SalesPeriod> SalesPeriods => Set<SalesPeriod>();
    public DbSet<OrderPackage> OrderPackages => Set<OrderPackage>();
    public DbSet<FcmToken> FcmTokens => Set<FcmToken>();

    // Inventario físico de bodega (independiente del POS legado)
    public DbSet<InventoryBox> InventoryBoxes => Set<InventoryBox>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<InventoryCountSession> InventoryCountSessions => Set<InventoryCountSession>();
    public DbSet<InventoryCountEntry> InventoryCountEntries => Set<InventoryCountEntry>();

    // Diseñador, activos y trazabilidad de impresión de etiquetas
    public DbSet<LabelTemplate> LabelTemplates => Set<LabelTemplate>();
    public DbSet<LabelTemplateVersion> LabelTemplateVersions => Set<LabelTemplateVersion>();
    public DbSet<LabelAsset> LabelAssets => Set<LabelAsset>();
    public DbSet<LabelPrintEvent> LabelPrintEvents => Set<LabelPrintEvent>();

    // Sorteos
    public DbSet<Raffle> Raffles => Set<Raffle>();
    public DbSet<RaffleParticipant> RaffleParticipants => Set<RaffleParticipant>();
    public DbSet<RaffleEntry> RaffleEntries => Set<RaffleEntry>();
    public DbSet<RaffleDraw> RaffleDraws => Set<RaffleDraw>();

    // Identidad multi-señal de clientas
    public DbSet<ClientAlias> ClientAliases => Set<ClientAlias>();
    public DbSet<ClientMergeAudit> ClientMergeAudits => Set<ClientMergeAudit>();

    // Live Capture pipeline
    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();
    public DbSet<LiveProduct> LiveProducts => Set<LiveProduct>();
    public DbSet<LiveSpokenOrder> LiveSpokenOrders => Set<LiveSpokenOrder>();
    public DbSet<LiveCommentOrder> LiveCommentOrders => Set<LiveCommentOrder>();
    public DbSet<LiveCandidate> LiveCandidates => Set<LiveCandidate>();

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

        // Identidad multi-señal: campos normalizados + alias
        modelBuilder.Entity<Client>(entity =>
        {
            entity.Property(c => c.NormalizedName)
                  .IsRequired()
                  .HasDefaultValue(string.Empty);

            entity.HasIndex(c => c.NormalizedPhone)
                  .HasDatabaseName("IX_Clients_NormalizedPhone");

            entity.HasMany(c => c.Aliases)
                  .WithOne(a => a.Client!)
                  .HasForeignKey(a => a.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClientAlias>(entity =>
        {
            entity.HasIndex(a => a.NormalizedAlias)
                  .IsUnique()
                  .HasDatabaseName("IX_ClientAliases_NormalizedAlias");

            entity.HasIndex(a => a.ClientId)
                  .HasDatabaseName("IX_ClientAliases_ClientId");
        });

        // Live Capture pipeline
        modelBuilder.Entity<LiveCandidate>()
            .HasOne(c => c.LiveSession)
            .WithMany(s => s.Candidates)
            .HasForeignKey(c => c.LiveSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LiveCandidate>()
            .HasOne(c => c.LiveProduct)
            .WithMany(p => p.Candidates)
            .HasForeignKey(c => c.LiveProductId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<TandaParticipant>()
            .HasIndex(tp => new { tp.TandaId, tp.AssignedTurn })
            .IsUnique()
            .HasDatabaseName("IX_TandaParticipant_Tanda_Turn");

        // --- RELACIONES & CONFIGURACIONES ---

        // Order -> Deliveries (1:N). Un reintento conserva el intento anterior.
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Deliveries)
            .WithOne(d => d.Order)
            .HasForeignKey(d => d.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Delivery -> TandaParticipant (many-to-one opcional; XOR con OrderId)
        modelBuilder.Entity<Delivery>(entity =>
        {
            entity.HasOne(d => d.TandaParticipant)
                  .WithMany()
                  .HasForeignKey(d => d.TandaParticipantId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(d => d.TandaParticipantId);

            // Garantiza el XOR: exactamente uno de OrderId / TandaParticipantId debe estar presente.
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_Deliveries_OrderXorTanda",
                "(\"OrderId\" IS NOT NULL AND \"TandaParticipantId\" IS NULL) OR " +
                "(\"OrderId\" IS NULL AND \"TandaParticipantId\" IS NOT NULL)"));
        });

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

        // Inventario físico / etiquetas NFC
        modelBuilder.Entity<InventoryBox>(entity =>
        {
            entity.HasIndex(box => box.Code).IsUnique();
            entity.HasIndex(box => box.NfcToken).IsUnique();
            entity.HasIndex(box => box.NfcTagUid).IsUnique();
            entity.Property(box => box.Code).HasMaxLength(30);
            entity.Property(box => box.Name).HasMaxLength(120);
            entity.Property(box => box.Location).HasMaxLength(200);
            entity.Property(box => box.NfcToken).HasMaxLength(64);
            entity.Property(box => box.NfcTagUid).HasMaxLength(64);
            entity.HasMany(box => box.Items)
                .WithOne(item => item.InventoryBox)
                .HasForeignKey(item => item.InventoryBoxId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(box => box.Movements)
                .WithOne(movement => movement.InventoryBox)
                .HasForeignKey(movement => movement.InventoryBoxId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(box => box.CountSessions)
                .WithOne(session => session.InventoryBox)
                .HasForeignKey(session => session.InventoryBoxId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InventoryItem>(entity =>
        {
            entity.HasIndex(item => item.InventoryBoxId);
            entity.HasIndex(item => item.LabelCode).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(150);
            entity.Property(item => item.Variant).HasMaxLength(120);
            entity.Property(item => item.Barcode).HasMaxLength(100);
            entity.Property(item => item.LabelCode).HasMaxLength(40);
        });

        modelBuilder.Entity<InventoryMovement>(entity =>
        {
            entity.HasIndex(movement => new { movement.InventoryBoxId, movement.OccurredAt });
            entity.HasIndex(movement => movement.TransferGroupId);
            entity.Property(movement => movement.Note).HasMaxLength(300);
            entity.Property(movement => movement.PerformedBy).HasMaxLength(120);
            entity.HasOne(movement => movement.InventoryItem)
                .WithMany(item => item.Movements)
                .HasForeignKey(movement => movement.InventoryItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<InventoryCountSession>(entity =>
        {
            entity.HasIndex(session => new { session.InventoryBoxId, session.CountedAt });
            entity.Property(session => session.Note).HasMaxLength(300);
            entity.Property(session => session.PerformedBy).HasMaxLength(120);
            entity.HasMany(session => session.Entries)
                .WithOne(entry => entry.InventoryCountSession)
                .HasForeignKey(entry => entry.InventoryCountSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InventoryCountEntry>(entity =>
        {
            entity.HasIndex(entry => new { entry.InventoryCountSessionId, entry.InventoryItemId }).IsUnique();
            entity.Property(entry => entry.ItemName).HasMaxLength(150);
            entity.Property(entry => entry.Variant).HasMaxLength(120);
        });

        // Plantillas de impresión: la versión publicada se referencia sin permitir
        // cascadas cíclicas; las versiones históricas se preservan con la plantilla.
        modelBuilder.Entity<LabelTemplate>(entity =>
        {
            entity.HasIndex(template => new { template.Kind, template.PrinterProfile, template.IsArchived });
            entity.HasIndex(template => template.Kind)
                .IsUnique()
                .HasFilter("\"IsDefault\" = true AND \"IsArchived\" = false")
                .HasDatabaseName("IX_LabelTemplates_ActiveDefaultByKind");
            entity.Property(template => template.Name).HasMaxLength(120);
            entity.Property(template => template.Description).HasMaxLength(400);
            entity.Property(template => template.CreatedBy).HasMaxLength(120);
            entity.Property(template => template.UpdatedBy).HasMaxLength(120);
            entity.HasOne(template => template.PublishedVersion)
                .WithMany()
                .HasForeignKey(template => template.PublishedVersionId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(template => template.Versions)
                .WithOne(version => version.LabelTemplate)
                .HasForeignKey(version => version.LabelTemplateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LabelTemplateVersion>(entity =>
        {
            entity.HasIndex(version => new { version.LabelTemplateId, version.VersionNumber }).IsUnique();
            entity.HasIndex(version => new { version.LabelTemplateId, version.Status });
            entity.Property(version => version.DesignJson).HasColumnType("jsonb");
            entity.Property(version => version.CreatedBy).HasMaxLength(120);
            entity.Property(version => version.PublishedBy).HasMaxLength(120);
        });

        modelBuilder.Entity<LabelAsset>(entity =>
        {
            entity.HasIndex(asset => new { asset.IsArchived, asset.UploadedAt });
            entity.Property(asset => asset.Name).HasMaxLength(120);
            entity.Property(asset => asset.OriginalFileName).HasMaxLength(260);
            entity.Property(asset => asset.ContentType).HasMaxLength(120);
            entity.Property(asset => asset.Url).HasMaxLength(1200);
            entity.Property(asset => asset.UploadedBy).HasMaxLength(120);
        });

        modelBuilder.Entity<LabelPrintEvent>(entity =>
        {
            entity.HasIndex(printEvent => new { printEvent.TargetKind, printEvent.TargetId, printEvent.RequestedAt });
            entity.HasIndex(printEvent => printEvent.LabelTemplateVersionId);
            entity.Property(printEvent => printEvent.TargetId).HasMaxLength(64);
            entity.Property(printEvent => printEvent.RequestedBy).HasMaxLength(120);
            entity.HasOne(printEvent => printEvent.LabelTemplateVersion)
                .WithMany()
                .HasForeignKey(printEvent => printEvent.LabelTemplateVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
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

        // Sorteos
        modelBuilder.Entity<Raffle>(entity =>
        {
            entity.HasIndex(r => r.Status);
            entity.HasIndex(r => r.RaffleDate);
            entity.HasIndex(r => r.TandaId);

            entity.HasOne(r => r.Winner)
                  .WithMany()
                  .HasForeignKey(r => r.WinnerId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(r => r.Tanda)
                  .WithMany()
                  .HasForeignKey(r => r.TandaId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(r => r.PrizeProduct)
                  .WithMany()
                  .HasForeignKey(r => r.PrizeProductId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(r => r.Participants)
                  .WithOne(p => p.Raffle)
                  .HasForeignKey(p => p.RaffleId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(r => r.Entries)
                  .WithOne(e => e.Raffle)
                  .HasForeignKey(e => e.RaffleId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(r => r.Draws)
                  .WithOne(d => d.Raffle)
                  .HasForeignKey(d => d.RaffleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RaffleParticipant>(entity =>
        {
            entity.HasIndex(p => new { p.RaffleId, p.ClientId })
                  .IsUnique()
                  .HasDatabaseName("IX_RaffleParticipant_Raffle_Client");

            entity.HasOne(p => p.Client)
                  .WithMany()
                  .HasForeignKey(p => p.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RaffleEntry>(entity =>
        {
            entity.HasIndex(e => e.OrderId);
            entity.HasIndex(e => new { e.RaffleId, e.ClientId });

            entity.HasOne(e => e.Client)
                  .WithMany()
                  .HasForeignKey(e => e.ClientId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Order)
                  .WithMany()
                  .HasForeignKey(e => e.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RaffleDraw>(entity =>
        {
            entity.HasIndex(d => d.RaffleId);
            entity.HasIndex(d => d.DrawDate);

            entity.HasOne(d => d.Winner)
                  .WithMany()
                  .HasForeignKey(d => d.WinnerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

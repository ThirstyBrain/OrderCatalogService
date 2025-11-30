using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OrderCatalogService.Domain.Orders;
using OrderCatalogService.Infrastructure.Outbox;

namespace OrderCatalogService.Infrastructure.Database;

/// <summary>
/// Database context with optimistic concurrency control
/// Uses PostgreSQL xmin for row versioning (automatic concurrency detection)
/// Configured for high-concurrency read/write patterns
/// </summary>
public sealed class OrderCatalogDbContext : DbContext
{
    public OrderCatalogDbContext(DbContextOptions<OrderCatalogDbContext> options)
        : base(options)
    {
        // Connection resiliency configured in Program.cs
    }

    // Aggregate Roots
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Cart> Carts => Set<Cart>();
    
    // Infrastructure
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ═══════════════════════════════════════════════════════════
        // ORDER AGGREGATE
        // ═══════════════════════════════════════════════════════════
        
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(e => e.Id);
            
            // Strongly-typed ID conversion
            entity.Property(e => e.Id)
                .HasConversion(
                    id => id.Value,
                    value => OrderId.From(value))
                .ValueGeneratedNever();

            entity.Property(e => e.CustomerId)
                .HasConversion(
                    id => id.Value,
                    value => CustomerId.From(value))
                .IsRequired();

            // Optimistic Concurrency - PostgreSQL xmin (system column)
            // Alternative: explicit RowVersion column for cross-DB compatibility
            entity.Property(e => e.RowVersion)
                .IsRowVersion() // Maps to xmin in PostgreSQL
                .IsConcurrencyToken();

            // Value Object: OrderStatus
            entity.Property(e => e.Status)
                .HasConversion(
                    status => status.Value,
                    value => OrderStatus.FromString(value))
                .HasMaxLength(20)
                .IsRequired();

            // Value Object: Money (owned type)
            entity.OwnsOne(e => e.TotalAmount, money =>
            {
                money.Property(m => m.Amount)
                    .HasColumnName("TotalAmount")
                    .HasPrecision(18, 2)
                    .IsRequired();
                
                money.Property(m => m.Currency)
                    .HasColumnName("Currency")
                    .HasMaxLength(3)
                    .IsRequired();
            });

            // Value Object: ShippingAddress (owned type - stored as JSON in PostgreSQL)
            entity.OwnsOne(e => e.ShippingAddress, address =>
            {
                address.ToJson("ShippingAddress"); // PostgreSQL JSONB column
                address.Property(a => a.AddressLine1).IsRequired();
                address.Property(a => a.City).IsRequired();
                address.Property(a => a.PostalCode).IsRequired();
                address.Property(a => a.Country).IsRequired();
            });

            // Order Lines (owned collection)
            entity.OwnsMany(e => e.Lines, lines =>
            {
                lines.ToTable("OrderLines");
                lines.WithOwner().HasForeignKey("OrderId");
                
                lines.HasKey("Id");
                
                lines.Property<OrderLineId>("Id")
                    .HasConversion(
                        id => id.Value,
                        value => OrderLineId.From(value))
                    .ValueGeneratedNever();

                lines.Property(l => l.ProductId)
                    .HasConversion(
                        id => id.Value,
                        value => ProductId.From(value))
                    .IsRequired();

                lines.Property(l => l.ProductName)
                    .HasMaxLength(200)
                    .IsRequired();

                lines.Property(l => l.Quantity).IsRequired();

                lines.OwnsOne(l => l.UnitPrice, money =>
                {
                    money.Property(m => m.Amount)
                        .HasColumnName("UnitPrice")
                        .HasPrecision(18, 2)
                        .IsRequired();
                    
                    money.Property(m => m.Currency)
                        .HasColumnName("Currency")
                        .HasMaxLength(3)
                        .IsRequired();
                });

                // Index for efficient queries
                lines.HasIndex("OrderId");
            });

            // Idempotency key (unique constraint)
            entity.Property(e => e.IdempotencyKey)
                .HasMaxLength(100);
            
            entity.HasIndex(e => e.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");

            // Timestamps
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.CompletedAt);
            entity.Property(e => e.CancelledAt);

            // Indexes for query performance
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            
            // Composite index for customer's recent orders
            entity.HasIndex(e => new { e.CustomerId, e.CreatedAt });

            // Ignore domain events (not persisted)
            entity.Ignore(e => e.DomainEvents);
        });

        // ═══════════════════════════════════════════════════════════
        // PRODUCT AGGREGATE (Read-Heavy, Cached)
        // ═══════════════════════════════════════════════════════════
        
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .ValueGeneratedNever();

            entity.Property(e => e.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(e => e.Description)
                .HasMaxLength(2000);

            entity.Property(e => e.Price)
                .HasPrecision(18, 2)
                .IsRequired();

            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .IsRequired()
                .HasDefaultValue("USD");

            // Inventory (with optimistic concurrency)
            entity.Property(e => e.StockQuantity)
                .IsRequired()
                .IsConcurrencyToken(); // Detect concurrent stock updates

            entity.Property(e => e.IsAvailable)
                .IsRequired()
                .HasDefaultValue(true);

            entity.Property(e => e.CategoryId);

            // Timestamps
            entity.Property(e => e.CreatedAt)
                .IsRequired()
                .HasDefaultValueSql("NOW()");

            entity.Property(e => e.UpdatedAt)
                .IsRequired()
                .HasDefaultValueSql("NOW()");

            // Row version for full optimistic concurrency
            entity.Property(e => e.RowVersion)
                .IsRowVersion();

            // Indexes
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.CategoryId);
            entity.HasIndex(e => e.IsAvailable);
            entity.HasIndex(e => new { e.CategoryId, e.IsAvailable });
        });

        // ═══════════════════════════════════════════════════════════
        // CART AGGREGATE (Ephemeral, Short-Lived)
        // ═══════════════════════════════════════════════════════════
        
        modelBuilder.Entity<Cart>(entity =>
        {
            entity.ToTable("Carts");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .ValueGeneratedNever();

            entity.Property(e => e.CustomerId);

            entity.Property(e => e.SessionId)
                .HasMaxLength(100)
                .IsRequired();

            // Cart items (owned collection, stored as JSON for flexibility)
            entity.OwnsMany(e => e.Items, items =>
            {
                items.ToJson("Items"); // PostgreSQL JSONB
                items.Property(i => i.ProductId).IsRequired();
                items.Property(i => i.Quantity).IsRequired();
                items.Property(i => i.AddedAt).IsRequired();
            });

            entity.Property(e => e.CreatedAt)
                .IsRequired()
                .HasDefaultValueSql("NOW()");

            entity.Property(e => e.UpdatedAt)
                .IsRequired()
                .HasDefaultValueSql("NOW()");

            // TTL: Delete carts older than 7 days (handled by background job)
            entity.Property(e => e.ExpiresAt)
                .IsRequired();

            // Indexes
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.SessionId).IsUnique();
            entity.HasIndex(e => e.ExpiresAt); // For cleanup queries
        });

        // ═══════════════════════════════════════════════════════════
        // OUTBOX MESSAGES (Transactional Outbox Pattern)
        // ═══════════════════════════════════════════════════════════
        
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Type)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(e => e.Payload)
                .HasColumnType("jsonb") // PostgreSQL JSONB
                .IsRequired();

            entity.Property(e => e.OccurredAt)
                .IsRequired();

            entity.Property(e => e.ProcessedAt);

            entity.Property(e => e.RetryCount)
                .HasDefaultValue(0);

            entity.Property(e => e.LastError)
                .HasMaxLength(2000);

            // Critical indexes for outbox polling
            entity.HasIndex(e => new { e.ProcessedAt, e.OccurredAt })
                .HasFilter("\"ProcessedAt\" IS NULL"); // Partial index for unprocessed

            entity.HasIndex(e => e.Type);
        });

        // ═══════════════════════════════════════════════════════════
        // WEBHOOKS
        // ═══════════════════════════════════════════════════════════
        
        modelBuilder.Entity<Webhook>(entity =>
        {
            entity.ToTable("Webhooks");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Url)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(e => e.Secret)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.Events)
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(e => e.IsActive)
                .IsRequired()
                .HasDefaultValue(true);

            entity.Property(e => e.CreatedAt)
                .IsRequired()
                .HasDefaultValueSql("NOW()");

            entity.HasIndex(e => e.IsActive);
        });

        // ═══════════════════════════════════════════════════════════
        // GLOBAL QUERY FILTERS & CONVENTIONS
        // ═══════════════════════════════════════════════════════════

        // Soft delete support (if needed)
        // modelBuilder.Entity<Order>().HasQueryFilter(e => !e.IsDeleted);
    }

    // ═══════════════════════════════════════════════════════════
    // SAVECHANGES OVERRIDE (Automatic Timestamp Updates)
    // ═══════════════════════════════════════════════════════════

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Automatically update timestamps
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is IAuditableEntity && 
                       (e.State == EntityState.Added || e.State == EntityState.Modified));

        foreach (var entry in entries)
        {
            var entity = (IAuditableEntity)entry.Entity;

            if (entry.State == EntityState.Added)
            {
                entity.CreatedAt = DateTimeOffset.UtcNow;
            }

            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}

// ═══════════════════════════════════════════════════════════
// DOMAIN ENTITIES
// ═══════════════════════════════════════════════════════════

public sealed class Product : IAuditableEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public int StockQuantity { get; set; }
    public bool IsAvailable { get; set; }
    public Guid? CategoryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public sealed class Cart : IAuditableEntity
{
    public Guid Id { get; set; }
    public Guid? CustomerId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public List<CartItem> Items { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class CartItem
{
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public interface IAuditableEntity
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
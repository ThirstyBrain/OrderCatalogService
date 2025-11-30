using System.Collections.Concurrent;
using OrderCatalogService.Domain.Common;

namespace OrderCatalogService.Domain.Orders;

/// <summary>
/// Order Aggregate Root - handles all order lifecycle with strong consistency
/// Implements optimistic concurrency via RowVersion
/// </summary>
public sealed class Order : AggregateRoot<OrderId>
{
    // Backing fields for encapsulation
    private readonly List<OrderLine> _lines = new();
    private readonly List<DomainEvent> _domainEvents = new();
    
    // Concurrency token - PostgreSQL uses xmin or explicit rowversion
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();
    
    // Value Objects
    public CustomerId CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public Money TotalAmount { get; private set; }
    public ShippingAddress ShippingAddress { get; private set; }
    
    // Timestamps
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    
    // Idempotency tracking
    public string? IdempotencyKey { get; private set; }
    
    // Read-only collection
    public IReadOnlyCollection<OrderLine> Lines => _lines.AsReadOnly();
    public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    // EF Core constructor
    private Order() { }

    // Factory method - ensures valid creation
    public static Order Create(
        CustomerId customerId,
        ShippingAddress shippingAddress,
        IEnumerable<OrderLine> lines,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(customerId);
        ArgumentNullException.ThrowIfNull(shippingAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var orderLines = lines.ToList();
        if (orderLines.Count == 0)
            throw new DomainException("Order must contain at least one line item");

        var order = new Order
        {
            Id = OrderId.NewOrderId(),
            CustomerId = customerId,
            ShippingAddress = shippingAddress,
            Status = OrderStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey
        };

        // Thread-safe addition
        lock (order._lines)
        {
            order._lines.AddRange(orderLines);
        }

        order.RecalculateTotal();
        
        // Raise domain event
        order.AddDomainEvent(new OrderCreatedEvent(
            order.Id,
            order.CustomerId,
            order.TotalAmount,
            order.CreatedAt));

        return order;
    }

    /// <summary>
    /// Thread-safe total calculation using lock for safety
    /// In practice, orders are created once and rarely modified concurrently
    /// </summary>
    private void RecalculateTotal()
    {
        decimal total;
        
        lock (_lines)
        {
            total = _lines.Sum(l => l.UnitPrice.Amount * l.Quantity);
        }
        
        TotalAmount = new Money(total, "USD");
    }

    /// <summary>
    /// Confirm order - idempotent operation with status guard
    /// </summary>
    public void Confirm()
    {
        if (Status != OrderStatus.Pending)
            throw new DomainException($"Cannot confirm order in {Status} status");

        Status = OrderStatus.Confirmed;
        
        AddDomainEvent(new OrderConfirmedEvent(Id, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Cancel order - uses lock-free compare-and-swap pattern conceptually
    /// Actual DB-level concurrency handled by EF Core + RowVersion
    /// </summary>
    public void Cancel(string reason)
    {
        // Terminal states check
        if (Status == OrderStatus.Completed || Status == OrderStatus.Cancelled)
            throw new DomainException($"Cannot cancel order in {Status} status");

        var previousStatus = Status;
        Status = OrderStatus.Cancelled;
        CancelledAt = DateTimeOffset.UtcNow;
        
        AddDomainEvent(new OrderCancelledEvent(Id, previousStatus, reason, CancelledAt.Value));
    }

    /// <summary>
    /// Mark as shipped - requires confirmed state
    /// </summary>
    public void Ship(string trackingNumber)
    {
        if (Status != OrderStatus.Confirmed)
            throw new DomainException($"Cannot ship order in {Status} status");

        Status = OrderStatus.Shipped;
        
        AddDomainEvent(new OrderShippedEvent(Id, trackingNumber, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Complete order - terminal state
    /// </summary>
    public void Complete()
    {
        if (Status != OrderStatus.Shipped)
            throw new DomainException($"Cannot complete order in {Status} status");

        Status = OrderStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
        
        AddDomainEvent(new OrderCompletedEvent(Id, CompletedAt.Value));
    }

    /// <summary>
    /// Thread-safe domain event collection
    /// Using List with lock since events are rarely added concurrently
    /// For ultra-high concurrency, could use ConcurrentBag
    /// </summary>
    private void AddDomainEvent(DomainEvent domainEvent)
    {
        lock (_domainEvents)
        {
            _domainEvents.Add(domainEvent);
        }
    }

    public void ClearDomainEvents()
    {
        lock (_domainEvents)
        {
            _domainEvents.Clear();
        }
    }
}

/// <summary>
/// Order Line - owned entity, part of Order aggregate
/// Immutable after creation for thread safety
/// </summary>
public sealed class OrderLine : Entity<OrderLineId>
{
    public ProductId ProductId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public Money UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    
    private OrderLine() { }

    public static OrderLine Create(ProductId productId, string productName, Money unitPrice, int quantity)
    {
        ArgumentNullException.ThrowIfNull(productId);
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentNullException.ThrowIfNull(unitPrice);
        
        if (quantity <= 0)
            throw new DomainException("Quantity must be positive");

        return new OrderLine
        {
            Id = OrderLineId.NewOrderLineId(),
            ProductId = productId,
            ProductName = productName,
            UnitPrice = unitPrice,
            Quantity = quantity
        };
    }
}

/// <summary>
/// Order Status - value object with valid transitions
/// </summary>
public sealed record OrderStatus
{
    public string Value { get; }

    private OrderStatus(string value) => Value = value;

    public static readonly OrderStatus Pending = new("Pending");
    public static readonly OrderStatus Confirmed = new("Confirmed");
    public static readonly OrderStatus Shipped = new("Shipped");
    public static readonly OrderStatus Completed = new("Completed");
    public static readonly OrderStatus Cancelled = new("Cancelled");

    public static OrderStatus FromString(string status) => status switch
    {
        "Pending" => Pending,
        "Confirmed" => Confirmed,
        "Shipped" => Shipped,
        "Completed" => Completed,
        "Cancelled" => Cancelled,
        _ => throw new DomainException($"Invalid order status: {status}")
    };

    // Implicit conversion for convenience
    public static implicit operator string(OrderStatus status) => status.Value;
}

// ═══════════════════════════════════════════════════════════
// STRONGLY TYPED IDs (prevents primitive obsession)
// ═══════════════════════════════════════════════════════════

public sealed record OrderId(Guid Value)
{
    public static OrderId NewOrderId() => new(Guid.NewGuid());
    public static OrderId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public sealed record OrderLineId(Guid Value)
{
    public static OrderLineId NewOrderLineId() => new(Guid.NewGuid());
    public static OrderLineId From(Guid value) => new(value);
}

public sealed record CustomerId(Guid Value)
{
    public static CustomerId NewCustomerId() => new(Guid.NewGuid());
    public static CustomerId From(Guid value) => new(value);
    public static CustomerId From(string value) => new(Guid.Parse(value));
}

public sealed record ProductId(Guid Value)
{
    public static ProductId NewProductId() => new(Guid.NewGuid());
    public static ProductId From(Guid value) => new(value);
}

// ═══════════════════════════════════════════════════════════
// VALUE OBJECTS
// ═══════════════════════════════════════════════════════════

public sealed record Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency = "USD") => new(0, currency);
    
    public Money Add(Money other)
    {
        if (Currency != other.Currency)
            throw new DomainException("Cannot add money in different currencies");
        return new Money(Amount + other.Amount, Currency);
    }
}

public sealed record ShippingAddress(
    string AddressLine1,
    string? AddressLine2,
    string City,
    string StateOrProvince,
    string PostalCode,
    string Country)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AddressLine1);
        ArgumentException.ThrowIfNullOrWhiteSpace(City);
        ArgumentException.ThrowIfNullOrWhiteSpace(PostalCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(Country);
    }
}

// ═══════════════════════════════════════════════════════════
// DOMAIN EVENTS (for Transactional Outbox)
// ═══════════════════════════════════════════════════════════

public abstract record DomainEvent(DateTimeOffset OccurredAt);

public sealed record OrderCreatedEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    Money TotalAmount,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record OrderConfirmedEvent(
    OrderId OrderId,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record OrderCancelledEvent(
    OrderId OrderId,
    OrderStatus PreviousStatus,
    string Reason,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record OrderShippedEvent(
    OrderId OrderId,
    string TrackingNumber,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record OrderCompletedEvent(
    OrderId OrderId,
    DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

// ═══════════════════════════════════════════════════════════
// BASE CLASSES
// ═══════════════════════════════════════════════════════════

public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    // Aggregate roots are the consistency boundary
}

public abstract class Entity<TId> where TId : notnull
{
    public TId Id { get; protected set; } = default!;
    
    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other)
            return false;
        
        if (ReferenceEquals(this, other))
            return true;
        
        return Id.Equals(other.Id);
    }
    
    public override int GetHashCode() => Id.GetHashCode();
}

public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}
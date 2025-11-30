using HotChocolate;
using HotChocolate.Authorization;
using HotChocolate.Data;
using HotChocolate.Types;
using HotChocolate.Types.Relay;
using MediatR;
using OrderCatalogService.Features.Orders.CreateOrder;
using OrderCatalogService.Features.Orders.GetOrder;
using OrderCatalogService.Features.Products;
using OrderCatalogService.Infrastructure.Database;

namespace OrderCatalogService.GraphQL;

// ═══════════════════════════════════════════════════════════
// QUERY ROOT
// ═══════════════════════════════════════════════════════════

public sealed class Query
{
    /// <summary>
    /// Get paginated list of products
    /// </summary>
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    [UsePaging]
    public IQueryable<Product> GetProducts([Service] OrderCatalogDbContext dbContext)
    {
        return dbContext.Products.Where(p => p.IsAvailable);
    }

    /// <summary>
    /// Get single product by ID
    /// </summary>
    public async Task<ProductDto?> GetProduct(
        [ID] Guid id,
        [Service] ISender sender,
        CancellationToken cancellationToken)
    {
        var query = new GetProductDetailsQuery(id);
        return await sender.Send(query, cancellationToken);
    }

    /// <summary>
    /// Get single order by ID (requires authentication)
    /// </summary>
    [Authorize(Policy = "OrderRead")]
    public async Task<OrderDetailDto?> GetOrder(
        [ID] Guid id,
        [Service] ISender sender,
        CancellationToken cancellationToken)
    {
        var query = new GetOrderQuery(id);
        return await sender.Send(query, cancellationToken);
    }

    /// <summary>
    /// Get current user's orders (requires authentication)
    /// </summary>
    [Authorize(Policy = "OrderRead")]
    [UseProjection]
    [UsePaging]
    public IQueryable<Order> GetMyOrders(
        [GlobalState] Guid userId,
        [Service] OrderCatalogDbContext dbContext)
    {
        return dbContext.Orders.Where(o => o.CustomerId == CustomerId.From(userId));
    }
}

// ═══════════════════════════════════════════════════════════
// MUTATION ROOT
// ═══════════════════════════════════════════════════════════

public sealed class Mutation
{
    /// <summary>
    /// Create a new order (idempotent, requires authentication)
    /// </summary>
    [Authorize(Policy = "OrderWrite")]
    public async Task<CreateOrderPayload> CreateOrder(
        CreateOrderInput input,
        [Service] ISender sender,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = httpContextAccessor.HttpContext?.Request.Headers["Idempotency-Key"].FirstOrDefault();
        
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            throw new GraphQLException("Idempotency-Key header is required for order creation");
        }

        var command = new CreateOrderCommand
        {
            IdempotencyKey = idempotencyKey,
            CustomerId = input.CustomerId,
            ShippingAddress = new CreateOrderAddress(
                input.ShippingAddress.AddressLine1,
                input.ShippingAddress.AddressLine2,
                input.ShippingAddress.City,
                input.ShippingAddress.StateOrProvince,
                input.ShippingAddress.PostalCode,
                input.ShippingAddress.Country),
            Lines = input.Lines.Select(l => new CreateOrderLine(l.ProductId, l.Quantity)).ToList()
        };

        var result = await sender.Send(command, cancellationToken);

        return new CreateOrderPayload
        {
            OrderId = result.OrderId,
            OrderNumber = result.OrderNumber,
            Status = result.Status,
            TotalAmount = result.TotalAmount
        };
    }

    /// <summary>
    /// Add item to cart
    /// </summary>
    public async Task<CartPayload> AddToCart(
        AddToCartInput input,
        [Service] ISender sender,
        CancellationToken cancellationToken)
    {
        var command = new Features.Carts.AddToCartCommand
        {
            CartId = input.CartId,
            ProductId = input.ProductId,
            Quantity = input.Quantity
        };

        var result = await sender.Send(command, cancellationToken);

        return new CartPayload
        {
            CartId = result.Id,
            ItemCount = result.Items.Count
        };
    }
}

// ═══════════════════════════════════════════════════════════
// INPUT TYPES
// ═══════════════════════════════════════════════════════════

public sealed record CreateOrderInput
{
    public Guid CustomerId { get; init; }
    public AddressInput ShippingAddress { get; init; } = null!;
    public List<OrderLineInput> Lines { get; init; } = new();
}

public sealed record AddressInput
{
    public string AddressLine1 { get; init; } = string.Empty;
    public string? AddressLine2 { get; init; }
    public string City { get; init; } = string.Empty;
    public string StateOrProvince { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
}

public sealed record OrderLineInput
{
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
}

public sealed record AddToCartInput
{
    public Guid CartId { get; init; }
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
}

// ═══════════════════════════════════════════════════════════
// PAYLOAD TYPES (Mutation Responses)
// ═══════════════════════════════════════════════════════════

public sealed record CreateOrderPayload
{
    public Guid OrderId { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
}

public sealed record CartPayload
{
    public Guid CartId { get; init; }
    public int ItemCount { get; init; }
}

// ═══════════════════════════════════════════════════════════
// OBJECT TYPES (GraphQL Schema Types)
// ═══════════════════════════════════════════════════════════

public sealed class ProductType : ObjectType<Product>
{
    protected override void Configure(IObjectTypeDescriptor<Product> descriptor)
    {
        descriptor.Description("A product in the catalog");

        descriptor.Field(p => p.Id).Type<NonNullType<IdType>>();
        descriptor.Field(p => p.Name).Type<NonNullType<StringType>>();
        descriptor.Field(p => p.Description).Type<StringType>();
        descriptor.Field(p => p.Price).Type<NonNullType<DecimalType>>();
        descriptor.Field(p => p.Currency).Type<NonNullType<StringType>>();
        descriptor.Field(p => p.IsAvailable).Type<NonNullType<BooleanType>>();
        descriptor.Field(p => p.StockQuantity).Type<NonNullType<IntType>>();
    }
}

public sealed class OrderType : ObjectType<Order>
{
    protected override void Configure(IObjectTypeDescriptor<Order> descriptor)
    {
        descriptor.Description("A customer order");

        descriptor.Field(o => o.Id)
            .Type<NonNullType<IdType>>()
            .Resolve(ctx => ctx.Parent<Order>().Id.Value);

        descriptor.Field(o => o.CustomerId)
            .Type<NonNullType<IdType>>()
            .Resolve(ctx => ctx.Parent<Order>().CustomerId.Value);

        descriptor.Field(o => o.Status)
            .Type<NonNullType<StringType>>()
            .Resolve(ctx => ctx.Parent<Order>().Status.Value);

        descriptor.Field(o => o.TotalAmount)
            .Type<NonNullType<DecimalType>>()
            .Resolve(ctx => ctx.Parent<Order>().TotalAmount.Amount);

        descriptor.Field(o => o.Lines)
            .Type<NonNullType<ListType<NonNullType<OrderLineType>>>>()
            .Resolve(ctx => ctx.Parent<Order>().Lines);
    }
}

public sealed class OrderLineType : ObjectType<OrderLine>
{
    protected override void Configure(IObjectTypeDescriptor<OrderLine> descriptor)
    {
        descriptor.Description("A line item in an order");

        descriptor.Field(l => l.ProductId)
            .Type<NonNullType<IdType>>()
            .Resolve(ctx => ctx.Parent<OrderLine>().ProductId.Value);

        descriptor.Field(l => l.ProductName).Type<NonNullType<StringType>>();
        descriptor.Field(l => l.Quantity).Type<NonNullType<IntType>>();

        descriptor.Field(l => l.UnitPrice)
            .Type<NonNullType<DecimalType>>()
            .Resolve(ctx => ctx.Parent<OrderLine>().UnitPrice.Amount);
    }
}

// ═══════════════════════════════════════════════════════════
// DATA LOADERS (N+1 Prevention)
// ═══════════════════════════════════════════════════════════

public sealed class ProductDataLoader : BatchDataLoader<Guid, Product>
{
    private readonly IDbContextFactory<OrderCatalogDbContext> _dbContextFactory;

    public ProductDataLoader(
        IDbContextFactory<OrderCatalogDbContext> dbContextFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options)
    {
        _dbContextFactory = dbContextFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, Product>> LoadBatchAsync(
        IReadOnlyList<Guid> keys,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.Products
            .AsNoTracking()
            .Where(p => keys.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
    }
}

public sealed class OrderDataLoader : BatchDataLoader<Guid, Order>
{
    private readonly IDbContextFactory<OrderCatalogDbContext> _dbContextFactory;

    public OrderDataLoader(
        IDbContextFactory<OrderCatalogDbContext> dbContextFactory,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options)
    {
        _dbContextFactory = dbContextFactory;
    }

    protected override async Task<IReadOnlyDictionary<Guid, Order>> LoadBatchAsync(
        IReadOnlyList<Guid> keys,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Where(o => keys.Contains(o.Id.Value))
            .ToDictionaryAsync(o => o.Id.Value, cancellationToken);
    }
}
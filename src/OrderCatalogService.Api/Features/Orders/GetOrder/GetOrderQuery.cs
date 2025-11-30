using Carter;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderCatalogService.Common.Behaviors;
using OrderCatalogService.Domain.Orders;
using OrderCatalogService.Infrastructure.Database;

namespace OrderCatalogService.Features.Orders.GetOrder;

// ═══════════════════════════════════════════════════════════
// QUERY (Read Model - Cacheable)
// ═══════════════════════════════════════════════════════════

public sealed record GetOrderQuery(Guid OrderId) : IRequest<OrderDetailDto?>, ICacheableQuery
{
    public string CacheKey => $"order:{OrderId}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

// ═══════════════════════════════════════════════════════════
// HANDLER (Optimized for Reading)
// ═══════════════════════════════════════════════════════════

public sealed class GetOrderHandler : IRequestHandler<GetOrderQuery, OrderDetailDto?>
{
    private readonly OrderCatalogDbContext _dbContext;
    private readonly ILogger<GetOrderHandler> _logger;

    public GetOrderHandler(OrderCatalogDbContext dbContext, ILogger<GetOrderHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<OrderDetailDto?> Handle(GetOrderQuery request, CancellationToken cancellationToken)
    {
        // Optimized query with AsNoTracking (read-only)
        var order = await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Where(o => o.Id == OrderId.From(request.OrderId))
            .Select(o => new OrderDetailDto
            {
                Id = o.Id.Value,
                OrderNumber = o.Id.ToString(),
                CustomerId = o.CustomerId.Value,
                Status = o.Status.Value,
                TotalAmount = o.TotalAmount.Amount,
                Currency = o.TotalAmount.Currency,
                ShippingAddress = new OrderAddressDto
                {
                    AddressLine1 = o.ShippingAddress.AddressLine1,
                    AddressLine2 = o.ShippingAddress.AddressLine2,
                    City = o.ShippingAddress.City,
                    StateOrProvince = o.ShippingAddress.StateOrProvince,
                    PostalCode = o.ShippingAddress.PostalCode,
                    Country = o.ShippingAddress.Country
                },
                Lines = o.Lines.Select(l => new OrderLineDto
                {
                    ProductId = l.ProductId.Value,
                    ProductName = l.ProductName,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice.Amount
                }).ToList(),
                CreatedAt = o.CreatedAt,
                CompletedAt = o.CompletedAt,
                CancelledAt = o.CancelledAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (order == null)
        {
            _logger.LogWarning("Order {OrderId} not found", request.OrderId);
        }

        return order;
    }
}

// ═══════════════════════════════════════════════════════════
// DTOs (Response Models)
// ═══════════════════════════════════════════════════════════

public sealed record OrderDetailDto
{
    public Guid Id { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public Guid CustomerId { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = "USD";
    public OrderAddressDto ShippingAddress { get; init; } = null!;
    public List<OrderLineDto> Lines { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
}

public sealed record OrderAddressDto
{
    public string AddressLine1 { get; init; } = string.Empty;
    public string? AddressLine2 { get; init; }
    public string City { get; init; } = string.Empty;
    public string StateOrProvince { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
}

public sealed record OrderLineDto
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}

// ═══════════════════════════════════════════════════════════
// ENDPOINT (Carter)
// ═══════════════════════════════════════════════════════════

public sealed class GetOrderEndpoint : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/orders/{orderId:guid}", async (
            Guid orderId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var query = new GetOrderQuery(orderId);
            var result = await sender.Send(query, cancellationToken);

            return result is not null
                ? Results.Ok(result)
                : Results.NotFound(new
                {
                    type = "https://httpstatuses.com/404",
                    title = "Order Not Found",
                    status = 404,
                    detail = $"Order with ID '{orderId}' was not found.",
                    instance = $"/api/v1/orders/{orderId}"
                });
        })
        .RequireAuthorization("OrderRead")
        .WithName("GetOrderById")
        .WithTags("Orders")
        .WithOpenApi()
        .Produces<OrderDetailDto>(StatusCodes.Status200OK)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound);
    }
}
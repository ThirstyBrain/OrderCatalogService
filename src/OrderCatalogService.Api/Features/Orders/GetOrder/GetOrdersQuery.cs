using Carter;
using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderCatalogService.Common.Behaviors;
using OrderCatalogService.Domain.Orders;
using OrderCatalogService.Infrastructure.Database;
using System.Text.Json;

namespace OrderCatalogService.Features.Orders.GetOrders;

// ═══════════════════════════════════════════════════════════
// QUERY (Cursor-Based Pagination)
// ═══════════════════════════════════════════════════════════

public sealed record GetOrdersQuery : IRequest<PagedOrdersResult>, ICacheableQuery
{
    public Guid? CustomerId { get; init; }
    public string? Status { get; init; }
    public string? Cursor { get; init; }
    public int PageSize { get; init; } = 20;

    public string CacheKey => $"orders:{CustomerId}:{Status}:{Cursor}:{PageSize}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(2);
}

// ═══════════════════════════════════════════════════════════
// HANDLER
// ═══════════════════════════════════════════════════════════

public sealed class GetOrdersHandler : IRequestHandler<GetOrdersQuery, PagedOrdersResult>
{
    private readonly OrderCatalogDbContext _dbContext;
    private readonly ILogger<GetOrdersHandler> _logger;

    public GetOrdersHandler(OrderCatalogDbContext dbContext, ILogger<GetOrdersHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<PagedOrdersResult> Handle(GetOrdersQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.Orders.AsNoTracking();

        // Apply filters
        if (request.CustomerId.HasValue)
        {
            query = query.Where(o => o.CustomerId == CustomerId.From(request.CustomerId.Value));
        }

        if (!string.IsNullOrEmpty(request.Status))
        {
            query = query.Where(o => o.Status == OrderStatus.FromString(request.Status));
        }

        // Cursor pagination (after specific ID)
        if (!string.IsNullOrEmpty(request.Cursor))
        {
            var cursorId = DecodeCursor(request.Cursor);
            if (cursorId.HasValue)
            {
                query = query.Where(o => o.Id.Value.CompareTo(cursorId.Value) > 0);
            }
        }

        // Order by ID for consistent pagination
        query = query.OrderBy(o => o.Id.Value);

        // Fetch one extra to determine if there's a next page
        var orders = await query
            .Take(request.PageSize + 1)
            .Select(o => new OrderSummaryDto
            {
                Id = o.Id.Value,
                OrderNumber = o.Id.ToString(),
                Status = o.Status.Value,
                TotalAmount = o.TotalAmount.Amount,
                Currency = o.TotalAmount.Currency,
                ItemCount = o.Lines.Count,
                CreatedAt = o.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var hasMore = orders.Count > request.PageSize;
        var items = hasMore ? orders.Take(request.PageSize).ToList() : orders;
        
        string? nextCursor = null;
        if (hasMore)
        {
            var lastId = items.Last().Id;
            nextCursor = EncodeCursor(lastId);
        }

        return new PagedOrdersResult
        {
            Items = items,
            NextCursor = nextCursor,
            HasMore = hasMore,
            TotalCount = items.Count
        };
    }

    private static Guid? DecodeCursor(string cursor)
    {
        try
        {
            var bytes = Convert.FromBase64String(cursor);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<Guid>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string EncodeCursor(Guid id)
    {
        var json = JsonSerializer.Serialize(id);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes);
    }
}

// ═══════════════════════════════════════════════════════════
// DTOs
// ═══════════════════════════════════════════════════════════

public sealed record OrderSummaryDto
{
    public Guid Id { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = "USD";
    public int ItemCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record PagedOrdersResult
{
    public List<OrderSummaryDto> Items { get; init; } = new();
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
    public int TotalCount { get; init; }
}

// ═══════════════════════════════════════════════════════════
// ENDPOINT
// ═══════════════════════════════════════════════════════════

public sealed class GetOrdersEndpoint : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/orders", async (
            Guid? customerId,
            string? status,
            string? cursor,
            int pageSize,
            ISender sender,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            pageSize = Math.Min(pageSize == 0 ? 20 : pageSize, 100); // Max 100

            var query = new GetOrdersQuery
            {
                CustomerId = customerId,
                Status = status,
                Cursor = cursor,
                PageSize = pageSize
            };

            var result = await sender.Send(query, cancellationToken);

            // Add Link header for pagination (RFC 8288)
            if (result.NextCursor != null)
            {
                var nextUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}?pageSize={pageSize}&cursor={result.NextCursor}";
                context.Response.Headers.Append("Link", $"<{nextUrl}>; rel=\"next\"");
            }

            return Results.Ok(result);
        })
        .RequireAuthorization("OrderRead")
        .WithName("GetOrders")
        .WithTags("Orders")
        .WithOpenApi()
        .Produces<PagedOrdersResult>(StatusCodes.Status200OK);
    }
}
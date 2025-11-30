using Carter;
using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderCatalogService.Common.Behaviors;
using OrderCatalogService.Infrastructure.Database;

namespace OrderCatalogService.Features.Products;

// ═══════════════════════════════════════════════════════════
// GET PRODUCTS (List with Pagination & Filters)
// ═══════════════════════════════════════════════════════════

public sealed record GetProductsQuery : IRequest<PagedProductsResult>, ICacheableQuery
{
    public string? Search { get; init; }
    public Guid? CategoryId { get; init; }
    public bool? IsAvailable { get; init; }
    public string? Cursor { get; init; }
    public int PageSize { get; init; } = 50;

    public string CacheKey => $"products:{Search}:{CategoryId}:{IsAvailable}:{Cursor}:{PageSize}";
    public TimeSpan CacheDuration => TimeSpan.FromHours(1);
}

public sealed class GetProductsHandler : IRequestHandler<GetProductsQuery, PagedProductsResult>
{
    private readonly OrderCatalogDbContext _dbContext;

    public GetProductsHandler(OrderCatalogDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedProductsResult> Handle(GetProductsQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.Products.AsNoTracking();

        // Apply filters
        if (!string.IsNullOrEmpty(request.Search))
        {
            query = query.Where(p => 
                EF.Functions.ILike(p.Name, $"%{request.Search}%") ||
                EF.Functions.ILike(p.Description, $"%{request.Search}%"));
        }

        if (request.CategoryId.HasValue)
        {
            query = query.Where(p => p.CategoryId == request.CategoryId.Value);
        }

        if (request.IsAvailable.HasValue)
        {
            query = query.Where(p => p.IsAvailable == request.IsAvailable.Value);
        }

        // Cursor pagination
        if (!string.IsNullOrEmpty(request.Cursor) && Guid.TryParse(request.Cursor, out var cursorId))
        {
            query = query.Where(p => p.Id.CompareTo(cursorId) > 0);
        }

        query = query.OrderBy(p => p.Id);

        var products = await query
            .Take(request.PageSize + 1)
            .Select(p => new ProductDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Price = p.Price,
                Currency = p.Currency,
                IsAvailable = p.IsAvailable,
                StockQuantity = p.StockQuantity
            })
            .ToListAsync(cancellationToken);

        var hasMore = products.Count > request.PageSize;
        var items = hasMore ? products.Take(request.PageSize).ToList() : products;
        
        return new PagedProductsResult
        {
            Items = items,
            NextCursor = hasMore ? items.Last().Id.ToString() : null,
            HasMore = hasMore
        };
    }
}

// ═══════════════════════════════════════════════════════════
// GET PRODUCT DETAILS (Single)
// ═══════════════════════════════════════════════════════════

public sealed record GetProductDetailsQuery(Guid ProductId) : IRequest<ProductDto?>, ICacheableQuery
{
    public string CacheKey => $"product:{ProductId}";
    public TimeSpan CacheDuration => TimeSpan.FromHours(1);
}

public sealed class GetProductDetailsHandler : IRequestHandler<GetProductDetailsQuery, ProductDto?>
{
    private readonly OrderCatalogDbContext _dbContext;

    public GetProductDetailsHandler(OrderCatalogDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ProductDto?> Handle(GetProductDetailsQuery request, CancellationToken cancellationToken)
    {
        return await _dbContext.Products
            .AsNoTracking()
            .Where(p => p.Id == request.ProductId)
            .Select(p => new ProductDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                Price = p.Price,
                Currency = p.Currency,
                IsAvailable = p.IsAvailable,
                StockQuantity = p.StockQuantity,
                CategoryId = p.CategoryId,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
    }
}

// ═══════════════════════════════════════════════════════════
// DTOs
// ═══════════════════════════════════════════════════════════

public sealed record ProductDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string Currency { get; init; } = "USD";
    public bool IsAvailable { get; init; }
    public int StockQuantity { get; init; }
    public Guid? CategoryId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PagedProductsResult
{
    public List<ProductDto> Items { get; init; } = new();
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

// ═══════════════════════════════════════════════════════════
// ENDPOINTS
// ═══════════════════════════════════════════════════════════

public sealed class ProductEndpoints : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/products").WithTags("Products");

        // GET /api/v1/products
        group.MapGet("/", async (
            string? search,
            Guid? categoryId,
            bool? isAvailable,
            string? cursor,
            int pageSize,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            pageSize = Math.Min(pageSize == 0 ? 50 : pageSize, 100);

            var query = new GetProductsQuery
            {
                Search = search,
                CategoryId = categoryId,
                IsAvailable = isAvailable,
                Cursor = cursor,
                PageSize = pageSize
            };

            var result = await sender.Send(query, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("GetProducts")
        .Produces<PagedProductsResult>(StatusCodes.Status200OK);

        // GET /api/v1/products/{id}
        group.MapGet("/{id:guid}", async (
            Guid id,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var query = new GetProductDetailsQuery(id);
            var result = await sender.Send(query, cancellationToken);

            return result is not null
                ? Results.Ok(result)
                : Results.NotFound();
        })
        .WithName("GetProductById")
        .Produces<ProductDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }
}
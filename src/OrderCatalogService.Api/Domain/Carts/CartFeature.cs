using Carter;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderCatalogService.Common.Behaviors;
using OrderCatalogService.Infrastructure.Database;

namespace OrderCatalogService.Features.Carts;

// ═══════════════════════════════════════════════════════════
// CREATE CART
// ═══════════════════════════════════════════════════════════

public sealed record CreateCartCommand : IRequest<CartDto>, ICommand
{
    public string SessionId { get; init; } = string.Empty;
    public Guid? CustomerId { get; init; }
}

public sealed class CreateCartValidator : AbstractValidator<CreateCartCommand>
{
    public CreateCartValidator()
    {
        RuleFor(x => x.SessionId)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public sealed class CreateCartHandler : IRequestHandler<CreateCartCommand, CartDto>
{
    private readonly OrderCatalogDbContext _dbContext;

    public CreateCartHandler(OrderCatalogDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CartDto> Handle(CreateCartCommand request, CancellationToken cancellationToken)
    {
        var cart = new Cart
        {
            Id = Guid.NewGuid(),
            SessionId = request.SessionId,
            CustomerId = request.CustomerId,
            Items = new List<CartItem>(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) // 7-day TTL
        };

        await _dbContext.Carts.AddAsync(cart, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CartDto
        {
            Id = cart.Id,
            SessionId = cart.SessionId,
            CustomerId = cart.CustomerId,
            Items = new List<CartItemDto>(),
            CreatedAt = cart.CreatedAt,
            ExpiresAt = cart.ExpiresAt
        };
    }
}

// ═══════════════════════════════════════════════════════════
// ADD TO CART
// ═══════════════════════════════════════════════════════════

public sealed record AddToCartCommand : IRequest<CartDto>, ICommand
{
    public Guid CartId { get; init; }
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
}

public sealed class AddToCartValidator : AbstractValidator<AddToCartCommand>
{
    public AddToCartValidator()
    {
        RuleFor(x => x.CartId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(99);
    }
}

public sealed class AddToCartHandler : IRequestHandler<AddToCartCommand, CartDto>
{
    private readonly OrderCatalogDbContext _dbContext;

    public AddToCartHandler(OrderCatalogDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CartDto> Handle(AddToCartCommand request, CancellationToken cancellationToken)
    {
        var cart = await _dbContext.Carts
            .FirstOrDefaultAsync(c => c.Id == request.CartId, cancellationToken);

        if (cart == null)
        {
            throw new KeyNotFoundException($"Cart {request.CartId} not found");
        }

        // Check if product exists and is available
        var product = await _dbContext.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);

        if (product == null || !product.IsAvailable)
        {
            throw new InvalidOperationException($"Product {request.ProductId} is not available");
        }

        if (product.StockQuantity < request.Quantity)
        {
            throw new InvalidOperationException($"Insufficient stock for product {product.Name}");
        }

        // Add or update cart item
        var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == request.ProductId);
        
        if (existingItem != null)
        {
            existingItem.Quantity += request.Quantity;
        }
        else
        {
            cart.Items.Add(new CartItem
            {
                ProductId = request.ProductId,
                Quantity = request.Quantity,
                AddedAt = DateTimeOffset.UtcNow
            });
        }

        cart.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CartDto
        {
            Id = cart.Id,
            SessionId = cart.SessionId,
            CustomerId = cart.CustomerId,
            Items = cart.Items.Select(i => new CartItemDto
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity,
                AddedAt = i.AddedAt
            }).ToList(),
            CreatedAt = cart.CreatedAt,
            ExpiresAt = cart.ExpiresAt
        };
    }
}

// ═══════════════════════════════════════════════════════════
// GET CART
// ═══════════════════════════════════════════════════════════

public sealed record GetCartQuery(Guid CartId) : IRequest<CartDto?>;

public sealed class GetCartHandler : IRequestHandler<GetCartQuery, CartDto?>
{
    private readonly OrderCatalogDbContext _dbContext;

    public GetCartHandler(OrderCatalogDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CartDto?> Handle(GetCartQuery request, CancellationToken cancellationToken)
    {
        var cart = await _dbContext.Carts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.CartId, cancellationToken);

        if (cart == null)
        {
            return null;
        }

        return new CartDto
        {
            Id = cart.Id,
            SessionId = cart.SessionId,
            CustomerId = cart.CustomerId,
            Items = cart.Items.Select(i => new CartItemDto
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity,
                AddedAt = i.AddedAt
            }).ToList(),
            CreatedAt = cart.CreatedAt,
            ExpiresAt = cart.ExpiresAt
        };
    }
}

// ═══════════════════════════════════════════════════════════
// DTOs
// ═══════════════════════════════════════════════════════════

public sealed record CartDto
{
    public Guid Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public Guid? CustomerId { get; init; }
    public List<CartItemDto> Items { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record CartItemDto
{
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

// ═══════════════════════════════════════════════════════════
// ENDPOINTS
// ═══════════════════════════════════════════════════════════

public sealed class CartEndpoints : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/carts").WithTags("Carts");

        // POST /api/v1/carts
        group.MapPost("/", async (
            [FromBody] CreateCartRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var command = new CreateCartCommand
            {
                SessionId = request.SessionId,
                CustomerId = request.CustomerId
            };

            var result = await sender.Send(command, cancellationToken);
            return Results.Created($"/api/v1/carts/{result.Id}", result);
        })
        .WithName("CreateCart")
        .Produces<CartDto>(StatusCodes.Status201Created);

        // PUT /api/v1/carts/{cartId}/items
        group.MapPut("/{cartId:guid}/items", async (
            Guid cartId,
            [FromBody] AddToCartRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var command = new AddToCartCommand
            {
                CartId = cartId,
                ProductId = request.ProductId,
                Quantity = request.Quantity
            };

            try
            {
                var result = await sender.Send(command, cancellationToken);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("AddToCart")
        .Produces<CartDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // GET /api/v1/carts/{cartId}
        group.MapGet("/{cartId:guid}", async (
            Guid cartId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var query = new GetCartQuery(cartId);
            var result = await sender.Send(query, cancellationToken);

            return result is not null
                ? Results.Ok(result)
                : Results.NotFound();
        })
        .WithName("GetCart")
        .Produces<CartDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }
}

// ═══════════════════════════════════════════════════════════
// REQUEST DTOs
// ═══════════════════════════════════════════════════════════

public sealed record CreateCartRequest(string SessionId, Guid? CustomerId);
public sealed record AddToCartRequest(Guid ProductId, int Quantity);
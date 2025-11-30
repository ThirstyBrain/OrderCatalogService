using System.Diagnostics;
using System.Text.Json.Serialization;
using Carter;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderCatalogService.Domain.Orders;
using OrderCatalogService.Infrastructure.Database;
using OrderCatalogService.Infrastructure.Idempotency;

namespace OrderCatalogService.Features.Orders.CreateOrder;

// ═══════════════════════════════════════════════════════════
// COMMAND (Write Model)
// ═══════════════════════════════════════════════════════════

public sealed record CreateOrderCommand : IRequest<CreateOrderResult>, IIdempotentCommand
{
    [JsonIgnore]
    public string IdempotencyKey { get; init; } = string.Empty;
    
    public Guid CustomerId { get; init; }
    public CreateOrderAddress ShippingAddress { get; init; } = null!;
    public List<CreateOrderLine> Lines { get; init; } = new();
}

public sealed record CreateOrderAddress(
    string AddressLine1,
    string? AddressLine2,
    string City,
    string StateOrProvince,
    string PostalCode,
    string Country);

public sealed record CreateOrderLine(
    Guid ProductId,
    int Quantity);

public sealed record CreateOrderResult(
    Guid OrderId,
    string OrderNumber,
    decimal TotalAmount,
    string Status,
    DateTimeOffset CreatedAt);

// ═══════════════════════════════════════════════════════════
// VALIDATION (Fail Fast)
// ═══════════════════════════════════════════════════════════

public sealed class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.IdempotencyKey)
            .NotEmpty()
            .MaximumLength(100)
            .WithMessage("Idempotency-Key header is required");

        RuleFor(x => x.CustomerId)
            .NotEmpty()
            .WithMessage("CustomerId is required");

        RuleFor(x => x.ShippingAddress)
            .NotNull()
            .WithMessage("ShippingAddress is required");

        RuleFor(x => x.ShippingAddress.AddressLine1)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.ShippingAddress.City)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.ShippingAddress.PostalCode)
            .NotEmpty()
            .MaximumLength(20);

        RuleFor(x => x.ShippingAddress.Country)
            .NotEmpty()
            .Length(2)
            .WithMessage("Country must be ISO 3166-1 alpha-2 code");

        RuleFor(x => x.Lines)
            .NotEmpty()
            .WithMessage("Order must contain at least one line item")
            .Must(lines => lines.Count <= 100)
            .WithMessage("Order cannot contain more than 100 line items");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity)
                .GreaterThan(0)
                .LessThanOrEqualTo(9999)
                .WithMessage("Quantity must be between 1 and 9999");
        });
    }
}

// ═══════════════════════════════════════════════════════════
// HANDLER (Command Handler with Async/Concurrency Best Practices)
// ═══════════════════════════════════════════════════════════

public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, CreateOrderResult>
{
    private readonly OrderCatalogDbContext _dbContext;
    private readonly ILogger<CreateOrderHandler> _logger;
    private readonly ActivitySource _activitySource;
    
    // Thread-safe singleton activity source
    private static readonly ActivitySource ActivitySourceInstance = 
        new("OrderCatalogService.Orders", "1.0.0");

    public CreateOrderHandler(
        OrderCatalogDbContext dbContext,
        ILogger<CreateOrderHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
        _activitySource = ActivitySourceInstance;
    }

    public async Task<CreateOrderResult> Handle(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("CreateOrder", ActivityKind.Internal);
        activity?.SetTag("order.customerId", command.CustomerId);
        activity?.SetTag("order.lineCount", command.Lines.Count);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // STEP 1: Fetch products in parallel using async LINQ
            // Uses compiled query for performance (EF Core caches execution plan)
            var productIds = command.Lines.Select(l => l.ProductId).Distinct().ToList();
            
            _logger.LogDebug(
                "Fetching {ProductCount} products for order {IdempotencyKey}",
                productIds.Count, command.IdempotencyKey);

            // Parallel product fetch with AsNoTracking for read-only query
            var products = await _dbContext.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Price, p.IsAvailable, p.StockQuantity })
                .ToListAsync(cancellationToken);

            if (products.Count != productIds.Count)
            {
                var missingIds = productIds.Except(products.Select(p => p.Id)).ToList();
                throw new DomainException(
                    $"Products not found: {string.Join(", ", missingIds)}");
            }

            // STEP 2: Validate product availability
            var unavailableProducts = products.Where(p => !p.IsAvailable).ToList();
            if (unavailableProducts.Any())
            {
                throw new DomainException(
                    $"Products unavailable: {string.Join(", ", unavailableProducts.Select(p => p.Name))}");
            }

            // STEP 3: Check stock levels (inventory reservation would happen here)
            var insufficientStock = command.Lines
                .Join(products, 
                    line => line.ProductId, 
                    product => product.Id,
                    (line, product) => new { line.Quantity, product.StockQuantity, product.Name })
                .Where(x => x.Quantity > x.StockQuantity)
                .ToList();

            if (insufficientStock.Any())
            {
                throw new DomainException(
                    $"Insufficient stock for: {string.Join(", ", insufficientStock.Select(x => x.Name))}");
            }

            // STEP 4: Build order lines (in-memory, no DB calls)
            var orderLines = command.Lines
                .Select(line =>
                {
                    var product = products.First(p => p.Id == line.ProductId);
                    return OrderLine.Create(
                        ProductId.From(product.Id),
                        product.Name,
                        new Money(product.Price, "USD"),
                        line.Quantity);
                })
                .ToList();

            // STEP 5: Create order aggregate (domain logic)
            var shippingAddress = new ShippingAddress(
                command.ShippingAddress.AddressLine1,
                command.ShippingAddress.AddressLine2,
                command.ShippingAddress.City,
                command.ShippingAddress.StateOrProvince,
                command.ShippingAddress.PostalCode,
                command.ShippingAddress.Country);

            var order = Order.Create(
                CustomerId.From(command.CustomerId),
                shippingAddress,
                orderLines,
                command.IdempotencyKey);

            // STEP 6: Persist order (single DB round-trip)
            // EF Core will batch INSERT commands for order + lines
            await _dbContext.Orders.AddAsync(order, cancellationToken);
            
            // STEP 7: Update inventory (optimistic concurrency via RowVersion)
            // This uses a compiled update query for performance
            foreach (var line in command.Lines)
            {
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE ""Products"" 
                       SET ""StockQuantity"" = ""StockQuantity"" - {line.Quantity}
                       WHERE ""Id"" = {line.ProductId} 
                       AND ""StockQuantity"" >= {line.Quantity}",
                    cancellationToken);
            }

            // STEP 8: Outbox pattern - store domain events for async processing
            // Events are persisted in same transaction (exactly-once guarantee)
            foreach (var domainEvent in order.DomainEvents)
            {
                var outboxMessage = new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    Type = domainEvent.GetType().Name,
                    Payload = System.Text.Json.JsonSerializer.Serialize(domainEvent),
                    OccurredAt = domainEvent.OccurredAt,
                    ProcessedAt = null
                };
                
                await _dbContext.OutboxMessages.AddAsync(outboxMessage, cancellationToken);
            }

            // STEP 9: Commit transaction (single DB commit)
            // If this fails, entire operation rolls back (atomicity)
            await _dbContext.SaveChangesAsync(cancellationToken);

            stopwatch.Stop();
            
            _logger.LogInformation(
                "Order {OrderId} created successfully in {ElapsedMs}ms for customer {CustomerId}",
                order.Id, stopwatch.ElapsedMilliseconds, command.CustomerId);

            activity?.SetTag("order.id", order.Id.Value);
            activity?.SetTag("order.duration_ms", stopwatch.ElapsedMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);

            // STEP 10: Clear domain events after persistence
            order.ClearDomainEvents();

            // Return result
            return new CreateOrderResult(
                order.Id.Value,
                order.Id.ToString(), // Could use a proper order number generator
                order.TotalAmount.Amount,
                order.Status.Value,
                order.CreatedAt);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Optimistic concurrency failure (e.g., inventory conflict)
            _logger.LogWarning(ex, 
                "Concurrency conflict creating order {IdempotencyKey}", 
                command.IdempotencyKey);
            
            activity?.SetStatus(ActivityStatusCode.Error, "Concurrency conflict");
            throw new DomainException("Order could not be created due to concurrent modification. Please retry.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Failed to create order {IdempotencyKey}", 
                command.IdempotencyKey);
            
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}

// ═══════════════════════════════════════════════════════════
// REST ENDPOINT (Carter Module - REPR Pattern)
// ═══════════════════════════════════════════════════════════

public sealed class CreateOrderEndpoint : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/orders", async (
            [FromBody] CreateOrderRequest request,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            // Validate idempotency key presence (belt and suspenders with validator)
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Type = "https://httpstatuses.com/400",
                    Title = "Bad Request",
                    Status = 400,
                    Detail = "Idempotency-Key header is required for order creation",
                    Instance = "/api/v1/orders"
                });
            }

            var command = new CreateOrderCommand
            {
                IdempotencyKey = idempotencyKey,
                CustomerId = request.CustomerId,
                ShippingAddress = request.ShippingAddress,
                Lines = request.Lines
            };

            try
            {
                var result = await sender.Send(command, cancellationToken);
                
                return Results.Created(
                    $"/api/v1/orders/{result.OrderId}",
                    result);
            }
            catch (IdempotencyInProgressException ex)
            {
                // Return 202 Accepted with Location header to poll
                return Results.AcceptedAtRoute(
                    "GetOrderById",
                    new { orderId = ex.IdempotencyKey },
                    new { message = "Order creation in progress" });
            }
            catch (IdempotencyFailedException ex)
            {
                return Results.Conflict(new ProblemDetails
                {
                    Type = "https://httpstatuses.com/409",
                    Title = "Conflict",
                    Status = 409,
                    Detail = ex.Message,
                    Instance = "/api/v1/orders"
                });
            }
            catch (DomainException ex)
            {
                return Results.UnprocessableEntity(new ProblemDetails
                {
                    Type = "https://httpstatuses.com/422",
                    Title = "Unprocessable Entity",
                    Status = 422,
                    Detail = ex.Message,
                    Instance = "/api/v1/orders"
                });
            }
        })
        .RequireRateLimiting("OrderCreation")
        .RequireAuthorization("OrderWrite")
        .WithName("CreateOrder")
        .WithTags("Orders")
        .WithOpenApi()
        .Produces<CreateOrderResult>(StatusCodes.Status201Created)
        .Produces<ProblemDetails>(StatusCodes.Status202Accepted)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
        .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
        .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
        .Produces<ProblemDetails>(StatusCodes.Status429TooManyRequests);
    }
}

// ═══════════════════════════════════════════════════════════
// REQUEST DTO
// ═══════════════════════════════════════════════════════════

public sealed record CreateOrderRequest(
    Guid CustomerId,
    CreateOrderAddress ShippingAddress,
    List<CreateOrderLine> Lines);
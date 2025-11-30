using Carter;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderCatalogService.Common.Behaviors;
using OrderCatalogService.Domain.Orders;
using OrderCatalogService.Infrastructure.Database;

namespace OrderCatalogService.Features.Orders.CancelOrder;

// ═══════════════════════════════════════════════════════════
// COMMAND
// ═══════════════════════════════════════════════════════════

public sealed record CancelOrderCommand : IRequest<CancelOrderResult>, ICommand
{
    public Guid OrderId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public Guid RequestingUserId { get; init; } // For authorization
}

public sealed record CancelOrderResult(
    Guid OrderId,
    string Status,
    DateTimeOffset CancelledAt);

// ═══════════════════════════════════════════════════════════
// VALIDATION
// ═══════════════════════════════════════════════════════════

public sealed class CancelOrderValidator : AbstractValidator<CancelOrderCommand>
{
    public CancelOrderValidator()
    {
        RuleFor(x => x.OrderId)
            .NotEmpty()
            .WithMessage("OrderId is required");

        RuleFor(x => x.Reason)
            .NotEmpty()
            .WithMessage("Cancellation reason is required")
            .MaximumLength(500)
            .WithMessage("Reason cannot exceed 500 characters");
    }
}

// ═══════════════════════════════════════════════════════════
// HANDLER (With Optimistic Concurrency)
// ═══════════════════════════════════════════════════════════

public sealed class CancelOrderHandler : IRequestHandler<CancelOrderCommand, CancelOrderResult>
{
    private readonly OrderCatalogDbContext _dbContext;
    private readonly ILogger<CancelOrderHandler> _logger;

    public CancelOrderHandler(OrderCatalogDbContext dbContext, ILogger<CancelOrderHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<CancelOrderResult> Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        // Fetch order with tracking (for update)
        var order = await _dbContext.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == OrderId.From(request.OrderId), cancellationToken);

        if (order == null)
        {
            throw new KeyNotFoundException($"Order {request.OrderId} not found");
        }

        // Authorization check (simplified - in production use proper policy)
        if (order.CustomerId.Value != request.RequestingUserId)
        {
            throw new UnauthorizedAccessException("You can only cancel your own orders");
        }

        try
        {
            // Domain logic handles state validation
            order.Cancel(request.Reason);

            // Store cancellation event in outbox
            var cancellationEvent = order.DomainEvents
                .OfType<OrderCancelledEvent>()
                .FirstOrDefault();

            if (cancellationEvent != null)
            {
                var outboxMessage = new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    Type = nameof(OrderCancelledEvent),
                    Payload = System.Text.Json.JsonSerializer.Serialize(cancellationEvent),
                    OccurredAt = cancellationEvent.OccurredAt,
                    ProcessedAt = null
                };

                await _dbContext.OutboxMessages.AddAsync(outboxMessage, cancellationToken);
            }

            // Save changes (optimistic concurrency check happens here)
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Order {OrderId} cancelled successfully by user {UserId}",
                request.OrderId, request.RequestingUserId);

            order.ClearDomainEvents();

            return new CancelOrderResult(
                order.Id.Value,
                order.Status.Value,
                order.CancelledAt!.Value);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(ex,
                "Domain validation failed for cancelling order {OrderId}",
                request.OrderId);
            throw;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex,
                "Concurrency conflict when cancelling order {OrderId}",
                request.OrderId);

            throw new InvalidOperationException(
                "The order was modified by another request. Please refresh and try again.", ex);
        }
    }
}

// ═══════════════════════════════════════════════════════════
// ENDPOINT
// ═══════════════════════════════════════════════════════════

public sealed class CancelOrderEndpoint : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPatch("/api/v1/orders/{orderId:guid}/cancel", async (
            Guid orderId,
            [FromBody] CancelOrderRequest request,
            ISender sender,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            // Extract user ID from JWT claims
            var userIdClaim = context.User.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Results.Unauthorized();
            }

            var command = new CancelOrderCommand
            {
                OrderId = orderId,
                Reason = request.Reason,
                RequestingUserId = userId
            };

            try
            {
                var result = await sender.Send(command, cancellationToken);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Type = "https://httpstatuses.com/404",
                    Title = "Order Not Found",
                    Status = 404,
                    Detail = $"Order with ID '{orderId}' was not found.",
                    Instance = $"/api/v1/orders/{orderId}/cancel"
                });
            }
            catch (DomainException ex)
            {
                return Results.Conflict(new ProblemDetails
                {
                    Type = "https://httpstatuses.com/409",
                    Title = "Cannot Cancel Order",
                    Status = 409,
                    Detail = ex.Message,
                    Instance = $"/api/v1/orders/{orderId}/cancel"
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Forbid();
            }
        })
        .RequireAuthorization("OrderCancel")
        .WithName("CancelOrder")
        .WithTags("Orders")
        .WithOpenApi()
        .Produces<CancelOrderResult>(StatusCodes.Status200OK)
        .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
        .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status403Forbidden);
    }
}

// ═══════════════════════════════════════════════════════════
// REQUEST DTO
// ═══════════════════════════════════════════════════════════

public sealed record CancelOrderRequest(string Reason);
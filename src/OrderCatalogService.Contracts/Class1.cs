// ═══════════════════════════════════════════════════════════
// FILE: OrderCatalogService.Contracts.csproj
// ═══════════════════════════════════════════════════════════

/*
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
*/

namespace OrderCatalogService.Contracts.Requests;

// ═══════════════════════════════════════════════════════════
// ORDER REQUESTS
// ═══════════════════════════════════════════════════════════

public sealed record CreateOrderRequest
{
    public Guid CustomerId { get; init; }
    public CreateOrderAddressRequest ShippingAddress { get; init; } = null!;
    public List<CreateOrderLineRequest> Lines { get; init; } = new();
}

public sealed record CreateOrderAddressRequest
{
    public string AddressLine1 { get; init; } = string.Empty;
    public string? AddressLine2 { get; init; }
    public string City { get; init; } = string.Empty;
    public string StateOrProvince { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
}

public sealed record CreateOrderLineRequest
{
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
}

public sealed record CancelOrderRequest
{
    public string Reason { get; init; } = string.Empty;
}

// ═══════════════════════════════════════════════════════════
// CART REQUESTS
// ═══════════════════════════════════════════════════════════

public sealed record CreateCartRequest
{
    public string SessionId { get; init; } = string.Empty;
    public Guid? CustomerId { get; init; }
}

public sealed record AddToCartRequest
{
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
}

// ═══════════════════════════════════════════════════════════
// WEBHOOK REQUESTS
// ═══════════════════════════════════════════════════════════

public sealed record RegisterWebhookRequest
{
    public string Url { get; init; } = string.Empty;
    public List<string> Events { get; init; } = new();
}

namespace OrderCatalogService.Contracts.Responses;

// ═══════════════════════════════════════════════════════════
// ORDER RESPONSES
// ═══════════════════════════════════════════════════════════

public sealed record OrderResponse
{
    public Guid Id { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public Guid CustomerId { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = "USD";
    public OrderAddressResponse ShippingAddress { get; init; } = null!;
    public List<OrderLineResponse> Lines { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
}

public sealed record OrderAddressResponse
{
    public string AddressLine1 { get; init; } = string.Empty;
    public string? AddressLine2 { get; init; }
    public string City { get; init; } = string.Empty;
    public string StateOrProvince { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
}

public sealed record OrderLineResponse
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}

public sealed record OrderListResponse
{
    public List<OrderSummaryResponse> Items { get; init; } = new();
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

public sealed record OrderSummaryResponse
{
    public Guid Id { get; init; }
    public string OrderNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public int ItemCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// ═══════════════════════════════════════════════════════════
// PRODUCT RESPONSES
// ═══════════════════════════════════════════════════════════

public sealed record ProductResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string Currency { get; init; } = "USD";
    public bool IsAvailable { get; init; }
    public int StockQuantity { get; init; }
    public Guid? CategoryId { get; init; }
}

public sealed record ProductListResponse
{
    public List<ProductResponse> Items { get; init; } = new();
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

// ═══════════════════════════════════════════════════════════
// CART RESPONSES
// ═══════════════════════════════════════════════════════════

public sealed record CartResponse
{
    public Guid Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public Guid? CustomerId { get; init; }
    public List<CartItemResponse> Items { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record CartItemResponse
{
    public Guid ProductId { get; init; }
    public int Quantity { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

// ═══════════════════════════════════════════════════════════
// ERROR RESPONSES
// ═══════════════════════════════════════════════════════════

public sealed record ErrorResponse
{
    public string Type { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int Status { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string Instance { get; init; } = string.Empty;
    public Dictionary<string, object>? Extensions { get; init; }
}

public sealed record ValidationErrorResponse : ErrorResponse
{
    public Dictionary<string, string[]> Errors { get; init; } = new();
}
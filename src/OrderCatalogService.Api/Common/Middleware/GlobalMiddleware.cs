using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using OrderCatalogService.Domain.Orders;

namespace OrderCatalogService.Common.Middleware;

// ═══════════════════════════════════════════════════════════
// CORRELATION ID MIDDLEWARE (Distributed Tracing)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Ensures every request has a correlation ID for distributed tracing
/// Uses W3C Trace Context standard (traceparent header)
/// Thread-safe: uses AsyncLocal for async context flow
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;
    
    // Thread-safe correlation ID storage per async context
    private static readonly AsyncLocal<string> _correlationId = new();
    
    private const string CorrelationIdHeaderName = "X-Correlation-ID";
    private const string TraceParentHeaderName = "traceparent";

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Extract or generate correlation ID
        var correlationId = ExtractCorrelationId(context);
        
        // Store in AsyncLocal for access throughout request pipeline
        _correlationId.Value = correlationId;
        
        // Add to HttpContext for easy access
        context.Items["CorrelationId"] = correlationId;
        
        // Add to response headers
        context.Response.Headers.TryAdd(CorrelationIdHeaderName, correlationId);
        
        // Add to logging scope
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestPath"] = context.Request.Path,
            ["RequestMethod"] = context.Request.Method
        }))
        {
            await _next(context);
        }
    }

    private static string ExtractCorrelationId(HttpContext context)
    {
        // Priority 1: W3C Trace Context (traceparent)
        if (context.Request.Headers.TryGetValue(TraceParentHeaderName, out var traceparent))
        {
            var parts = traceparent.ToString().Split('-');
            if (parts.Length >= 2)
            {
                return parts[1]; // Trace ID
            }
        }

        // Priority 2: Custom correlation header
        if (context.Request.Headers.TryGetValue(CorrelationIdHeaderName, out var correlationId))
        {
            return correlationId.ToString();
        }

        // Priority 3: ASP.NET Core TraceIdentifier
        if (!string.IsNullOrEmpty(context.TraceIdentifier))
        {
            return context.TraceIdentifier;
        }

        // Fallback: Generate new ID
        return Activity.Current?.Id ?? Guid.NewGuid().ToString();
    }

    public static string GetCorrelationId() => _correlationId.Value ?? string.Empty;
}

// ═══════════════════════════════════════════════════════════
// GLOBAL EXCEPTION MIDDLEWARE (RFC 7807 Problem Details)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Centralized exception handling with RFC 7807 Problem Details
/// Handles all exceptions and returns consistent error responses
/// Includes correlation IDs for debugging
/// Thread-safe: no shared state
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _env;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var correlationId = CorrelationIdMiddleware.GetCorrelationId();
        
        _logger.LogError(exception,
            "Unhandled exception occurred. CorrelationId: {CorrelationId}, Path: {Path}",
            correlationId, context.Request.Path);

        // Map exception to status code and problem details
        var (statusCode, problemDetails) = MapExceptionToProblemDetails(exception, context, correlationId);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        // Add security headers
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        await context.Response.WriteAsJsonAsync(problemDetails, JsonOptions);
    }

    private (int StatusCode, ProblemDetails ProblemDetails) MapExceptionToProblemDetails(
        Exception exception,
        HttpContext context,
        string correlationId)
    {
        return exception switch
        {
            // Domain exceptions (business rule violations)
            DomainException domainEx => (
                StatusCodes.Status422UnprocessableEntity,
                new ProblemDetails
                {
                    Type = "https://httpstatuses.com/422",
                    Title = "Business Rule Violation",
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Detail = domainEx.Message,
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["correlationId"] = correlationId,
                        ["timestamp"] = DateTimeOffset.UtcNow
                    }
                }),

            // Validation exceptions (input validation failures)
            ValidationException validationEx => (
                StatusCodes.Status400BadRequest,
                new ValidationProblemDetails(
                    validationEx.Errors
                        .GroupBy(e => e.PropertyName)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Select(e => e.ErrorMessage).ToArray()))
                {
                    Type = "https://httpstatuses.com/400",
                    Title = "Validation Failed",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "One or more validation errors occurred",
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["correlationId"] = correlationId,
                        ["timestamp"] = DateTimeOffset.UtcNow
                    }
                }),

            // Not found exceptions
            KeyNotFoundException or InvalidOperationException { Message: var msg } when msg.Contains("not found") => (
                StatusCodes.Status404NotFound,
                new ProblemDetails
                {
                    Type = "https://httpstatuses.com/404",
                    Title = "Resource Not Found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = exception.Message,
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["correlationId"] = correlationId,
                        ["timestamp"] = DateTimeOffset.UtcNow
                    }
                }),

            // Concurrency conflicts
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (
                StatusCodes.Status409Conflict,
                new ProblemDetails
                {
                    Type = "https://httpstatuses.com/409",
                    Title = "Concurrency Conflict",
                    Status = StatusCodes.Status409Conflict,
                    Detail = "The resource was modified by another request. Please retry.",
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["correlationId"] = correlationId,
                        ["timestamp"] = DateTimeOffset.UtcNow,
                        ["retryable"] = true
                    }
                }),

            // Unauthorized
            UnauthorizedAccessException => (
                StatusCodes.Status401Unauthorized,
                new ProblemDetails
                {
                    Type = "https://httpstatuses.com/401",
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "Authentication is required to access this resource",
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["correlationId"] = correlationId,
                        ["timestamp"] = DateTimeOffset.UtcNow
                    }
                }),

            // Timeout exceptions
            TimeoutException or TaskCanceledException => (
                StatusCodes.Status504GatewayTimeout,
                new ProblemDetails
                {
                    Type = "https://httpstatuses.com/504",
                    Title = "Request Timeout",
                    Status = StatusCodes.Status504GatewayTimeout,
                    Detail = "The request took too long to process",
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["correlationId"] = correlationId,
                        ["timestamp"] = DateTimeOffset.UtcNow,
                        ["retryable"] = true
                    }
                }),

            // Rate limiting
            InvalidOperationException { Message: var msg } when msg.Contains("rate limit") => (
                StatusCodes.Status429TooManyRequests,
                new ProblemDetails
                {
                    Type = "https://httpstatuses.com/429",
                    Title = "Too Many Requests",
                    Status = StatusCodes.Status429TooManyRequests,
                    Detail = exception.Message,
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["correlationId"] = correlationId,
                        ["timestamp"] = DateTimeOffset.UtcNow,
                        ["retryAfterSeconds"] = 60
                    }
                }),

            // Generic server errors
            _ => (
                StatusCodes.Status500InternalServerError,
                new ProblemDetails
                {
                    Type = "https://httpstatuses.com/500",
                    Title = "Internal Server Error",
                    Status = StatusCodes.Status500InternalServerError,
                    Detail = _env.IsDevelopment() 
                        ? exception.ToString() 
                        : "An unexpected error occurred. Please contact support with the correlation ID.",
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        ["correlationId"] = correlationId,
                        ["timestamp"] = DateTimeOffset.UtcNow
                    }
                })
        };
    }
}

// ═══════════════════════════════════════════════════════════
// REQUEST LOGGING MIDDLEWARE (Performance Tracking)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Logs request/response details for monitoring and debugging
/// Tracks execution time and response status
/// Thread-safe: uses Stopwatch per request
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId();

        try
        {
            await _next(context);
            
            stopwatch.Stop();

            _logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms - CorrelationId: {CorrelationId}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                correlationId);
        }
        catch
        {
            stopwatch.Stop();
            
            _logger.LogError(
                "HTTP {Method} {Path} failed after {ElapsedMs}ms - CorrelationId: {CorrelationId}",
                context.Request.Method,
                context.Request.Path,
                stopwatch.ElapsedMilliseconds,
                correlationId);
            
            throw;
        }
    }
}

// ═══════════════════════════════════════════════════════════
// RATE LIMIT RESPONSE MIDDLEWARE (Headers)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Adds rate limit information to response headers
/// Complies with draft IETF standard for rate limit headers
/// </summary>
public sealed class RateLimitHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public RateLimitHeaderMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        // Add rate limit headers to all responses
        if (!context.Response.Headers.ContainsKey("X-RateLimit-Limit"))
        {
            context.Response.Headers["X-RateLimit-Limit"] = "100";
            context.Response.Headers["X-RateLimit-Remaining"] = "99";
            context.Response.Headers["X-RateLimit-Reset"] = 
                DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString();
        }
    }
}

// ═══════════════════════════════════════════════════════════
// SECURITY HEADERS MIDDLEWARE
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Adds security headers to all responses
/// OWASP recommended security headers
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Add security headers
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        
        // Remove server header for security
        context.Response.Headers.Remove("Server");

        await _next(context);
    }
}

// ═══════════════════════════════════════════════════════════
// CONCURRENT REQUEST LIMITER (Per-Endpoint Protection)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Limits concurrent requests per endpoint using SemaphoreSlim
/// Protects against thread pool exhaustion
/// Thread-safe: uses ConcurrentDictionary for endpoint tracking
/// </summary>
public sealed class ConcurrentRequestLimiterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ConcurrentRequestLimiterMiddleware> _logger;
    
    // Thread-safe endpoint-specific semaphores
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    
    private const int MaxConcurrentRequests = 1000;

    public ConcurrentRequestLimiterMiddleware(
        RequestDelegate next,
        ILogger<ConcurrentRequestLimiterMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint()?.DisplayName ?? "unknown";
        
        // Get or create semaphore for this endpoint
        var semaphore = _semaphores.GetOrAdd(endpoint, 
            _ => new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests));

        var acquired = await semaphore.WaitAsync(TimeSpan.FromSeconds(30));
        
        if (!acquired)
        {
            _logger.LogWarning(
                "Concurrent request limit exceeded for endpoint {Endpoint}",
                endpoint);

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Type = "https://httpstatuses.com/503",
                Title = "Service Unavailable",
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = "Too many concurrent requests. Please retry later.",
                Instance = context.Request.Path
            });
            
            return;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
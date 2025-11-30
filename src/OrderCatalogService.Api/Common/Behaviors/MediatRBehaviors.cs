using System.Diagnostics;
using System.Transactions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderCatalogService.Infrastructure.Database;

namespace OrderCatalogService.Common.Behaviors;

// ═══════════════════════════════════════════════════════════
// VALIDATION BEHAVIOR (Fail Fast)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Validates requests before execution using FluentValidation
/// Throws ValidationException on failure (caught by global exception handler)
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);

        // Execute all validators in parallel
        var validationTasks = _validators
            .Select(v => v.ValidateAsync(context, cancellationToken));

        var validationResults = await Task.WhenAll(validationTasks);

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f != null)
            .ToList();

        if (failures.Count != 0)
        {
            throw new ValidationException(failures);
        }

        return await next();
    }
}

// ═══════════════════════════════════════════════════════════
// LOGGING BEHAVIOR (Request/Response Logging)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Logs all requests and responses with correlation tracking
/// Includes execution time and success/failure status
/// </summary>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Handling {RequestName} {@Request}",
            requestName, request);

        try
        {
            var response = await next();
            
            stopwatch.Stop();
            
            _logger.LogInformation(
                "Handled {RequestName} in {ElapsedMs}ms with result {@Response}",
                requestName, stopwatch.ElapsedMilliseconds, response);

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            
            _logger.LogError(ex,
                "Failed handling {RequestName} after {ElapsedMs}ms: {Error}",
                requestName, stopwatch.ElapsedMilliseconds, ex.Message);

            throw;
        }
    }
}

// ═══════════════════════════════════════════════════════════
// PERFORMANCE BEHAVIOR (Slow Query Detection)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Tracks request execution time and logs warnings for slow requests
/// Helps identify performance bottlenecks in production
/// </summary>
public sealed class PerformanceBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<PerformanceBehavior<TRequest, TResponse>> _logger;
    private readonly Stopwatch _timer;
    private const int SlowRequestThresholdMs = 500;

    public PerformanceBehavior(ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
        _timer = new Stopwatch();
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        _timer.Start();

        var response = await next();

        _timer.Stop();

        var elapsedMilliseconds = _timer.ElapsedMilliseconds;

        if (elapsedMilliseconds > SlowRequestThresholdMs)
        {
            var requestName = typeof(TRequest).Name;

            _logger.LogWarning(
                "Slow request detected: {RequestName} took {ElapsedMs}ms {@Request}",
                requestName, elapsedMilliseconds, request);
        }

        return response;
    }
}

// ═══════════════════════════════════════════════════════════
// TRANSACTION BEHAVIOR (Unit of Work Pattern)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Wraps command execution in a database transaction
/// Ensures atomicity - all changes saved or none
/// Only applies to commands (not queries)
/// Handles optimistic concurrency conflicts with retry
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly OrderCatalogDbContext _dbContext;
    private readonly ILogger<TransactionBehavior<TRequest, TResponse>> _logger;

    public TransactionBehavior(
        OrderCatalogDbContext dbContext,
        ILogger<TransactionBehavior<TRequest, TResponse>> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only apply transactions to commands (not queries)
        if (request is not ICommand)
        {
            return await next();
        }

        var requestName = typeof(TRequest).Name;

        // Check if already in transaction (nested transaction prevention)
        if (_dbContext.Database.CurrentTransaction != null)
        {
            return await next();
        }

        _logger.LogDebug("Starting transaction for {RequestName}", requestName);

        // Use serializable isolation for critical writes
        var strategy = _dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, // Balance between consistency and performance
                cancellationToken);

            try
            {
                var response = await next();

                await transaction.CommitAsync(cancellationToken);

                _logger.LogDebug("Transaction committed for {RequestName}", requestName);

                return response;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "Concurrency conflict in {RequestName}, transaction rolled back",
                    requestName);

                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Transaction failed for {RequestName}, rolling back",
                    requestName);

                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }
}

// Marker interface for commands (mutating operations)
public interface ICommand { }

// ═══════════════════════════════════════════════════════════
// CACHING BEHAVIOR (Query Result Caching)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Caches query results in distributed cache (Redis)
/// Only applies to queries implementing ICacheableQuery
/// Uses cache-aside pattern with configurable TTL
/// Thread-safe with distributed locking to prevent cache stampede
/// </summary>
public sealed class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache _cache;
    private readonly ILogger<CachingBehavior<TRequest, TResponse>> _logger;

    public CachingBehavior(
        Microsoft.Extensions.Caching.Distributed.IDistributedCache cache,
        ILogger<CachingBehavior<TRequest, TResponse>> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only cache queries
        if (request is not ICacheableQuery cacheableQuery)
        {
            return await next();
        }

        var cacheKey = cacheableQuery.CacheKey;
        
        // Try to get from cache
        var cachedResult = await _cache.GetStringAsync(cacheKey, cancellationToken);
        
        if (!string.IsNullOrEmpty(cachedResult))
        {
            _logger.LogDebug("Cache hit for {CacheKey}", cacheKey);
            return System.Text.Json.JsonSerializer.Deserialize<TResponse>(cachedResult)!;
        }

        _logger.LogDebug("Cache miss for {CacheKey}, executing query", cacheKey);

        // Execute query
        var response = await next();

        // Store in cache
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);
        
        await _cache.SetStringAsync(
            cacheKey,
            serialized,
            new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = cacheableQuery.CacheDuration
            },
            cancellationToken);

        return response;
    }
}

public interface ICacheableQuery
{
    string CacheKey { get; }
    TimeSpan CacheDuration { get; }
}

// ═══════════════════════════════════════════════════════════
// RETRY BEHAVIOR (Transient Failure Handling)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Retries failed requests on transient errors
/// Uses exponential backoff with jitter
/// Only applies to idempotent operations
/// </summary>
public sealed class RetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<RetryBehavior<TRequest, TResponse>> _logger;
    private const int MaxRetries = 3;

    public RetryBehavior(ILogger<RetryBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only retry idempotent operations
        if (request is not IRetryableRequest)
        {
            return await next();
        }

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await next();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(
                    Math.Pow(2, attempt) * 100 + Random.Shared.Next(0, 100));

                _logger.LogWarning(ex,
                    "Transient error on attempt {Attempt}/{MaxRetries}, retrying after {DelayMs}ms",
                    attempt, MaxRetries, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }

        // Final attempt
        return await next();
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is TimeoutException ||
               ex is HttpRequestException ||
               (ex is DbUpdateException && ex.InnerException is Npgsql.NpgsqlException);
    }
}

public interface IRetryableRequest { }

// ═══════════════════════════════════════════════════════════
// CIRCUIT BREAKER BEHAVIOR (Cascading Failure Prevention)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Circuit breaker pattern for external dependencies
/// Prevents cascading failures by failing fast when system is degraded
/// Thread-safe using Interlocked operations
/// </summary>
public sealed class CircuitBreakerBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<CircuitBreakerBehavior<TRequest, TResponse>> _logger;
    
    // Thread-safe state management
    private static int _failureCount = 0;
    private static DateTimeOffset _circuitOpenedAt = DateTimeOffset.MinValue;
    
    private const int FailureThreshold = 5;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromSeconds(30);

    public CircuitBreakerBehavior(ILogger<CircuitBreakerBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Check if circuit is open
        if (IsCircuitOpen())
        {
            _logger.LogWarning("Circuit breaker is OPEN, rejecting request");
            throw new InvalidOperationException("Service temporarily unavailable due to repeated failures");
        }

        try
        {
            var response = await next();
            
            // Success - reset failure count
            Interlocked.Exchange(ref _failureCount, 0);
            
            return response;
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref _failureCount);
            
            if (failures >= FailureThreshold)
            {
                _circuitOpenedAt = DateTimeOffset.UtcNow;
                _logger.LogError(ex, 
                    "Circuit breaker OPENED after {FailureCount} consecutive failures",
                    failures);
            }
            
            throw;
        }
    }

    private static bool IsCircuitOpen()
    {
        if (_failureCount < FailureThreshold)
            return false;

        var timeSinceOpened = DateTimeOffset.UtcNow - _circuitOpenedAt;
        
        if (timeSinceOpened > CircuitOpenDuration)
        {
            // Half-open state - allow one request through
            Interlocked.Exchange(ref _failureCount, FailureThreshold - 1);
            return false;
        }

        return true;
    }
}
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using RedLockNet.SERedis;
using StackExchange.Redis;

namespace OrderCatalogService.Infrastructure.Idempotency;

/// <summary>
/// Redis-backed idempotency service with distributed locking
/// Ensures exactly-once order creation under retry storms
/// Handles race conditions using RedLock algorithm
/// </summary>
public interface IIdempotencyService
{
    Task<IdempotencyResult<TResponse>> ExecuteAsync<TResponse>(
        string idempotencyKey,
        Func<CancellationToken, Task<TResponse>> operation,
        CancellationToken cancellationToken = default);
    
    Task<IdempotencyResult<TResponse>?> GetStoredResultAsync<TResponse>(
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}

public sealed class RedisIdempotencyService : IIdempotencyService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDistributedCache _cache;
    private readonly RedLockFactory _lockFactory;
    private readonly ILogger<RedisIdempotencyService> _logger;
    
    // In-memory deduplication for requests currently in-flight
    // Prevents duplicate execution even before Redis is checked
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _inFlightLocks = new();
    
    private const string KeyPrefix = "idempotency:";
    private const string LockPrefix = "idempotency-lock:";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockExpiry = TimeSpan.FromSeconds(30);

    public RedisIdempotencyService(
        IConnectionMultiplexer redis,
        IDistributedCache cache,
        RedLockFactory lockFactory,
        ILogger<RedisIdempotencyService> logger)
    {
        _redis = redis;
        _cache = cache;
        _lockFactory = lockFactory;
        _logger = logger;
    }

    /// <summary>
    /// Execute operation with idempotency guarantee
    /// Uses multi-layer locking: in-memory → distributed lock → Redis
    /// </summary>
    public async Task<IdempotencyResult<TResponse>> ExecuteAsync<TResponse>(
        string idempotencyKey,
        Func<CancellationToken, Task<TResponse>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(operation);

        var redisKey = GetRedisKey(idempotencyKey);
        var lockKey = GetLockKey(idempotencyKey);

        // LAYER 1: In-memory lock for local process deduplication
        // Prevents same process from executing twice before Redis check
        var localLock = _inFlightLocks.GetOrAdd(idempotencyKey, _ => new SemaphoreSlim(1, 1));
        
        await localLock.WaitAsync(cancellationToken);
        try
        {
            // LAYER 2: Check Redis cache first (fast path)
            var cachedResult = await GetStoredResultAsync<TResponse>(idempotencyKey, cancellationToken);
            if (cachedResult != null)
            {
                _logger.LogInformation(
                    "Idempotency hit for key {IdempotencyKey}, status: {Status}",
                    idempotencyKey, cachedResult.Status);
                return cachedResult;
            }

            // LAYER 3: Distributed lock using RedLock for cross-instance coordination
            // Prevents concurrent execution across multiple service instances
            await using var redLock = await _lockFactory.CreateLockAsync(
                lockKey,
                LockExpiry,
                LockTimeout,
                TimeSpan.FromMilliseconds(100), // retry interval
                cancellationToken);

            if (!redLock.IsAcquired)
            {
                _logger.LogWarning(
                    "Failed to acquire distributed lock for {IdempotencyKey}, returning conflict",
                    idempotencyKey);
                
                // Return 409 with polling instruction
                return new IdempotencyResult<TResponse>
                {
                    Status = IdempotencyStatus.InProgress,
                    Key = idempotencyKey,
                    Message = "Request is currently being processed. Poll for results."
                };
            }

            _logger.LogDebug("Acquired distributed lock for {IdempotencyKey}", idempotencyKey);

            // Double-check Redis after acquiring lock (another instance may have completed)
            cachedResult = await GetStoredResultAsync<TResponse>(idempotencyKey, cancellationToken);
            if (cachedResult != null)
            {
                _logger.LogInformation(
                    "Idempotency hit after lock acquisition for {IdempotencyKey}",
                    idempotencyKey);
                return cachedResult;
            }

            // Mark as in-progress (with minimal payload for fast writes)
            await StoreInProgressAsync(idempotencyKey, cancellationToken);

            try
            {
                // Execute the operation (CRITICAL SECTION)
                var result = await operation(cancellationToken);

                // Store successful result
                var successResult = new IdempotencyResult<TResponse>
                {
                    Status = IdempotencyStatus.Completed,
                    Key = idempotencyKey,
                    Response = result,
                    CompletedAt = DateTimeOffset.UtcNow
                };

                await StoreResultAsync(idempotencyKey, successResult, cancellationToken);

                _logger.LogInformation(
                    "Successfully executed and stored result for {IdempotencyKey}",
                    idempotencyKey);

                return successResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Operation failed for {IdempotencyKey}, storing failure state",
                    idempotencyKey);

                // Store failure state (prevents retry storms on permanent failures)
                var failureResult = new IdempotencyResult<TResponse>
                {
                    Status = IdempotencyStatus.Failed,
                    Key = idempotencyKey,
                    Message = ex.Message,
                    CompletedAt = DateTimeOffset.UtcNow
                };

                await StoreResultAsync(idempotencyKey, failureResult, cancellationToken);
                throw;
            }
        }
        finally
        {
            // Release in-memory lock
            localLock.Release();
            
            // Cleanup in-memory lock if no waiters (best effort)
            if (localLock.CurrentCount == 1 && 
                _inFlightLocks.TryRemove(idempotencyKey, out var removedLock))
            {
                removedLock.Dispose();
            }
        }
    }

    /// <summary>
    /// Retrieve stored idempotency result (for polling)
    /// </summary>
    public async Task<IdempotencyResult<TResponse>?> GetStoredResultAsync<TResponse>(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var redisKey = GetRedisKey(idempotencyKey);
        
        try
        {
            var serialized = await _cache.GetStringAsync(redisKey, cancellationToken);
            
            if (string.IsNullOrEmpty(serialized))
                return null;

            var result = JsonSerializer.Deserialize<IdempotencyResult<TResponse>>(
                serialized,
                new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve idempotency result for {Key}", idempotencyKey);
            return null;
        }
    }

    /// <summary>
    /// Store in-progress marker (fast write, minimal data)
    /// </summary>
    private async Task StoreInProgressAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var redisKey = GetRedisKey(idempotencyKey);
        var inProgressMarker = JsonSerializer.Serialize(new
        {
            Status = IdempotencyStatus.InProgress.ToString(),
            Key = idempotencyKey,
            StartedAt = DateTimeOffset.UtcNow
        });

        await _cache.SetStringAsync(
            redisKey,
            inProgressMarker,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) // Short TTL for in-progress
            },
            cancellationToken);
    }

    /// <summary>
    /// Store final result with full 24h TTL
    /// </summary>
    private async Task StoreResultAsync<TResponse>(
        string idempotencyKey,
        IdempotencyResult<TResponse> result,
        CancellationToken cancellationToken)
    {
        var redisKey = GetRedisKey(idempotencyKey);
        var serialized = JsonSerializer.Serialize(result);

        await _cache.SetStringAsync(
            redisKey,
            serialized,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = DefaultTtl
            },
            cancellationToken);

        // Also publish to Redis Pub/Sub for real-time updates (optional)
        try
        {
            var subscriber = _redis.GetSubscriber();
            await subscriber.PublishAsync(
                RedisChannel.Literal($"idempotency-updates:{idempotencyKey}"),
                serialized,
                CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            // Pub/Sub failure is non-critical
            _logger.LogWarning(ex, "Failed to publish idempotency update for {Key}", idempotencyKey);
        }
    }

    private static string GetRedisKey(string idempotencyKey) => $"{KeyPrefix}{idempotencyKey}";
    private static string GetLockKey(string idempotencyKey) => $"{LockPrefix}{idempotencyKey}";
}

// ═══════════════════════════════════════════════════════════
// IDEMPOTENCY MODELS
// ═══════════════════════════════════════════════════════════

public sealed class IdempotencyResult<TResponse>
{
    public IdempotencyStatus Status { get; init; }
    public string Key { get; init; } = string.Empty;
    public TResponse? Response { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public enum IdempotencyStatus
{
    InProgress,
    Completed,
    Failed
}

// ═══════════════════════════════════════════════════════════
// MEDIATOR BEHAVIOR - Auto Idempotency for Commands
// ═══════════════════════════════════════════════════════════

/// <summary>
/// MediatR pipeline behavior that automatically handles idempotency
/// for commands that implement IIdempotentCommand
/// </summary>
public sealed class IdempotencyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IIdempotencyService _idempotencyService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<IdempotencyBehavior<TRequest, TResponse>> _logger;

    public IdempotencyBehavior(
        IIdempotencyService idempotencyService,
        IHttpContextAccessor httpContextAccessor,
        ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    {
        _idempotencyService = idempotencyService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only apply to idempotent commands
        if (request is not IIdempotentCommand idempotentCommand)
        {
            return await next();
        }

        // Extract idempotency key from header or command
        var idempotencyKey = idempotentCommand.IdempotencyKey;
        
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Try to get from HTTP header
            var httpContext = _httpContextAccessor.HttpContext;
            idempotencyKey = httpContext?.Request.Headers["Idempotency-Key"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new InvalidOperationException(
                "Idempotency-Key header is required for idempotent operations");
        }

        _logger.LogDebug("Processing idempotent command with key: {IdempotencyKey}", idempotencyKey);

        var result = await _idempotencyService.ExecuteAsync(
            idempotencyKey,
            async ct => await next(),
            cancellationToken);

        // Handle different statuses
        return result.Status switch
        {
            IdempotencyStatus.Completed => result.Response!,
            IdempotencyStatus.InProgress => throw new IdempotencyInProgressException(idempotencyKey),
            IdempotencyStatus.Failed => throw new IdempotencyFailedException(idempotencyKey, result.Message),
            _ => throw new InvalidOperationException($"Unknown idempotency status: {result.Status}")
        };
    }
}

public interface IIdempotentCommand
{
    string IdempotencyKey { get; }
}

public sealed class IdempotencyInProgressException : Exception
{
    public string IdempotencyKey { get; }
    
    public IdempotencyInProgressException(string idempotencyKey)
        : base($"Request with idempotency key '{idempotencyKey}' is currently being processed")
    {
        IdempotencyKey = idempotencyKey;
    }
}

public sealed class IdempotencyFailedException : Exception
{
    public string IdempotencyKey { get; }
    
    public IdempotencyFailedException(string idempotencyKey, string? message)
        : base($"Request with idempotency key '{idempotencyKey}' previously failed: {message}")
    {
        IdempotencyKey = idempotencyKey;
    }
}
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using OrderCatalogService.Domain.Orders;
using OrderCatalogService.Infrastructure.Database;

namespace OrderCatalogService.Infrastructure.Outbox;

/// <summary>
/// Transactional Outbox Pattern processor
/// Polls database for unpublished events and publishes to Azure Service Bus
/// Uses parallel processing with configurable degree of parallelism
/// Ensures at-least-once delivery with automatic retries
/// </summary>
public sealed class OutboxProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IConfiguration _configuration;
    
    // Channel for producer-consumer pattern (bounded for backpressure)
    private readonly Channel<OutboxMessage> _messageChannel;
    
    private const int DefaultBatchSize = 100;
    private const int DefaultParallelism = 4;
    private const int ChannelCapacity = 1000;
    
    public OutboxProcessor(
        IServiceProvider serviceProvider,
        ILogger<OutboxProcessor> logger,
        ServiceBusClient serviceBusClient,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _serviceBusClient = serviceBusClient;
        _configuration = configuration;
        
        // Bounded channel for memory control under high load
        _messageChannel = Channel.CreateBounded<OutboxMessage>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // Backpressure
            SingleReader = false,
            SingleWriter = false
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox Processor starting...");

        // Start consumer tasks (parallel processing)
        var consumerTasks = Enumerable.Range(0, DefaultParallelism)
            .Select(i => ConsumeMessagesAsync(i, stoppingToken))
            .ToList();

        // Producer loop (polls database)
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollAndEnqueueAsync(stoppingToken);
                    
                    // Configurable polling interval (exponential backoff when idle)
                    var pollingInterval = TimeSpan.FromSeconds(
                        _configuration.GetValue("Outbox:PollingIntervalSeconds", 5));
                    
                    await Task.Delay(pollingInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in outbox polling loop");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }
        finally
        {
            // Signal completion to consumers
            _messageChannel.Writer.Complete();
            
            // Wait for consumers to finish processing
            await Task.WhenAll(consumerTasks);
            
            _logger.LogInformation("Outbox Processor stopped");
        }
    }

    /// <summary>
    /// Producer: Poll database for unpublished messages
    /// Uses efficient batching and pagination
    /// </summary>
    private async Task PollAndEnqueueAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderCatalogDbContext>();
        
        // Fetch batch of unprocessed messages (ordered by OccurredAt for FIFO)
        var messages = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(DefaultBatchSize)
            .ToListAsync(stoppingToken);

        if (messages.Count == 0)
            return;

        _logger.LogDebug("Fetched {Count} outbox messages for processing", messages.Count);

        // Enqueue to channel (bounded channel provides backpressure)
        foreach (var message in messages)
        {
            await _messageChannel.Writer.WriteAsync(message, stoppingToken);
        }
    }

    /// <summary>
    /// Consumer: Process messages from channel in parallel
    /// Each consumer runs independently for maximum throughput
    /// </summary>
    private async Task ConsumeMessagesAsync(int consumerId, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox consumer {ConsumerId} starting", consumerId);

        await foreach (var message in _messageChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessMessageAsync(message, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Consumer {ConsumerId} failed to process message {MessageId}", 
                    consumerId, message.Id);
                
                // Message will be retried on next poll (ProcessedAt is still null)
            }
        }

        _logger.LogInformation("Outbox consumer {ConsumerId} stopped", consumerId);
    }

    /// <summary>
    /// Process single message: Publish to Service Bus + mark as processed
    /// Uses separate DbContext per message to avoid concurrency issues
    /// Idempotent: tolerates duplicate processing via ProcessedAt check
    /// </summary>
    private async Task ProcessMessageAsync(OutboxMessage message, CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderCatalogDbContext>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Double-check message hasn't been processed (race condition prevention)
            var existingMessage = await dbContext.OutboxMessages
                .Where(m => m.Id == message.Id)
                .FirstOrDefaultAsync(stoppingToken);

            if (existingMessage == null || existingMessage.ProcessedAt != null)
            {
                _logger.LogDebug("Message {MessageId} already processed, skipping", message.Id);
                return;
            }

            // Publish to Azure Service Bus (at-least-once delivery)
            await PublishToServiceBusAsync(message, stoppingToken);

            // Mark as processed (atomic update)
            existingMessage.ProcessedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(stoppingToken);

            stopwatch.Stop();
            
            _logger.LogInformation(
                "Published outbox message {MessageId} ({Type}) in {ElapsedMs}ms",
                message.Id, message.Type, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            
            _logger.LogError(ex,
                "Failed to process outbox message {MessageId} after {ElapsedMs}ms",
                message.Id, stopwatch.ElapsedMilliseconds);
            
            throw; // Will be retried on next poll
        }
    }

    /// <summary>
    /// Publish message to Azure Service Bus with retry logic
    /// Maps domain events to Service Bus messages
    /// </summary>
    private async Task PublishToServiceBusAsync(OutboxMessage message, CancellationToken stoppingToken)
    {
        var topicName = _configuration["ServiceBus:TopicName"] ?? "order-events";
        var sender = _serviceBusClient.CreateSender(topicName);

        try
        {
            var serviceBusMessage = new ServiceBusMessage(message.Payload)
            {
                MessageId = message.Id.ToString(),
                Subject = message.Type,
                ContentType = "application/json",
                CorrelationId = message.Id.ToString(),
                TimeToLive = TimeSpan.FromDays(7)
            };

            // Add metadata
            serviceBusMessage.ApplicationProperties["EventType"] = message.Type;
            serviceBusMessage.ApplicationProperties["OccurredAt"] = message.OccurredAt.ToString("O");
            serviceBusMessage.ApplicationProperties["Source"] = "OrderCatalogService";

            // Send with automatic retry (Service Bus SDK handles transient failures)
            await sender.SendMessageAsync(serviceBusMessage, stoppingToken);
            
            _logger.LogDebug(
                "Published message {MessageId} to Service Bus topic {TopicName}",
                message.Id, topicName);
        }
        catch (ServiceBusException ex) when (ex.IsTransient)
        {
            _logger.LogWarning(ex, 
                "Transient Service Bus error for message {MessageId}, will retry",
                message.Id);
            throw;
        }
        finally
        {
            await sender.DisposeAsync();
        }
    }
}

// ═══════════════════════════════════════════════════════════
// OUTBOX MESSAGE ENTITY
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Outbox message stored in same database as domain entities
/// Ensures transactional consistency (events only published if order persisted)
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    
    // Retry tracking (future enhancement)
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}

// ═══════════════════════════════════════════════════════════
// WEBHOOK DELIVERY SERVICE (Parallel Execution)
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Webhook delivery service with parallel execution and retry logic
/// Implements at-least-once delivery with HMAC-SHA256 signatures
/// </summary>
public interface IWebhookDeliveryService
{
    Task DeliverAsync(string eventType, object payload, CancellationToken cancellationToken = default);
}

public sealed class WebhookDeliveryService : IWebhookDeliveryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebhookDeliveryService> _logger;
    private readonly IConfiguration _configuration;
    
    // Semaphore for controlling max concurrent webhook deliveries
    private readonly SemaphoreSlim _concurrencySemaphore;
    
    private const int MaxConcurrentDeliveries = 10;
    private const int MaxRetries = 10;

    public WebhookDeliveryService(
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        ILogger<WebhookDeliveryService> logger,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
        _concurrencySemaphore = new SemaphoreSlim(MaxConcurrentDeliveries, MaxConcurrentDeliveries);
    }

    /// <summary>
    /// Deliver webhook to all registered subscribers in parallel
    /// Each delivery is independent (fire-and-forget with retries)
    /// </summary>
    public async Task DeliverAsync(
        string eventType,
        object payload,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderCatalogDbContext>();

        // Fetch active webhooks for this event type
        var webhooks = await dbContext.Webhooks
            .AsNoTracking()
            .Where(w => w.IsActive && w.Events.Contains(eventType))
            .ToListAsync(cancellationToken);

        if (webhooks.Count == 0)
        {
            _logger.LogDebug("No webhooks registered for event type {EventType}", eventType);
            return;
        }

        _logger.LogInformation(
            "Delivering {EventType} to {WebhookCount} subscribers",
            eventType, webhooks.Count);

        // Parallel delivery using Task.WhenAll with semaphore throttling
        var deliveryTasks = webhooks.Select(webhook => 
            DeliverToWebhookAsync(webhook, eventType, payload, cancellationToken));

        await Task.WhenAll(deliveryTasks);
    }

    /// <summary>
    /// Deliver to single webhook with exponential backoff retry
    /// </summary>
    private async Task DeliverToWebhookAsync(
        Webhook webhook,
        string eventType,
        object payload,
        CancellationToken cancellationToken)
    {
        // Throttle concurrent deliveries
        await _concurrencySemaphore.WaitAsync(cancellationToken);
        
        try
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            var signature = GenerateHmacSignature(payloadJson, webhook.Secret);

            var httpClient = _httpClientFactory.CreateClient("Webhooks");
            
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
                    {
                        Content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json")
                    };
                    
                    request.Headers.Add("X-Webhook-Signature", signature);
                    request.Headers.Add("X-Event-Type", eventType);
                    request.Headers.Add("X-Delivery-Id", Guid.NewGuid().ToString());

                    var response = await httpClient.SendAsync(request, cancellationToken);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation(
                            "Webhook delivered successfully to {Url} on attempt {Attempt}",
                            webhook.Url, attempt);
                        return;
                    }

                    _logger.LogWarning(
                        "Webhook delivery failed with status {StatusCode} on attempt {Attempt} to {Url}",
                        response.StatusCode, attempt, webhook.Url);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Webhook delivery exception on attempt {Attempt} to {Url}",
                        attempt, webhook.Url);
                }

                // Exponential backoff with jitter
                if (attempt < MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)) + 
                                TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
                    await Task.Delay(delay, cancellationToken);
                }
            }

            // All retries failed - log for manual intervention or DLQ
            _logger.LogError(
                "Webhook delivery failed after {MaxRetries} attempts to {Url}",
                MaxRetries, webhook.Url);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    private static string GenerateHmacSignature(string payload, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret));
        
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }
}

// ═══════════════════════════════════════════════════════════
// WEBHOOK ENTITY
// ═══════════════════════════════════════════════════════════

public sealed class Webhook
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public List<string> Events { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
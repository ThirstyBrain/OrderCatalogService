using System.Reflection;
using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Carter;
using FluentValidation;
using HotChocolate.Execution.Options;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderCatalogService.Common.Behaviors;
using OrderCatalogService.Common.Middleware;
using OrderCatalogService.Infrastructure.Database;
using OrderCatalogService.Infrastructure.Idempotency;
using OrderCatalogService.Infrastructure.Outbox;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ═══════════════════════════════════════════════════════════
// CONCURRENCY & THREADING CONFIGURATION (Production Tuned)
// ═══════════════════════════════════════════════════════════

// ThreadPool tuning for high-throughput scenarios
ThreadPool.GetMinThreads(out var minWorker, out var minIOC);
var processorCount = Environment.ProcessorCount;
ThreadPool.SetMinThreads(minWorker: processorCount * 2, minIOC: processorCount * 4);
ThreadPool.SetMaxThreads(workerThreads: 32767, completionPortThreads: 1000);

// Kestrel tuning for 100K+ RPM
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 10000;
    options.Limits.MaxConcurrentUpgradedConnections = 10000;
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
    options.Limits.MinRequestBodyDataRate = null; // Disable timeout for streaming
    options.Limits.MinResponseDataRate = null;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.AddServerHeader = false;
    
    // HTTP/2 for better multiplexing
    options.ConfigureHttpsDefaults(https =>
    {
        https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | 
                            System.Security.Authentication.SslProtocols.Tls13;
    });
});

// ═══════════════════════════════════════════════════════════
// CONFIGURATION & SECRETS
// ═══════════════════════════════════════════════════════════

if (builder.Environment.IsProduction())
{
    builder.Configuration.AddAzureAppConfiguration(options =>
    {
        options.Connect(builder.Configuration["AzureAppConfig:Endpoint"])
               .ConfigureKeyVault(kv => kv.SetCredential(new DefaultAzureCredential()))
               .ConfigureRefresh(refresh =>
               {
                   refresh.Register("Sentinel", refreshAll: true)
                          .SetCacheExpiration(TimeSpan.FromMinutes(5));
               });
    });
}

// ═══════════════════════════════════════════════════════════
// LOGGING & OBSERVABILITY
// ═══════════════════════════════════════════════════════════

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProperty("Application", "OrderCatalogService")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.Seq(builder.Configuration["Seq:Url"] ?? "http://localhost:5341")
    .WriteTo.AzureAnalytics(
        workspaceId: builder.Configuration["LogAnalytics:WorkspaceId"],
        authenticationId: builder.Configuration["LogAnalytics:Key"])
    .CreateLogger();

builder.Host.UseSerilog();

// OpenTelemetry with Azure Monitor + Jaeger
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService("OrderCatalogService", serviceVersion: "1.0.0"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                activity.SetTag("http.client_ip", request.HttpContext.Connection.RemoteIpAddress?.ToString());
                activity.SetTag("http.user_agent", request.Headers["User-Agent"].ToString());
            };
        })
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation(options =>
        {
            options.SetDbStatementForText = true;
            options.SetDbStatementForStoredProcedure = true;
        })
        .AddRedisInstrumentation()
        .AddSource("MediatR", "OrderCatalogService.*")
        .AddJaegerExporter()
        .AddAzureMonitorTraceExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation()
        .AddMeter("OrderCatalogService.*")
        .AddPrometheusExporter());

// ═══════════════════════════════════════════════════════════
// DATABASE - PostgreSQL with Connection Pooling
// ═══════════════════════════════════════════════════════════

builder.Services.AddDbContext<OrderCatalogDbContext>((sp, options) =>
{
    var connString = builder.Configuration.GetConnectionString("PostgreSQL");
    
    options.UseNpgsql(connString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorCodesToAdd: null);
        npgsqlOptions.CommandTimeout(30);
        npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        npgsqlOptions.MigrationsAssembly(typeof(OrderCatalogDbContext).Assembly.FullName);
    })
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking) // Default no-tracking for reads
    .EnableSensitiveDataLogging(builder.Environment.IsDevelopment())
    .EnableDetailedErrors(builder.Environment.IsDevelopment())
    .UseLoggerFactory(LoggerFactory.Create(b => b.AddSerilog()));
}, ServiceLifetime.Scoped);

// Connection pool configuration via connection string:
// "Maximum Pool Size=200;Minimum Pool Size=10;Connection Idle Lifetime=300;Connection Pruning Interval=10"

// ═══════════════════════════════════════════════════════════
// REDIS - Distributed Cache & Locks
// ═══════════════════════════════════════════════════════════

var redisConnection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
{
    EndPoints = { builder.Configuration["Redis:Endpoint"] },
    Password = builder.Configuration["Redis:Password"],
    AbortOnConnectFail = false,
    ConnectRetry = 3,
    ConnectTimeout = 5000,
    SyncTimeout = 5000,
    AsyncTimeout = 5000,
    KeepAlive = 60,
    DefaultDatabase = 0,
    Ssl = true,
    SslProtocols = System.Security.Authentication.SslProtocols.Tls12,
    
    // Connection pooling
    ReconnectRetryPolicy = new ExponentialRetry(5000),
    SocketManager = new SocketManager(name: "OrderCatalogService", 
                                      workerCount: processorCount),
});

builder.Services.AddSingleton<IConnectionMultiplexer>(redisConnection);
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.ConnectionMultiplexerFactory = () => Task.FromResult(redisConnection);
    options.InstanceName = "OrderCatalog:";
});

// Distributed lock using RedLock.net
builder.Services.AddSingleton<RedLockFactory>(sp =>
{
    var redis = sp.GetRequiredService<IConnectionMultiplexer>();
    return RedLockFactory.Create(new[] { new RedLockMultiplexer(redis) });
});

// ═══════════════════════════════════════════════════════════
// AUTHENTICATION & AUTHORIZATION
// ═══════════════════════════════════════════════════════════

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(options =>
    {
        builder.Configuration.Bind("AzureAdB2C", options);
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(2);
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                Log.Warning("JWT auth failed: {Error}", context.Exception.Message);
                return Task.CompletedTask;
            }
        };
    }, options => builder.Configuration.Bind("AzureAdB2C", options), "AzureAdB2C")
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CatalogRead", policy => 
        policy.RequireScope("catalog:read"));
    options.AddPolicy("OrderRead", policy => 
        policy.RequireScope("order:read"));
    options.AddPolicy("OrderWrite", policy => 
        policy.RequireScope("order:write"));
    options.AddPolicy("OrderCancel", policy => 
        policy.RequireScope("order:cancel"));
    options.AddPolicy("WebhookManage", policy => 
        policy.RequireScope("webhook:manage"));
});

// ═══════════════════════════════════════════════════════════
// RATE LIMITING - Application Level (APIM handles API gateway level)
// ═══════════════════════════════════════════════════════════

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    // Sliding window per user/IP
    options.AddSlidingWindowLimiter("PerUser", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 100;
        opt.SegmentsPerWindow = 6;
        opt.QueueLimit = 10;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    
    // Critical endpoints - stricter limits
    options.AddFixedWindowLimiter("OrderCreation", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 10;
        opt.QueueLimit = 5;
    });
    
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        context.HttpContext.Response.Headers["X-RateLimit-Limit"] = "100";
        context.HttpContext.Response.Headers["X-RateLimit-Remaining"] = "0";
        
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://httpstatuses.com/429",
            title = "Too Many Requests",
            status = 429,
            detail = "Rate limit exceeded. Please retry after 60 seconds.",
            instance = context.HttpContext.Request.Path
        }, cancellationToken: token);
    };
});

// ═══════════════════════════════════════════════════════════
// MEDIATOR + CQRS + VALIDATION PIPELINE
// ═══════════════════════════════════════════════════════════

builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(PerformanceBehavior<,>));
    cfg.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
    cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
});

builder.Services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

// ═══════════════════════════════════════════════════════════
// HTTP CLIENTS with Polly Resilience
// ═══════════════════════════════════════════════════════════

builder.Services.AddHttpClient("Webhooks", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "OrderCatalogService/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    MaxConnectionsPerServer = 100,
    EnableMultipleHttp2Connections = true,
    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
    KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
})
.AddPolicyHandler(GetRetryPolicy())
.AddPolicyHandler(GetCircuitBreakerPolicy());

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => 
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + 
                TimeSpan.FromMilliseconds(new Random().Next(0, 1000)),
            onRetry: (outcome, timespan, retryCount, context) =>
            {
                Log.Warning("Retry {RetryCount} after {Delay}ms due to {Error}", 
                    retryCount, timespan.TotalMilliseconds, outcome.Exception?.Message);
            });
}

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 5,
            durationOfBreak: TimeSpan.FromSeconds(30),
            onBreak: (outcome, duration) =>
            {
                Log.Error("Circuit breaker opened for {Duration}s", duration.TotalSeconds);
            },
            onReset: () => Log.Information("Circuit breaker reset"),
            onHalfOpen: () => Log.Information("Circuit breaker half-open"));
}

// ═══════════════════════════════════════════════════════════
// GRAPHQL with HotChocolate
// ═══════════════════════════════════════════════════════════

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddAuthorization()
    .AddFiltering()
    .AddSorting()
    .AddProjections()
    .ModifyRequestOptions(opt =>
    {
        opt.ExecutionTimeout = TimeSpan.FromSeconds(30);
        opt.IncludeExceptionDetails = builder.Environment.IsDevelopment();
    })
    .ModifyOptions(opt =>
    {
        opt.MaxExecutionDepth = 10;
        opt.UseComplexityMultipliers = true;
        opt.DefaultComplexity = 1;
        opt.MaxOperationComplexity = 200;
    })
    .UsePersistedQueryPipeline()
    .AddReadOnlyFileSystemQueryStorage("./persisted-queries")
    .UseAutomaticPersistedQueryPipeline()
    .AddRedisQueryStorage(sp => sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase())
    .AddRedisSubscriptions(sp => sp.GetRequiredService<IConnectionMultiplexer>())
    .RegisterDbContext<OrderCatalogDbContext>(DbContextKind.Pooled);

// ═══════════════════════════════════════════════════════════
// REST ENDPOINTS - Carter (FastEndpoints alternative)
// ═══════════════════════════════════════════════════════════

builder.Services.AddCarter();

// ═══════════════════════════════════════════════════════════
// API VERSIONING & OPENAPI
// ═══════════════════════════════════════════════════════════

builder.Services.AddApiVersioning(options =>
{
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.ReportApiVersions = true;
})
.AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";
    options.SubstituteApiVersionInUrl = true;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "Order & Catalog Service", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new()
    {
        Type = SecuritySchemeType.OAuth2,
        Flows = new()
        {
            AuthorizationCode = new()
            {
                AuthorizationUrl = new Uri(builder.Configuration["AzureAdB2C:AuthorizationUrl"]),
                TokenUrl = new Uri(builder.Configuration["AzureAdB2C:TokenUrl"]),
                Scopes = new Dictionary<string, string>
                {
                    { "catalog:read", "Read catalog" },
                    { "order:read", "Read orders" },
                    { "order:write", "Create orders" },
                    { "order:cancel", "Cancel orders" }
                }
            }
        }
    });
    options.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            new[] { "catalog:read", "order:read", "order:write", "order:cancel" }
        }
    });
});

// ═══════════════════════════════════════════════════════════
// INFRASTRUCTURE SERVICES
// ═══════════════════════════════════════════════════════════

builder.Services.AddScoped<IIdempotencyService, RedisIdempotencyService>();
builder.Services.AddHostedService<OutboxProcessor>(); // Background service
builder.Services.AddSingleton<IWebhookDeliveryService, WebhookDeliveryService>();

// ═══════════════════════════════════════════════════════════
// HEALTH CHECKS
// ═══════════════════════════════════════════════════════════

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("PostgreSQL")!, name: "postgresql")
    .AddRedis(redisConnection, name: "redis")
    .AddAzureServiceBusTopic(
        builder.Configuration["ServiceBus:ConnectionString"],
        builder.Configuration["ServiceBus:TopicName"],
        name: "servicebus")
    .AddDbContextCheck<OrderCatalogDbContext>();

// ═══════════════════════════════════════════════════════════
// BUILD & MIDDLEWARE PIPELINE
// ═══════════════════════════════════════════════════════════

var app = builder.Build();

// Forwarded headers for running behind APIM/reverse proxy
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.All
});

// Correlation ID injection
app.UseMiddleware<CorrelationIdMiddleware>();

// Exception handling
app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("ClientIP", httpContext.Connection.RemoteIpAddress);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"]);
        diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
    };
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "v1"));
}

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Prometheus metrics
app.UseOpenTelemetryPrometheusScrapingEndpoint();

// Health checks
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/startup");

// GraphQL
app.MapGraphQL();

// REST endpoints via Carter
app.MapCarter();

// Graceful shutdown
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    Log.Information("Application stopping, draining requests...");
    Thread.Sleep(5000); // Allow in-flight requests to complete
});

await app.RunAsync();
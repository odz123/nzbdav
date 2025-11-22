using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace NzbWebDAV;

class Program
{
    /// <summary>
    /// Validate dependency injection configuration to prevent lifetime mismatches.
    /// Ensures singleton services don't inject scoped DbContext instances.
    /// IMPORTANT: Singletons must create DbContext per-operation, not store as fields.
    /// </summary>
    static void ValidateDependencyInjection(IServiceProvider services)
    {
        // List of all singleton services that must NOT inject DavDatabaseContext
        var singletonServices = new[]
        {
            typeof(UsenetStreamingClient),
            typeof(QueueManager),
            typeof(ArrMonitoringService),
            typeof(HealthCheckService),
            typeof(ServerHealthTracker),
            typeof(ConfigManager),
            typeof(WebsocketManager)
        };

        foreach (var serviceType in singletonServices)
        {
            try
            {
                var service = services.GetRequiredService(serviceType);

                // Check for DbContext fields via reflection
                var fields = serviceType.GetFields(System.Reflection.BindingFlags.NonPublic |
                                                   System.Reflection.BindingFlags.Instance);
                var dbContextField = fields.FirstOrDefault(f =>
                    f.FieldType == typeof(DavDatabaseContext) ||
                    f.FieldType == typeof(DavDatabaseClient));

                if (dbContextField != null)
                {
                    Log.Fatal(
                        "DEPENDENCY INJECTION ERROR: Singleton service {ServiceType} has injected DbContext field {FieldName}. " +
                        "Singletons must create DbContext instances per operation using 'await using var dbContext = new DavDatabaseContext()', " +
                        "not store them as fields.",
                        serviceType.Name, dbContextField.Name);
                    throw new InvalidOperationException(
                        $"Singleton {serviceType.Name} incorrectly injects DbContext. " +
                        $"This violates EF Core lifetime rules and will cause disposed context errors.");
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Singleton"))
            {
                // Re-throw our validation errors
                throw;
            }
            catch (Exception ex)
            {
                // Service might not be registered yet - log warning but continue
                Log.Warning("Could not validate {ServiceType}: {Error}", serviceType.Name, ex.Message);
            }
        }

        Log.Information("Dependency injection validation passed - all singleton services follow proper DbContext lifetime pattern");
    }

    /// <summary>
    /// Configure thread pool with CPU-based defaults and environment variable overrides.
    /// Uses conservative settings that scale with available CPUs to prevent resource exhaustion.
    /// </summary>
    static void ConfigureThreadPool()
    {
        var cpuCount = Environment.ProcessorCount;

        // Allow override via environment variables for tuning in production
        var minWorkerThreads = EnvironmentUtil.GetIntVariable("MIN_WORKER_THREADS") ?? (cpuCount * 2);
        var minIoThreads = EnvironmentUtil.GetIntVariable("MIN_IO_THREADS") ?? (cpuCount * 4);
        var maxIoThreads = EnvironmentUtil.GetIntVariable("MAX_IO_THREADS");

        // Clamp to reasonable values to prevent misconfiguration
        // Min threads: between cpuCount and cpuCount*4 for workers
        // Min threads: between cpuCount*2 and cpuCount*8 for I/O
        minWorkerThreads = Math.Clamp(minWorkerThreads, cpuCount, cpuCount * 4);
        minIoThreads = Math.Clamp(minIoThreads, cpuCount * 2, cpuCount * 8);

        ThreadPool.SetMinThreads(minWorkerThreads, minIoThreads);

        // Only override max IO threads if explicitly configured
        // Let the runtime manage max worker threads for best performance
        if (maxIoThreads.HasValue)
        {
            ThreadPool.GetMaxThreads(out var maxWorker, out var _);
            var clampedMaxIo = Math.Clamp(maxIoThreads.Value, minIoThreads, 2000);
            ThreadPool.SetMaxThreads(maxWorker, clampedMaxIo);
        }

        // Log configuration for diagnostics and troubleshooting
        ThreadPool.GetMinThreads(out var actualMinWorker, out var actualMinIo);
        ThreadPool.GetMaxThreads(out var actualMaxWorker, out var actualMaxIo);
        Log.Information(
            "Thread pool configured: CPU={CpuCount}, MinThreads={MinWorker}/{MinIo}, MaxThreads={MaxWorker}/{MaxIo}",
            cpuCount, actualMinWorker, actualMinIo, actualMaxWorker, actualMaxIo);
    }

    static async Task Main(string[] args)
    {
        // Initialize logger first so ConfigureThreadPool can log
        var defaultLevel = LogEventLevel.Information;
        var envLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var level = Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed) ? parsed : defaultLevel;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("NWebDAV", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
            .WriteTo.Console(theme: AnsiConsoleTheme.Code)
            .CreateLogger();

        // Configure thread pool with CPU-based settings
        ConfigureThreadPool();

        // initialize database
        await using var databaseContext = new DavDatabaseContext();

        // run database migration, if necessary.
        if (args.Contains("--db-migration"))
        {
            var argIndex = args.ToList().IndexOf("--db-migration");
            var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
            await databaseContext.Database.MigrateAsync(targetMigration, SigtermUtil.GetCancellationToken());
            return;
        }

        // initialize the config-manager
        var configManager = new ConfigManager();
        await configManager.LoadConfig();

        // initialize websocket-manager
        var websocketManager = new WebsocketManager();

        // initialize webapp
        var builder = WebApplication.CreateBuilder(args);
        var maxRequestBodySize = EnvironmentUtil.GetLongVariable("MAX_REQUEST_BODY_SIZE") ?? 100 * 1024 * 1024;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddHealthChecks();
        builder.Services
            .AddWebdavBasicAuthentication(configManager)
            .AddSingleton(configManager)
            .AddSingleton(websocketManager)
            .AddSingleton<ServerHealthTracker>()
            .AddSingleton<UsenetStreamingClient>()
            .AddSingleton<QueueManager>()
            .AddSingleton<ArrMonitoringService>()
            .AddSingleton<HealthCheckService>()
            .AddScoped<DavDatabaseContext>()
            .AddScoped<DavDatabaseClient>()
            .AddScoped<DatabaseStore>()
            .AddScoped<IStore, DatabaseStore>()
            .AddScoped<GetAndHeadHandlerPatch>()
            .AddScoped<SabApiController>()
            .AddNWebDav(opts =>
            {
                opts.Handlers["GET"] = typeof(GetAndHeadHandlerPatch);
                opts.Handlers["HEAD"] = typeof(GetAndHeadHandlerPatch);
                opts.Filter = opts.GetFilter();
                opts.RequireAuthentication = !WebApplicationAuthExtensions
                    .IsWebdavAuthDisabled();
            });

        // force instantiation of services
        var app = builder.Build();
        app.Services.GetRequiredService<ArrMonitoringService>();
        app.Services.GetRequiredService<HealthCheckService>();

        // validate dependency injection configuration
        ValidateDependencyInjection(app.Services);

        // run
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseWebSockets();
        app.MapHealthChecks("/health");
        app.Map("/ws", websocketManager.HandleRoute);
        app.MapControllers();
        app.UseWebdavBasicAuthentication();
        app.UseNWebDav();
        app.Lifetime.ApplicationStopping.Register(SigtermUtil.Cancel);

        // LOW-1 FIX: Dispose ConfigManager on shutdown
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            configManager?.Dispose();
        });

        await app.RunAsync();
    }
}
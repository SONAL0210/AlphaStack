using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AlphaStack.Application.Common.Interfaces;
using AlphaStack.Application.Features.Analytics;
using AlphaStack.Application.Features.Trading;
using AlphaStack.Infrastructure.BackgroundServices;
using AlphaStack.Infrastructure.Caching;
using AlphaStack.Infrastructure.ExternalServices.Fyers;
using AlphaStack.Infrastructure.ExternalServices.KiteConnect;
using AlphaStack.Infrastructure.ExternalServices.KiteConnect.Strategies;
using AlphaStack.Infrastructure.ExternalServices.Telegram;
using AlphaStack.Infrastructure.Persistence;
using AlphaStack.Infrastructure.Persistence.Repositories;
using AlphaStack.Infrastructure.Security;
using AlphaStack.Infrastructure.Strategies;
using AlphaStack.Infrastructure.Repositories;

namespace AlphaStack.Infrastructure;

public static class InfrastructureServiceRegistration
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Database ──────────────────────────────────────────────────────────
        services.AddDbContext<TradingDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Postgres"),
                npgsql => npgsql.MigrationsAssembly(typeof(TradingDbContext).Assembly.FullName)
            ));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<TradingDbContext>());

        // ── Repositories ──────────────────────────────────────────────────────
        services.AddScoped<IUserProfileRepository,        UserProfileRepository>();
        services.AddScoped<IStrategyDefinitionRepository, StrategyDefinitionRepository>();
        services.AddScoped<IStrategyExecutionRepository,  StrategyExecutionRepository>();
        services.AddScoped<ITradeOrderRepository,         TradeOrderRepository>();
        services.AddScoped<ITradeRepository,              TradeRepository>();
        services.AddScoped<IPositionRepository,           PositionRepository>();
        services.AddScoped<IInstrumentRepository,         InstrumentRepository>();

        // ── NEW: Analytics ────────────────────────────────────────────────────
        services.AddScoped<ITradeAnalyticsRepository,     TradeAnalyticsRepository>();
        services.AddScoped<CsvExportService>();

        // ── Redis ─────────────────────────────────────────────────────────────
        services.AddStackExchangeRedisCache(options =>
            options.Configuration = configuration.GetConnectionString("Redis"));

        services.AddSingleton<IRedisCacheService, RedisCacheService>();

        // ── Encryption ────────────────────────────────────────────────────────
        var keyRingPath = configuration["DataProtection:KeyRingPath"] ?? "./keys";
        var keyRingDirectory = new DirectoryInfo(Path.GetFullPath(keyRingPath));
        keyRingDirectory.Create();

        services.AddDataProtection()
            .PersistKeysToFileSystem(keyRingDirectory);
        services.AddSingleton<IEncryptionService, DataProtectionEncryptionService>();

        // ── HTTP Clients ──────────────────────────────────────────────────────
        services.AddHttpClient("KiteConnect", client =>
        {
            client.BaseAddress = new Uri(configuration["KiteConnect:BaseUrl"]
                ?? "https://api.kite.trade");
            client.DefaultRequestHeaders.Add("X-Kite-Version", "3");
        });

        services.AddHttpClient("Fyers", client =>
        {
            client.BaseAddress = new Uri("https://api-t1.fyers.in/data/");
            client.Timeout = TimeSpan.FromSeconds(15);

            var accessToken = configuration["Fyers:AccessToken"];
            var clientId    = configuration["Fyers:ClientId"];

            if (!string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(clientId))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"{clientId}:{accessToken}");
                client.DefaultRequestHeaders.TryAddWithoutValidation("version", "3");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json");
            }
        });

        services.AddHttpClient("FyersAuth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddHttpClient("FyersData", client =>
        {
            client.BaseAddress = new Uri("https://api-t1.fyers.in/data/");
            client.Timeout = TimeSpan.FromSeconds(15);

            var accessToken = configuration["Fyers:AccessToken"];
            var clientId    = configuration["Fyers:ClientId"];

            if (!string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(clientId))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"{clientId}:{accessToken}");
                client.DefaultRequestHeaders.TryAddWithoutValidation("version", "3");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json");
            }
        });

        services.AddSingleton<FyersTokenService>();

        // ── External Services ─────────────────────────────────────────────────
        services.AddScoped<IKiteAuthService, KiteAuthService>();

        var marketDataProvider = configuration["MarketData:Provider"] ?? "Kite";
        services.AddScoped<IKiteMarketDataService, KiteMarketDataService>();

        if (marketDataProvider.Equals("Fyers", StringComparison.OrdinalIgnoreCase))
            services.AddScoped<IMarketDataProvider, FyersMarketDataProvider>();
        else
            services.AddScoped<IMarketDataProvider, KiteMarketDataService>();

        services.AddScoped<ITelegramNotificationService, TelegramNotificationService>();

        // ── Strategy Engines ──────────────────────────────────────────────────
        services.AddScoped<IStrategyEngine, BullPutSpreadEngine>();
        services.AddScoped<IStrategyEngine, BearCallSpreadEngine>();
        services.AddScoped<IStrategyEngine, FinniftyBullPutEngine>();
        services.AddScoped<IStrategyEngine, FinniftyBearCallEngine>();
        services.AddScoped<IStrategyEngine, NiftyIronCondorEngine>();
        services.AddScoped<IStrategyEngine, FinniftyIronCondorEngine>();
        services.AddScoped<IStrategyEngineFactory, StrategyEngineFactory>();

        // ── Trading Pipeline ──────────────────────────────────────────────────
        services.AddScoped<IRiskManager, RiskManager>();
        services.AddScoped<PaperOrderSimulator>();
        services.AddScoped<SignalProcessor>();

        // ── Background Services ───────────────────────────────────────────────
        services.AddHostedService<StrategyRunnerService>();
        services.AddHostedService<PnLTrackerService>();
        services.AddHostedService<StuckOrderMonitorService>();
        
        // InstrumentSyncService registered as singleton so IInstrumentSyncState
        // can be injected into scoped strategy engines
        services.AddSingleton<InstrumentSyncService>();
        services.AddSingleton<IInstrumentSyncState>(sp => 
            sp.GetRequiredService<InstrumentSyncService>());
        services.AddHostedService(sp => 
            sp.GetRequiredService<InstrumentSyncService>());

        services.AddScoped<ITradeAnalyticsRepository, TradeAnalyticsRepository>();
        services.AddScoped<IShadowTradeRepository,    ShadowTradeRepository>();        
        services.AddScoped<ShadowTradeLoggerService>();
        services.AddScoped<ShadowCsvExportService>();                                
        services.AddHostedService<FyersTokenReminderService>();

        return services;
    }
}

using Hangfire;
using Hangfire.Pro.Redis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingPilot.Blazor.Components;
using TradingPilot.Blazor.Client;
using TradingPilot.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.AspNetCore.Components.WebAssembly.WebApp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.UI.Bundling;
using Volo.Abp.AspNetCore.Mvc.Libs;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using Volo.Abp.AspNetCore.Components.WebAssembly.Theming.Bundling;
using Volo.Abp.AspNetCore.Components.WebAssembly.BasicTheme.Bundling;
using Volo.Abp.Swashbuckle;
using TradingPilot.Questrade;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using TradingPilot.Webull;

namespace TradingPilot.Blazor;

[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpAspNetCoreComponentsWebAssemblyBasicThemeBundlingModule),
    typeof(AbpAspNetCoreMvcUiBundlingModule),
    typeof(TradingPilotApplicationModule),
    typeof(TradingPilotHttpApiModule),
    typeof(TradingPilotEntityFrameworkCoreModule),
    typeof(AbpSwashbuckleModule),
    typeof(AbpAspNetCoreSerilogModule)
)]
public class TradingPilotBlazorModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();

        //https://github.com/dotnet/aspnetcore/issues/52530
        Configure<RouteOptions>(options =>
        {
            options.SuppressCheckForUnhandledSecurityMetadata = true;
        });

        // Add services to the container.
        context.Services.AddRazorComponents()
            .AddInteractiveWebAssemblyComponents();

        Configure<AbpMvcLibsOptions>(options =>
        {
            options.CheckLibs = false;
        });

        Configure<AbpBundlingOptions>(options =>
        {
            var globalStyles = options.StyleBundles.Get(BlazorWebAssemblyStandardBundles.Styles.Global);
            globalStyles.AddContributors(typeof(TradingPilotStyleBundleContributor));

            var globalScripts = options.ScriptBundles.Get(BlazorWebAssemblyStandardBundles.Scripts.Global);
            globalScripts.AddContributors(typeof(TradingPilotScriptBundleContributor));
        });

        // Auto-generate API controllers from application services
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers.Create(typeof(TradingPilotApplicationModule).Assembly);
        });

        // Swagger
        context.Services.AddAbpSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
            {
                Title = "TradingPilot API",
                Version = "v1"
            });
            options.DocInclusionPredicate((_, _) => true);
            options.CustomSchemaIds(type => type.FullName);
        });

        // Hangfire with Redis storage (database 10)
        context.Services.AddHangfire(config =>
        {
            config.UseRedisStorage(configuration["Redis:Configuration"] + ",defaultDatabase=10", new RedisStorageOptions
            {
                Prefix = "TradingPilot:"
            });
        });
        context.Services.AddHangfireServer();

        // Webull API client (typed HttpClient)
        context.Services.AddHttpClient<IWebullApiClient, WebullApiClient>(x =>
        {
            x.BaseAddress = new Uri("https://quotes-gw.webullfintech.com");
        });

        // L2 book cache (singleton in-memory rolling window)
        context.Services.AddSingleton<L2BookCache>();

        // Tick data cache (singleton — real-time tick/quote data)
        context.Services.AddSingleton<TickDataCache>();

        // Bar indicator cache (singleton — pre-computed technical indicators)
        context.Services.AddSingleton<BarIndicatorCache>();

        // Bar indicator service (transient — computes indicators from DB bars)
        context.Services.AddTransient<BarIndicatorService>();

        // Webull gRPC client (singleton — long-lived HTTP/2 channel)
        context.Services.AddSingleton<WebullGrpcClient>();

        // Strategy rule evaluator (singleton — evaluates AI-generated rules at runtime)
        context.Services.AddSingleton<StrategyRuleEvaluator>();

        // Swin vision model for L2 heatmap prediction (singleton — loads ONNX model)
        context.Services.AddSingleton<SwinPredictor>();

        // Trading signal analysis engine (singleton — analyzes L2 data for buy/sell signals)
        context.Services.AddSingleton<MarketMicrostructureAnalyzer>();
        context.Services.AddSingleton<SignalStore>();

        // Paper trading client (typed HttpClient — no base address since we use full URLs)
        context.Services.AddHttpClient<WebullPaperTradingClient>();

        // Broker client abstraction (singleton — swap via Broker:Type in appsettings.json)
        var brokerType = context.Services.GetConfiguration().GetValue<string>("Broker:Type") ?? "WebullPaper";
        if (brokerType == "Questrade")
        {
            context.Services.AddSingleton<QuestradeBrokerClient>();
            context.Services.AddSingleton<IBrokerClient>(sp => sp.GetRequiredService<QuestradeBrokerClient>());
        }
        else
        {
            context.Services.AddSingleton<WebullBrokerClient>();
            context.Services.AddSingleton<IBrokerClient>(sp => sp.GetRequiredService<WebullBrokerClient>());
        }

        // Trading executor (singleton — auto-executes trades from signals via IBrokerClient)
        context.Services.AddSingleton<PaperTradingExecutor>();

        // Position monitor (singleton — continuous background exit evaluation, 5s timer)
        context.Services.AddSingleton<PositionMonitor>();

        // MQTT message processor (singleton — processes real-time MQTT data into structured DB entities)
        context.Services.AddSingleton<MqttMessageProcessor>();

        // Nightly AI strategy optimizer (transient — Bedrock Sonnet 4.6, runs after market close)
        context.Services.AddTransient<NightlyStrategyOptimizer>();

        // Register the Webull hook hosted service
        context.Services.AddHostedService<WebullHookHostedService>();
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var env = context.GetEnvironment();
        var app = context.GetApplicationBuilder();

        // Configure the HTTP request pipeline.
        if (env.IsDevelopment())
        {
            app.UseWebAssemblyDebugging();
        }
        else
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseRouting();
        app.MapAbpStaticAssets();
        app.UseUnitOfWork();
        app.UseAntiforgery();

        app.UseSwagger();
        app.UseAbpSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "TradingPilot API");
        });
        app.UseAbpSerilogEnrichers();
        app.UseHangfireDashboard("/hangfire", new Hangfire.DashboardOptions
        {
            Authorization = [new HangfireNoAuthFilter()]
        });

        app.UseConfiguredEndpoints(builder =>
        {
            builder.MapRazorComponents<App>()
                .AddInteractiveWebAssemblyRenderMode()
                .AddAdditionalAssemblies(WebAppAdditionalAssembliesHelper.GetAssemblies<TradingPilotBlazorClientModule>());
        });

        var jobClient = context.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
        var recurringJobs = context.ServiceProvider.GetRequiredService<IRecurringJobManager>();

        // Schedule startup bar load — only m1 (used by BarIndicatorService for EMA/RSI/VWAP)
        string[] tickers = ["AMD", "RKLB", "NVDA", "TSLA", "PLTR", "SOFI", "SMCI", "RIVN", "SMR", "LLY"];
        string[] startupTimeframes = ["m1"];
        for (int i = 0; i < tickers.Length; i++)
        {
            var ticker = tickers[i];
            var tf = startupTimeframes;
            jobClient.Schedule<LoadHistoricalBarsJob>(
                job => job.ExecuteAsync(ticker, tf),
                TimeSpan.FromSeconds(30 + i * 5)); // stagger by 5s
        }

        // Recurring bar refresh (every 30 min during market hours to keep data fresh)
        recurringJobs.AddOrUpdate<LoadHistoricalBarsJob>(
            "refresh-bars",
            job => job.RefreshAllAsync(),
            "*/30 * * * *");
        recurringJobs.AddOrUpdate<RefreshNewsJob>(
            "refresh-news",
            job => job.ExecuteAsync(),
            "*/5 * * * *");
        recurringJobs.AddOrUpdate<RefreshFundamentalsJob>(
            "refresh-fundamentals",
            job => job.ExecuteAsync(),
            "*/30 * * * *"); // every 30 min

        // Clean up deprecated jobs from Redis
        recurringJobs.RemoveIfExists("signal-verification");

        // Nightly AI strategy optimization: 9 PM ET weekdays (after market close)
        // Backfills gaps + verifies signal outcomes + calls Bedrock Sonnet 4.6 per symbol
        recurringJobs.AddOrUpdate<NightlyStrategyOptimizer>(
            "nightly-strategy-optimization",
            optimizer => optimizer.OptimizeAsync(20),
            "0 21 * * 1-5",
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
            });

        // Nightly local training: 9:30 PM ET weekdays (after Bedrock optimization)
        // Hill-climbing weight optimization -> model_config.json
        recurringJobs.AddOrUpdate<NightlyLocalTrainer>(
            "nightly-local-training",
            trainer => trainer.TrainAsync(20),
            "30 21 * * 1-5",
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
            });

        // Nightly data cleanup: 10 PM ET weekdays (after both trainers complete)
        recurringJobs.AddOrUpdate<NightlyStrategyOptimizer>(
            "nightly-data-cleanup",
            optimizer => optimizer.CleanupOldDataAsync(),
            "0 22 * * 1-5",
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
            });

        // Eagerly resolve PositionMonitor to start its timer
        context.ServiceProvider.GetRequiredService<PositionMonitor>();

        // One-shot startup recovery (30s delay for auth capture)
        // Lightweight: takes L2 snapshot + backfills news only.
        // Heavy backfill (TickSnapshots + TradingSignals from bars) runs nightly.
        jobClient.Schedule<StartupRecoveryJob>(
            job => job.ExecuteAsync(),
            TimeSpan.FromSeconds(30));
    }
}

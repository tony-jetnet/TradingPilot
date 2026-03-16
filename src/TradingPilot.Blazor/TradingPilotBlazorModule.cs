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
using TradingPilot.Symbols;
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

        // MQTT message processor (singleton — processes real-time MQTT data into structured DB entities)
        context.Services.AddSingleton<MqttMessageProcessor>();

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
        app.UseHangfireDashboard("/hangfire");

        app.UseConfiguredEndpoints(builder =>
        {
            builder.MapRazorComponents<App>()
                .AddInteractiveWebAssemblyRenderMode()
                .AddAdditionalAssemblies(WebAppAdditionalAssembliesHelper.GetAssemblies<TradingPilotBlazorClientModule>());
        });

        var jobClient = context.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
        var recurringJobs = context.ServiceProvider.GetRequiredService<IRecurringJobManager>();

        // Schedule historical bars load (staggered, 30s delay for auth capture)
        string[] tickers = ["AMD", "RKLB"];
        string[] timeframes = ["d", "m1", "m5", "m15", "m30", "h1"];
        for (int i = 0; i < tickers.Length; i++)
        {
            var ticker = tickers[i];
            var tf = timeframes;
            jobClient.Schedule<LoadHistoricalBarsJob>(
                job => job.ExecuteAsync(ticker, tf),
                TimeSpan.FromSeconds(30 + i * 60)); // stagger by 60s
        }

        // Schedule recurring L2 depth polling (every minute, job internally polls 12x at 5s)
        recurringJobs.AddOrUpdate<PollL2DepthJob>(
            "poll-l2-depth",
            job => job.ExecuteAsync(),
            Cron.Minutely());
        recurringJobs.AddOrUpdate<RefreshNewsJob>(
            "refresh-news",
            job => job.ExecuteAsync(),
            "*/5 * * * *");
        recurringJobs.AddOrUpdate<RefreshFundamentalsJob>(
            "refresh-fundamentals",
            job => job.ExecuteAsync(),
            "*/30 * * * *"); // every 30 min

        // One-shot startup recovery (30s delay for auth capture)
        jobClient.Schedule<StartupRecoveryJob>(
            job => job.ExecuteAsync(),
            TimeSpan.FromSeconds(30));
    }
}

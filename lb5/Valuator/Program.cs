using StackExchange.Redis;
using Microsoft.AspNetCore.DataProtection;
using Valuator.Services;
using Valuator.Shared;
using Valuator.Hubs;
using Microsoft.AspNetCore.Http.Connections;

namespace Valuator;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddRazorPages();
        builder.Services.AddSignalR().AddStackExchangeRedis(
            builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379,abortConnect=false",
            options => options.Configuration.ChannelPrefix = RedisChannel.Literal("Valuator.PA5"));
        // All replicas must decrypt the same antiforgery cookies and form tokens.
        string keyPath = builder.Configuration["DataProtection:KeyPath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "..", ".data", "keys");
        builder.Services.AddDataProtection()
            .SetApplicationName("Valuator.PA5")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")
                ?? "localhost:6379,abortConnect=false"));
        builder.Services.AddSingleton<EvaluationStore>();
        builder.Services.AddSingleton(_ => RankQueue.CreateFactory(
            builder.Configuration["RabbitMQ:Host"] ?? "localhost",
            builder.Configuration["RabbitMQ:User"] ?? "valuator",
            builder.Configuration["RabbitMQ:Password"] ?? "valuator-local"));
        builder.Services.AddSingleton<EventPublisher>();
        builder.Services.AddHostedService<BrowserEventBridge>();
        builder.Services.AddHostedService(provider => new EventOutboxDispatcher(
            CalculationEvent.SimilarityCalculated, provider.GetRequiredService<EvaluationStore>(),
            provider.GetRequiredService<EventPublisher>(), provider.GetRequiredService<ILogger<EventOutboxDispatcher>>()));
        builder.Services.AddSingleton<RankPublisher>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<RankPublisher>());

        var app = builder.Build();
        string instance = builder.Configuration["InstanceName"] ?? $"process-{Environment.ProcessId}";
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Valuator-Instance"] = instance;
            await next(context);
        });

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
        }
        app.UseStaticFiles();

        app.UseRouting();

        app.UseAuthorization();

        app.MapRazorPages();
        app.MapHub<EvaluationHub>("/hubs/evaluation", options => options.Transports = HttpTransportType.WebSockets);
        app.MapGet("/health", () => Results.Ok(new { instance }));

        app.Run();
    }
}

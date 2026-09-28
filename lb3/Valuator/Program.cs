using StackExchange.Redis;
using Microsoft.AspNetCore.DataProtection;
using Valuator.Services;
using Valuator.Shared;

namespace Valuator;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddRazorPages();
        // All replicas must decrypt the same antiforgery cookies and form tokens.
        string keyPath = builder.Configuration["DataProtection:KeyPath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "..", ".data", "keys");
        builder.Services.AddDataProtection()
            .SetApplicationName("Valuator.PA3")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")
                ?? "localhost:6379,abortConnect=false"));
        builder.Services.AddSingleton<EvaluationStore>();
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
        app.MapGet("/health", () => Results.Ok(new { instance }));

        app.Run();
    }
}

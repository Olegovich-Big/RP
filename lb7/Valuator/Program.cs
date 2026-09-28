using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
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
        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizePage("/Index");
            options.Conventions.AuthorizePage("/Summary");
            options.Conventions.AuthorizePage("/Account/Logout");
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.Cookie.Name = "Valuator.PA7.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(2);
            options.SlidingExpiration = false;
        });
        builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
        builder.Services.AddSingleton<UserStore>();
        // All replicas must decrypt the same antiforgery cookies and form tokens.
        string keyPath = builder.Configuration["DataProtection:KeyPath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, "..", ".data", "keys");
        builder.Services.AddDataProtection()
            .SetApplicationName("Valuator.PA7")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(MiddlewareSettings.Redis(builder.Configuration)));
        builder.Services.AddSingleton<EvaluationStore>();
        builder.Services.AddSingleton(_ => RankQueue.CreateFactory(
            builder.Configuration["RabbitMQ:Host"] ?? "localhost",
            builder.Configuration["RabbitMQ:User"] ?? "valuator",
            MiddlewareSettings.Required(builder.Configuration, "RabbitMQ:Password")));
        builder.Services.AddSingleton<EventPublisher>();
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

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapRazorPages();
        app.MapGet("/health", () => Results.Ok(new { instance }));

        app.Run();
    }
}

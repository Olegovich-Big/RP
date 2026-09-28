using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Valuator.Shared;
using RankCalculator;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(
    MiddlewareSettings.Redis(builder.Configuration)));
builder.Services.AddSingleton<EvaluationStore>();
builder.Services.AddSingleton(_ => RankQueue.CreateFactory(
    builder.Configuration["RabbitMQ:Host"] ?? "localhost",
    builder.Configuration["RabbitMQ:User"] ?? "valuator",
    MiddlewareSettings.Required(builder.Configuration, "RabbitMQ:Password")));
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddHostedService(provider => new EventOutboxDispatcher(
    CalculationEvent.RankCalculated, provider.GetRequiredService<EvaluationStore>(),
    provider.GetRequiredService<EventPublisher>(), provider.GetRequiredService<ILogger<EventOutboxDispatcher>>()));
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();

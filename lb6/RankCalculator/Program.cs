using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Valuator.Shared;
using RankCalculator;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<ShardConnections>();
builder.Services.AddSingleton<EvaluationStore>();
builder.Services.AddSingleton(_ => RankQueue.CreateFactory(
    builder.Configuration["RabbitMQ:Host"] ?? "localhost",
    builder.Configuration["RabbitMQ:User"] ?? "valuator",
    builder.Configuration["RabbitMQ:Password"] ?? "valuator-local"));
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddHostedService(provider => new EventOutboxDispatcher(
    CalculationEvent.RankCalculated, provider.GetRequiredService<EvaluationStore>(),
    provider.GetRequiredService<EventPublisher>(), provider.GetRequiredService<ILogger<EventOutboxDispatcher>>()));
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();

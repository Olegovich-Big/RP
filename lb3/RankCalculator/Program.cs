using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Valuator.Shared;
using RankCalculator;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379,abortConnect=false"));
builder.Services.AddSingleton<EvaluationStore>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();

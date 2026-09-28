using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EventsLogger;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<EventSubscriber>();
await builder.Build().RunAsync();

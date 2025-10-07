using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Removed .ConfigureFunctionsWebApplication() as it is not available for HostBuilder.
// The rest of the configuration remains unchanged.

var host = new HostBuilder()
    //.ConfigureFunctionsWebApplication() // Removed this line to fix CS1061
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

host.Run();
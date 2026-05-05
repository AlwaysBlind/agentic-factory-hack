using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner;
using RepairPlanner.Models;
using RepairPlanner.Services;

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.ClearProviders();
    builder.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    builder.SetMinimumLevel(LogLevel.Information);
});

var aiProjectEndpoint = GetRequiredEnvVar("AZURE_AI_PROJECT_ENDPOINT");
services.AddSingleton(_ => new AIProjectClient(new Uri(aiProjectEndpoint), new DefaultAzureCredential()));

var cosmosOptions = new CosmosDbOptions
{
    Endpoint = GetRequiredEnvVar("COSMOS_ENDPOINT"),
    Key = GetRequiredEnvVar("COSMOS_KEY"),
    DatabaseName = GetRequiredEnvVar("COSMOS_DATABASE_NAME"),
};
services.AddSingleton(cosmosOptions);

services.AddSingleton(_ => new CosmosClient(
    cosmosOptions.Endpoint,
    cosmosOptions.Key,
    new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway }));

services.AddSingleton(sp => new CosmosDbService(
    sp.GetRequiredService<CosmosClient>(),
    cosmosOptions.DatabaseName,
    sp.GetRequiredService<ILogger<CosmosDbService>>(),
    cosmosOptions.TechniciansContainerName,
    cosmosOptions.PartsInventoryContainerName,
    cosmosOptions.WorkOrdersContainerName));

services.AddSingleton<IFaultMappingService, FaultMappingService>();

services.AddSingleton(sp => new RepairPlannerAgent(
    sp.GetRequiredService<AIProjectClient>(),
    sp.GetRequiredService<CosmosDbService>(),
    sp.GetRequiredService<IFaultMappingService>(),
    GetRequiredEnvVar("MODEL_DEPLOYMENT_NAME"),
    sp.GetRequiredService<ILogger<RepairPlannerAgent>>()));

await using var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

var planner = provider.GetRequiredService<RepairPlannerAgent>();
await planner.EnsureAgentVersionAsync();

var sampleFault = new DiagnosedFault
{
    MachineId = "machine-001",
    FaultType = "curing_temperature_excessive",
    Severity = "high",
    RootCause = "Heater element likely failing or thermocouple drift causing overshoot.",
    DetectedAt = DateTimeOffset.UtcNow,
    Metadata =
    {
        ["temperatureC"] = 195,
        ["setpointC"] = 170,
        ["pressureBar"] = 150,
    },
};

try
{
    var saved = await planner.PlanAndCreateWorkOrderAsync(sampleFault);
    logger.LogInformation(
        "Saved work order {WorkOrderNumber} (id={Id}, status={Status}, assignedTo={AssignedTo})",
        saved.WorkOrderNumber,
        saved.Id,
        saved.Status,
        saved.AssignedTo ?? "<unassigned>");

    Console.WriteLine(JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
}
catch (Exception ex)
{
    logger.LogError(ex, "Repair planning workflow failed.");
    Environment.ExitCode = 1;
}

static string GetRequiredEnvVar(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing required environment variable: {name}");

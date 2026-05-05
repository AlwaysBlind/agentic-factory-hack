using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

public sealed class CosmosDbService
{
    private readonly ILogger<CosmosDbService> _logger;
    private readonly Container _technicians;
    private readonly Container _partsInventory;
    private readonly Container _workOrders;

    public CosmosDbService(
        CosmosClient cosmosClient,
        string databaseName,
        ILogger<CosmosDbService> logger,
        string techniciansContainerName = "Technicians",
        string partsInventoryContainerName = "PartsInventory",
        string workOrdersContainerName = "WorkOrders")
    {
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _technicians = cosmosClient.GetContainer(databaseName, techniciansContainerName);
        _partsInventory = cosmosClient.GetContainer(databaseName, partsInventoryContainerName);
        _workOrders = cosmosClient.GetContainer(databaseName, workOrdersContainerName);
    }

    public async Task<IReadOnlyList<Technician>> QueryAvailableTechniciansBySkillsAsync(
        IEnumerable<string> requiredSkills,
        string? department = null,
        CancellationToken cancellationToken = default)
    {
        var skills = (requiredSkills ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        QueryDefinition query = string.IsNullOrWhiteSpace(department)
            ? new QueryDefinition("SELECT * FROM c WHERE c.available = true")
            : new QueryDefinition("SELECT * FROM c WHERE c.available = true AND c.department = @department")
                .WithParameter("@department", department);

        QueryRequestOptions? requestOptions = string.IsNullOrWhiteSpace(department)
            ? null
            : new QueryRequestOptions { PartitionKey = new PartitionKey(department) };

        try
        {
            var results = new List<Technician>();
            using var iterator = _technicians.GetItemQueryIterator<Technician>(query, requestOptions: requestOptions);
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken);
                results.AddRange(page);
            }

            if (skills.Length == 0)
            {
                _logger.LogInformation(
                    "Found {Count} available technicians (department={Department})",
                    results.Count, department ?? "<any>");
                return results;
            }

            var ranked = results
                .Select(t => new
                {
                    Technician = t,
                    MatchCount = t.Skills.Count(s => skills.Contains(s, StringComparer.OrdinalIgnoreCase)),
                })
                .Where(x => x.MatchCount > 0)
                .OrderByDescending(x => x.MatchCount)
                .ThenBy(x => x.Technician.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Technician)
                .ToList();

            _logger.LogInformation(
                "Found {Count} available technicians matching skills [{Skills}]",
                ranked.Count, string.Join(", ", skills));

            return ranked;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Technicians container not found.");
            return Array.Empty<Technician>();
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error querying technicians.");
            throw;
        }
    }

    public async Task<IReadOnlyList<Part>> GetPartsByPartNumbersAsync(
        IEnumerable<string> partNumbers,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partNumbers);

        var numbers = partNumbers
            .Where(pn => !string.IsNullOrWhiteSpace(pn))
            .Select(pn => pn.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (numbers.Length == 0)
        {
            return Array.Empty<Part>();
        }

        var queryText = "SELECT * FROM c WHERE ARRAY_CONTAINS(@partNumbers, c.partNumber)";
        if (!string.IsNullOrWhiteSpace(category))
        {
            queryText += " AND c.category = @category";
        }

        var query = new QueryDefinition(queryText).WithParameter("@partNumbers", numbers);
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.WithParameter("@category", category);
        }

        QueryRequestOptions? requestOptions = string.IsNullOrWhiteSpace(category)
            ? null
            : new QueryRequestOptions { PartitionKey = new PartitionKey(category) };

        try
        {
            var results = new List<Part>();
            using var iterator = _partsInventory.GetItemQueryIterator<Part>(query, requestOptions: requestOptions);
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken);
                results.AddRange(page);
            }

            _logger.LogInformation("Fetched {Count} parts for [{PartNumbers}]",
                results.Count, string.Join(", ", numbers));
            return results;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Parts inventory container not found.");
            return Array.Empty<Part>();
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error fetching parts.");
            throw;
        }
    }

    public async Task<WorkOrder> CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        if (string.IsNullOrWhiteSpace(workOrder.Id))
        {
            workOrder.Id = $"wo-{Guid.NewGuid():N}";
        }

        if (string.IsNullOrWhiteSpace(workOrder.WorkOrderNumber))
        {
            workOrder.WorkOrderNumber = $"WO-{DateTimeOffset.UtcNow:yyyyMMdd}-{Random.Shared.Next(1, 999):D3}";
        }

        if (string.IsNullOrWhiteSpace(workOrder.Status))
        {
            workOrder.Status = "new";
        }

        try
        {
            var response = await _workOrders.CreateItemAsync(
                workOrder,
                partitionKey: new PartitionKey(workOrder.Status),
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Created work order {Number} (id={Id}, status={Status}, RU={RU})",
                response.Resource.WorkOrderNumber, response.Resource.Id,
                response.Resource.Status, response.RequestCharge);

            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning(ex, "Work order id conflict for id={Id}", workOrder.Id);
            throw;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Cosmos DB error creating work order.");
            throw;
        }
    }
}

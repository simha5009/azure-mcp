using System.Text.Json;

namespace AzureMcp.Services.Interfaces;

public interface IMigrationService
{
    Task<Dictionary<string, string>> ListMigrationProjects(string subscriptionId);

    /// <summary>
    /// Summarizes the inventory for a given migration.
    /// </summary>
    /// <param name="inventoryId">The ID of the inventory to summarize.</param>
    /// <param name="subscription">The subscription ID or name.</param>
    /// <param name="tenantId">Optional tenant ID for cross-tenant operations.</param>
    /// <returns>A summary of the inventory.</returns>
    Task<JsonDocument> SummarizeInventoryAsync(string projectName, string subscription);

    Task<JsonDocument> GetProjectDetailsAsync(string projectName, string subscriptionId);

    Task<JsonDocument> SummarizeBusinessCaseAsync(string projectName, string subscriptionId);
}
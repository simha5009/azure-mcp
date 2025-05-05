using System.Text;
using System.Text.Json;
using Azure.Core;
using AzureMcp.Services.Azure.Authentication;
using AzureMcp.Services.Interfaces;

namespace AzureMcp.Services.Azure;

public class MigrationService(CustomChainedCredential? credential = null) : IMigrationService
{
    // API base URLs
    private const string AzureManagementBaseUrl = "https://management.azure.com";

    // API versions
    private const string ResourceGraphApiVersion = "2024-04-01";
    private const string MigrateProjectApiVersion = "2020-06-01-preview";
    private const string MasterSiteApiVersion = "2020-07-07";
    private const string BusinessCaseApiVersion = "2024-03-03-preview";

    // Full URLs
    private static readonly string AzureResourceGraphUrl = $"{AzureManagementBaseUrl}/providers/Microsoft.ResourceGraph/resources?api-version={ResourceGraphApiVersion}";
    private static readonly string[] ManagementScopes = new[] { $"{AzureManagementBaseUrl}/.default" };
    private readonly CustomChainedCredential _credential = credential ?? new CustomChainedCredential();

    /// <summary>
    /// Lists all migration projects in a subscription.
    /// </summary>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <returns>A dictionary containing project names and their IDs.</returns>
    public async Task<Dictionary<string, string>> ListMigrationProjects(string subscriptionId)
    {
        var requestBody = new
        {
            subscriptions = new[] { subscriptionId },
            query = @"resources
                | where type =~ 'microsoft.migrate/migrateprojects'"
        };

        var response = await ExecuteResourceGraphQueryAsync(requestBody);
        var projects = new Dictionary<string, string>();

        if (response.RootElement.TryGetProperty("data", out var dataElement) &&
            dataElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in dataElement.EnumerateArray())
            {
                var name = element.GetProperty("name").GetString() ?? string.Empty;
                var id = element.GetProperty("id").GetString() ?? string.Empty;

                if (!string.IsNullOrEmpty(name))
                {
                    projects[name] = id;
                }
            }
        }

        return projects;
    }

    /// <summary>
    /// Summarizes the inventory for a given migration project.
    /// </summary>
    /// <param name="projectName">The name of the migration project.</param>
    /// <param name="subscription">The subscription ID.</param>
    /// <returns>A JSON document containing inventory summary.</returns>
    public async Task<JsonDocument> SummarizeInventoryAsync(string projectName, string subscription)
    {
        var masterSiteId = await GetMasterSiteIdAsync(projectName, subscription);
        var sites = await GetSitesFromMasterSiteIdAsync(masterSiteId);
        var conditions = sites.Select(site => $"['id'] has '{site}'").ToList();
        var conditionsString = string.Join(" or ", conditions);
        var queryString = GenerateInventorySummaryQuery(conditionsString);

        var requestBody = new
        {
            subscriptions = new[] { subscription },
            query = queryString
        };

        return await ExecuteResourceGraphQueryAsync(requestBody);
    }

    /// <summary>
    /// Gets details for a specific migration project.
    /// </summary>
    /// <param name="projectName">The name of the migration project.</param>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <returns>A JSON document containing project details.</returns>
    public async Task<JsonDocument> GetProjectDetailsAsync(string projectName, string subscriptionId)
    {
        try
        {
            var projects = await ListMigrationProjects(subscriptionId);
            var projectId = projects.FirstOrDefault(p => p.Key == projectName).Value;

            if (string.IsNullOrEmpty(projectId))
            {
                throw new KeyNotFoundException($"Project '{projectName}' not found in subscription '{subscriptionId}'");
            }
            var projectDetailsUri = $"{AzureManagementBaseUrl}{projectId}?api-version={MigrateProjectApiVersion}";
            return await ExecuteHttpGetRequestAsync(projectDetailsUri);
        }
        catch (Exception ex)
        {
            throw new Exception($"Error fetching project details: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Summarizes business case for a migration project.
    /// </summary>
    /// <param name="projectName">The name of the migration project.</param>
    /// <param name="subscriptionId">The subscription ID.</param>
    /// <returns>A JSON document containing business case summary.</returns>
    public async Task<JsonDocument> SummarizeBusinessCaseAsync(string projectName, string subscriptionId)
    {
        var assessmentProjectId = await GetAssessmentProjectIdAsync(projectName, subscriptionId);
        var businessCaseId = await GetBusinessCasesFromAssessmentProjectIdAsync(assessmentProjectId);
        var businessCaseSummaryUri = $"{AzureManagementBaseUrl}{businessCaseId}/overviewsummaries/default?api-version={BusinessCaseApiVersion}";

        return await ExecuteHttpGetRequestAsync(businessCaseSummaryUri);
    }

    private static string GenerateInventorySummaryQuery(string conditionsString)
    {
        return $@"
migrateresources
| where ['type'] in (""microsoft.offazure/vmwaresites/machines"", ""microsoft.offazure/serversites/machines"", ""microsoft.offazure/hypervsites/machines"", ""microsoft.offazure/importsites/machines"", ""microsoft.offazure/mastersites/sqlsites/sqlservers"", ""microsoft.offazure/mastersites/webappsites/iiswebapplications"", ""microsoft.offazure/mastersites/webappsites/tomcatwebapplications"", ""microsoft.offazure/importsites/machines"")
| where {conditionsString}
| extend type=tolower(type)
| extend properties_machineArmIds = iif(array_length(properties.machineArmIds) == 0, pack_array(id), properties.machineArmIds)
| mv-expand properties_machineArmIds
| extend machineArmIds=tostring(properties_machineArmIds)
| extend parentId = case(type contains ""/machines"", id, type contains ""/sqlservers"", machineArmIds, type contains ""/webappsites"", machineArmIds, """")
| extend id = tolower(id), siteId = case (id has ""machines"", tostring(split(tolower(id), ""/machines/"")[0]), id has ""sqlsites"", tostring(split(tolower(id), ""/sqlsites/"")[0]), id has ""webappsites"", tostring(split(tolower(id), ""/webappsites/"")[0]), """")
| extend parentId = tolower(parentId),
        armId = id,
        resourceType = type,
        resourceTags = properties.tags,
        resourceName = tostring(iff(type contains ""/sqlservers"", properties.sqlServerName, properties.displayName)),
        version = tostring(case (id has ""/machines/"", coalesce(properties.guestOSDetails.osName, properties.operatingSystemDetails.osName), id has ""/sqlsites/"", """", id has ""/webappsites/"", properties.version, """")),
        edition = tostring(case (id has ""/machines/"", coalesce(properties.guestOSDetails.osVersion, properties.operatingSystemDetails.osVersion), id has ""/sqlsites/"", properties.edition, id has ""/webappsites/"", properties.version, """")),
        osType = tostring(coalesce(properties.guestOSDetails.osType, properties.operatingSystemDetails.osType)),
        powerOnStatus = case (properties.powerStatus == ""ON"" or properties.powerStatus == ""Running"", ""On"", properties.powerStatus == ""OFF"" or properties.powerStatus == ""PowerOff"" or properties.powerStatus == ""Saved"" or properties.powerStatus == ""Paused"", ""Off"", ""-""),
        discoverySource = case (id contains ""microsoft.offazure/importsites"", ""Import"", id contains ""/sqlsites/"" and properties.discoveryState == ""Imported"", ""Import"", ""Appliance""),
        dbProperties = case (id has ""/sqlsites/"", properties, parse_json("""")),
        dbEngineStatus = tostring(case (id has ""/sqlsites/"", properties.status, """")),
        userdatabases = tostring(case (id has ""/sqlsites/"", properties.numberOfUserDatabases, """")),
        totalSizeInGB = properties.totalDiskSizeInGB,
        ipAddressList = properties.ipAddresses,
        totalWebAppCount = tolong(case (id has ""/machines/"", case (coalesce(tolong(properties.webAppDiscovery.totalWebApplicationCount), 0) == 0, coalesce(tolong(properties.iisDiscovery.totalWebApplicationCount), 0) + coalesce(tolong(properties.tomcatDiscovery.totalWebApplicationCount), 0), coalesce(tolong(properties.webAppDiscovery.totalWebApplicationCount), 0)), 0)),
        totalDatabaseInstances = tolong(case (id has ""/machines/"", coalesce(tolong(properties.totalInstanceCount), 0), 0)),
        memoryInMB = case (id has ""/sqlsites/"", tolong(properties.maxServerMemoryInUseInMb), tolong(properties.allocatedMemoryInMB)),
        dbhadrConfiguration = tostring(case (id has ""/sqlsites/"", (case (toboolean(properties.isClustered) and toboolean(properties.isHighAvailabilityEnabled), ""Both"", case (toboolean(properties.isClustered), ""FailoverClusterInstance"", case (toboolean(properties.isHighAvailabilityEnabled), ""AvailabilityGroup"", """")))), """")),
        diskCount = array_length(properties.disks),
        supportEndsIn = datetime_diff(""day"", todatetime(properties.productSupportStatus.supportEndDate), todatetime(now())),
        depmapErrorCount = array_length(properties.dependencyMapDiscovery.errors)
        | summarize
        Infrastructure = countif(['type'] in~(""microsoft.offazure/vmwaresites/machines"", ""microsoft.offazure/hypervsites/machines"", ""microsoft.offazure/serversites/machines"", ""microsoft.offazure/importsites/machines"")),
        Databases = countif(['type'] in~(""microsoft.offazure/mastersites/sqlsites/sqlservers"")),
        Webapps = countif(['type'] in~(""microsoft.offazure/mastersites/webappsites/iiswebapplications"", ""microsoft.offazure/mastersites/webappsites/tomcatwebapplications""))
        | extend total = Infrastructure + Databases + Webapps
        ";
    }

    private async Task<string> GetMasterSiteIdAsync(string projectName, string subscription)
    {
        var projects = await ListMigrationProjects(subscription);
        var projectId = projects.FirstOrDefault(p => p.Key == projectName).Value;
        if (string.IsNullOrEmpty(projectId))
        {
            throw new KeyNotFoundException($"Project '{projectName}' not found in subscription '{subscription}'");
        }

        var solutionsUri = $"https://management.azure.com{projectId}/solutions?api-version=2020-06-01-preview";
        var response = await ExecuteHttpGetRequestAsync(solutionsUri);

        var root = response.RootElement;
        string masterSiteIdValue = string.Empty;

        if (root.TryGetProperty("value", out var values))
        {
            foreach (var solution in values.EnumerateArray())
            {
                if (solution.GetProperty("name").GetString() == "Servers-Discovery-ServerDiscovery")
                {
                    if (solution.TryGetProperty("properties", out var properties) &&
                        properties.TryGetProperty("details", out var details) &&
                        details.TryGetProperty("extendedDetails", out var extendedDetails) &&
                        extendedDetails.TryGetProperty("masterSiteId", out var masterSiteId))
                    {
                        masterSiteIdValue = masterSiteId.GetString() ?? string.Empty;
                        break;
                    }
                }
            }
        }

        return masterSiteIdValue;
    }

    private async Task<List<string>> GetSitesFromMasterSiteIdAsync(string masterSiteId)
    {
        if (string.IsNullOrEmpty(masterSiteId))
        {
            throw new ArgumentException("Master site ID cannot be null or empty", nameof(masterSiteId));
        }

        var masterSiteIdUri = $"{AzureManagementBaseUrl}{masterSiteId}?api-version={MasterSiteApiVersion}";
        var response = await ExecuteHttpGetRequestAsync(masterSiteIdUri);

        var result = new List<string>();
        var root = response.RootElement;

        if (root.TryGetProperty("properties", out var properties))
        {
            if (properties.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Array)
            {
                foreach (var site in sites.EnumerateArray())
                {
                    var siteValue = site.GetString();
                    if (!string.IsNullOrEmpty(siteValue))
                    {
                        result.Add(siteValue);
                    }
                }
            }

            if (properties.TryGetProperty("nestedSites", out var nestedSites) && nestedSites.ValueKind == JsonValueKind.Array)
            {
                foreach (var nestedSite in nestedSites.EnumerateArray())
                {
                    var nestedSiteValue = nestedSite.GetString();
                    if (!string.IsNullOrEmpty(nestedSiteValue))
                    {
                        result.Add(nestedSiteValue);
                    }
                }
            }
        }

        return result;
    }

    private async Task<string> GetAssessmentProjectIdAsync(string projectName, string subscriptionId)
    {
        var projects = await ListMigrationProjects(subscriptionId);
        var projectId = projects.FirstOrDefault(p => p.Key == projectName).Value;

        if (string.IsNullOrEmpty(projectId))
        {
            throw new KeyNotFoundException($"Project '{projectName}' not found in subscription '{subscriptionId}'");
        }

        var solutionsUri = $"https://management.azure.com{projectId}/solutions?api-version=2020-06-01-preview";
        var response = await ExecuteHttpGetRequestAsync(solutionsUri);
        var root = response.RootElement;

        string assessmentProjectId = string.Empty;
        if (root.TryGetProperty("value", out var values))
        {
            foreach (var solution in values.EnumerateArray())
            {
                if (solution.GetProperty("name").GetString() == "Servers-Assessment-ServerAssessment")
                {
                    if (solution.TryGetProperty("properties", out var properties) &&
                        properties.TryGetProperty("details", out var details) &&
                        details.TryGetProperty("extendedDetails", out var extendedDetails) &&
                        extendedDetails.TryGetProperty("projectId", out var assessmentProjectIdElement))
                    {
                        assessmentProjectId = assessmentProjectIdElement.GetString() ?? string.Empty;
                        break;
                    }
                }
            }
        }

        return assessmentProjectId;
    }

    private async Task<string> GetBusinessCasesFromAssessmentProjectIdAsync(string assessmentProjectId)
    {
        if (string.IsNullOrEmpty(assessmentProjectId))
        {
            throw new ArgumentException("Assessment project ID cannot be null or empty", nameof(assessmentProjectId));
        }

        var businessCaseUri = $"{AzureManagementBaseUrl}{assessmentProjectId}/businessCases/?api-version={BusinessCaseApiVersion}&pageSize=30";
        var response = await ExecuteHttpGetRequestAsync(businessCaseUri);

        var root = response.RootElement;
        string businessCaseId = string.Empty;

        if (root.TryGetProperty("value", out var values))
        {
            foreach (var businessCase in values.EnumerateArray())
            {
                var id = businessCase.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    businessCaseId = id;
                    break;
                }
            }
        }

        return businessCaseId;
    }

    private async Task<AccessToken> GetAccessTokenAsync()
    {
        return await _credential.GetTokenAsync(new TokenRequestContext(ManagementScopes), cancellationToken: default);
    }

    private async Task<JsonDocument> ExecuteHttpGetRequestAsync(string uri)
    {
        var accessToken = await GetAccessTokenAsync();
        EnsureValidAccessToken(accessToken);

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        var response = await httpClient.GetAsync(uri);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP request failed with status code: {response.StatusCode}. Uri: {uri}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(responseContent);
    }

    private async Task<JsonDocument> ExecuteResourceGraphQueryAsync(object requestBody)
    {
        var accessToken = await GetAccessTokenAsync();
        EnsureValidAccessToken(accessToken);

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        string jsonBody = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(AzureResourceGraphUrl, content);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Resource Graph query failed with status code: {response.StatusCode}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(responseContent);
    }

    private static void EnsureValidAccessToken(AccessToken accessToken)
    {
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new UnauthorizedAccessException("Failed to obtain a valid access token for the Azure Management API");
        }
    }
}

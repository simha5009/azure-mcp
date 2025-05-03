using AzureMcp.Services.Interfaces;
using Azure.Identity;
using Azure.Core;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using AzureMcp.Services.Azure.Authentication;
using Azure.ResourceManager.ResourceGraph;

namespace AzureMcp.Services.Azure;

public class MigrationService : IMigrationService
{
    private static readonly string ARGurl = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01";

    public async Task<Dictionary<string, string>> ListMigrationProjects(string subscriptionId)
    {
        var accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new Exception("Failed to obtain access token.");
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        var requestBody = new
        {
            subscriptions = new[] { subscriptionId },
            query = @"resources
                | where type =~ 'microsoft.migrate/migrateprojects'"
        };

        string jsonBody = JsonSerializer.Serialize(requestBody);

        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(ARGurl, content);
        var responseContent = await response.Content.ReadAsStringAsync();

        var doc = JsonDocument.Parse(responseContent);
        var root = doc.RootElement;
        var projects = new Dictionary<string, string>();

        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
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

    public async Task<JsonDocument> SummarizeInventoryAsync(string projectName, string subscription)
    {
        var masterSiteId = await getMasterSiteId(projectName, subscription);
        var sites = await getSitesFromMasterSiteId(masterSiteId);
        var conditions = sites.Select(site => $"['id'] has '{site}'").ToList();
        var conditionsString = string.Join(" or ", conditions);
        var queryString = $@"
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
        var accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new Exception("Failed to obtain access token.");
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        var requestBody = new
        {
            subscriptions = new[] { subscription },
            query = queryString
        };

        string jsonBody = JsonSerializer.Serialize(requestBody);

        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(ARGurl, content);
        var responseContent = await response.Content.ReadAsStringAsync();

        var doc = JsonDocument.Parse(responseContent);
        return doc;
    }

    public async Task<string> getMasterSiteId(string projectName, string subscription)
    {
        var projects = ListMigrationProjects(subscription).Result;
        var projectId = projects.FirstOrDefault(p => p.Key == projectName).Value;
        var solutionsUri = "https://management.azure.com" + projectId + "/solutions?api-version=2020-06-01-preview";
        var accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new Exception("Failed to obtain access token.");
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        var response = await httpClient.GetAsync(solutionsUri);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new Exception($"Failed to get solutions. Status code: {response.StatusCode}");
        }
        var responseContent = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(responseContent);
        JsonElement root = doc.RootElement;
        string masterSiteIdValue = string.Empty;
        if (root.TryGetProperty("value", out JsonElement values))
        {
            foreach (JsonElement solution in values.EnumerateArray())
            {
                if (solution.GetProperty("name").GetString() == "Servers-Discovery-ServerDiscovery")
                {
                    if (solution.TryGetProperty("properties", out JsonElement properties) &&
                        properties.TryGetProperty("details", out JsonElement details) &&
                        details.TryGetProperty("extendedDetails", out JsonElement extendedDetails) &&
                        extendedDetails.TryGetProperty("masterSiteId", out JsonElement masterSiteId))
                    {
                        masterSiteIdValue = masterSiteId.GetString() ?? string.Empty;
                        break;
                    }
                }
            }
        }
        return masterSiteIdValue;
    }

    public async Task<List<string>> getSitesFromMasterSiteId(string masterSiteId)
    {
        var accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new Exception("Failed to obtain access token.");
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        var masterSiteIdUri = "https://management.azure.com" + masterSiteId + "?api-version=2020-07-07";
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new Exception("Failed to obtain access token.");
        }

        var response = await httpClient.GetAsync(masterSiteIdUri);
        var responseContent = await response.Content.ReadAsStringAsync();
        var result = new List<string>();

        try
        {
            using var jsonDoc = JsonDocument.Parse(responseContent);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("properties", out var properties))
            {
                if (properties.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Array)
                {
                    foreach (var site in sites.EnumerateArray())
                    {
                        result.Add(site.GetString() ?? string.Empty);
                    }
                }

                if (properties.TryGetProperty("nestedSites", out var nestedSites) && nestedSites.ValueKind == JsonValueKind.Array)
                {
                    foreach (var nestedSite in nestedSites.EnumerateArray())
                    {
                        result.Add(nestedSite.GetString() ?? string.Empty);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception("Error parsing response JSON: " + ex.Message);
        }

        return result;
    }

    public async Task<JsonDocument> GetProjectDetailsAsync(string projectName, string subscriptionId)
    {
        // Simulate fetching project details
        try
        {
            var projects = await ListMigrationProjects(subscriptionId);
            var projectId = projects.FirstOrDefault(p => p.Key == projectName).Value;
            var projectDetailsUri = "https://management.azure.com" + projectId + "?api-version=2020-06-01-preview";
            var accessToken = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken.Token))
            {
                throw new Exception("Failed to obtain access token.");
            }
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            var response = await httpClient.GetAsync(projectDetailsUri);
            var responseContent = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(responseContent);
            return doc;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error fetching project details: {ex.Message}", ex);
        }
    }

    public async Task<AccessToken> GetAccessTokenAsync()
    {
        var credential = new CustomChainedCredential();
        return await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://management.azure.com/.default" }), cancellationToken: default);
    }

    public async Task<string> getAssessmentProjectId(string projectName, string subscriptionId)
    {
        var projects = await ListMigrationProjects(subscriptionId);
        var projectId = projects.FirstOrDefault(p => p.Key == projectName).Value;
        var solutionsUri = "https://management.azure.com" + projectId + "/solutions?api-version=2020-06-01-preview";
        var accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new Exception("Failed to obtain access token.");
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        var response = await httpClient.GetAsync(solutionsUri);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new Exception($"Failed to get solutions. Status code: {response.StatusCode}");
        }
        var responseContent = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(responseContent);
        JsonElement root = doc.RootElement;
        string assessmentProjectId = string.Empty;
        if (root.TryGetProperty("value", out JsonElement values))
        {
            foreach (JsonElement solution in values.EnumerateArray())
            {
                if (solution.GetProperty("name").GetString() == "Servers-Assessment-ServerAssessment")
                {
                    if (solution.TryGetProperty("properties", out JsonElement properties) &&
                        properties.TryGetProperty("details", out JsonElement details) &&
                        details.TryGetProperty("extendedDetails", out JsonElement extendedDetails) &&
                        extendedDetails.TryGetProperty("projectId", out JsonElement assessmentProjectIdElement))
                    {
                        assessmentProjectId = assessmentProjectIdElement.GetString() ?? string.Empty;
                        break;
                    }
                }
            }
        }
        return assessmentProjectId;
    }

    public async Task<string> getBusinessCasesFromAssessmentProjectId(string assessmentProjectId)
    {
        var businessCaseUri = "https://management.azure.com" + assessmentProjectId + "/businessCases/?api-version=2024-03-03-preview&pageSize=30";
        var accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new Exception("Failed to obtain access token.");
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

        var response = await httpClient.GetAsync(businessCaseUri);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new Exception($"Failed to get business cases. Status code: {response.StatusCode}");
        }
        var responseContent = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(responseContent);
        JsonElement root = doc.RootElement;
        string businessCaseId = string.Empty;
        if (root.TryGetProperty("value", out JsonElement values))
        {
            foreach (JsonElement solution in values.EnumerateArray())
            {
                if (!string.IsNullOrEmpty(solution.GetProperty("id").GetString()))
                {
                    businessCaseId = solution.GetProperty("id").GetString() ?? string.Empty;
                }
            }
        }
        return businessCaseId;
    }

    public async Task<JsonDocument> SummarizeBusinessCaseAsync(string projectName, string subscriptionId)
    {
        var assessmentProjectId = await getAssessmentProjectId(projectName, subscriptionId);
        var businessCaseId = await getBusinessCasesFromAssessmentProjectId(assessmentProjectId);
        var businessCaseSummaryUri = "https://management.azure.com" + businessCaseId + "/overviewsummaries/default?api-version=2024-03-03-preview";
        var accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken.Token))
        {
            throw new Exception("Failed to obtain access token.");
        }
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);
        var response = await httpClient.GetAsync(businessCaseSummaryUri);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new Exception($"Failed to get business case summary. Status code: {response.StatusCode}");
        }
        var responseContent = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(responseContent);
        return doc;
    }
}
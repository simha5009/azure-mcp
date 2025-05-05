// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AzureMcp.Commands;
using AzureMcp.Extensions;
using AzureMcp.Models.Command;
using AzureMcp.Services.Azure;
using ModelContextProtocol.Protocol.Types;
using AzureMcp.Services.Azure.AppConfig;
using AzureMcp.Services.Azure.Cosmos;
using AzureMcp.Services.Azure.Monitor;
using AzureMcp.Services.Azure.ResourceGroup;
using AzureMcp.Services.Azure.Storage;
using AzureMcp.Services.Azure.Subscription;
using AzureMcp.Services.Azure.Tenant;
using AzureMcp.Services.Caching;
using AzureMcp.Services.Interfaces;
using AzureMcp.Services.ProcessExecution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AzureMcp.Commands.Server;

// Create a web application
var builder = WebApplication.CreateBuilder(args);

// Configure services for MCP
ConfigureServices(builder.Services);

// Register ToolOperations for MCP server
builder.Services.AddSingleton<AzureMcp.Commands.Server.ToolOperations>();

// Configure MCP server options using the options pattern
builder.Services.AddOptions<McpServerOptions>()
    .Configure<AzureMcp.Commands.Server.ToolOperations>((options, toolOperations) =>
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var assemblyName = entryAssembly?.GetName();
        var serverName = entryAssembly?.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "Azure MCP Server";

        options.ServerInfo = new ModelContextProtocol.Protocol.Types.Implementation
        {
            Name = serverName,
            Version = assemblyName?.Version?.ToString() ?? "1.0.0-beta"
        };

        // Register tool capabilities
        options.Capabilities = new ModelContextProtocol.Protocol.Types.ServerCapabilities
        {
            Tools = toolOperations.ToolsCapability
        };

        options.ProtocolVersion = "2024-11-05";
    });

// Add MCP server services
builder.Services.AddMcpServer().WithHttpTransport();

// Build the application
var app = builder.Build();

// Configure HTTP request pipeline
// Note: Using top-level route registrations as recommended (ASP0014)

app.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync("<!DOCTYPE html><html><head><title>Azure MCP Server</title></head><body>");
    await context.Response.WriteAsync("<h1>Azure MCP Server</h1>");
    await context.Response.WriteAsync("<p>The server is running. Access the API endpoints to interact with it.</p>");
    await context.Response.WriteAsync("</body></html>");
});

app.MapGet("/health", async context =>
{
    await context.Response.WriteAsync("Healthy");
});

// Map MCP endpoints
app.MapMcp();

try
{
    // Run the web application
    app.Run();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to start MCP server: {ex.Message}");
    return 1;
}

static void ConfigureServices(IServiceCollection services)
{
    services.ConfigureOpenTelemetry();
    services.AddMemoryCache();
    services.AddLogging(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    });
    services.AddSingleton<ICacheService, CacheService>();
    services.AddSingleton<IExternalProcessService, ExternalProcessService>();
    services.AddSingleton<ISubscriptionService, SubscriptionService>();
    services.AddSingleton<ITenantService, TenantService>();
    services.AddSingleton<ICosmosService, CosmosService>();
    services.AddSingleton<IStorageService, StorageService>();
    services.AddSingleton<IMonitorService, MonitorService>();
    services.AddSingleton<IResourceGroupService, ResourceGroupService>();
    services.AddSingleton<IAppConfigService, AppConfigService>();
    services.AddSingleton<IMigrationService, MigrationService>();
    services.AddSingleton<CommandFactory>();

    // Try to register commands
    try
    {
        // Look for extension method in all loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var extensionTypes = assembly.GetTypes()
                .Where(t => t.Name.Contains("ServiceCollection") && t.Name.Contains("Extension"))
                .ToList();

            foreach (var extensionType in extensionTypes)
            {
                var method = extensionType.GetMethod("AddCommands",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(IServiceCollection) },
                    null);

                if (method != null)
                {
                    method.Invoke(null, new object[] { services });
                    Console.WriteLine($"Successfully registered commands via {extensionType.FullName}");
                    return;
                }
            }
        }

        // If we get here, we didn't find the extension method
        Console.WriteLine("Warning: Could not find AddCommands extension method in any assembly");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Note: Could not register commands via extension method: {ex.Message}");
    }
}

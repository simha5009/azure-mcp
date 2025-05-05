using System.CommandLine;
using System.CommandLine.Parsing;
using AzureMcp.Arguments.Migration;
using AzureMcp.Models.Command;
using AzureMcp.Services.Azure;
using AzureMcp.Services.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace AzureMcp.Commands.Migration;

public sealed class MigrateProjectListCommand(ILogger<MigrateProjectListCommand> logger) : SubscriptionCommand<MigrateProjectListArguments>
{
    private readonly ILogger<MigrateProjectListCommand> _logger = logger;

    protected override string GetCommandName() => "list";

    protected override string GetCommandDescription() =>
        """
        List all migrate projects in a subscription. This command retrieves and displays all migrate projects 
        available in the specified subscription. You must specify a subscription ID. Results are returned as a Dictionary
        with project names as keys and project id as values.
        """;

    [McpServerTool(Destructive = false, ReadOnly = true)]
    public override async Task<CommandResponse> ExecuteAsync(CommandContext context, ParseResult parseResult)
    {
        var args = BindArguments(parseResult);

        try
        {
            if (!await ProcessArguments(context, args))
            {
                return context.Response;
            }
            var _migrationService = context.GetService<IMigrationService>();
            var projects = await _migrationService.ListMigrationProjects(args.Subscription!);

            context.Response.Results = projects?.Count > 0 ? new { projects } : null;
        }
        catch (Exception ex)
        {
            HandleException(context.Response, ex);
        }

        return context.Response;
    }
}

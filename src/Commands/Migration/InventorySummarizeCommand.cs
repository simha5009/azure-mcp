using System.CommandLine;
using System.CommandLine.Parsing;
using Azure.ResourceManager.Resources.Models;
using AzureMcp.Arguments.Migration;
using AzureMcp.Models.Argument;
using AzureMcp.Models.Command;
using AzureMcp.Services.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace AzureMcp.Commands.Migration.Inventory;

public sealed class InventorySummarizeCommand(ILogger<InventorySummarizeCommand> logger) : SubscriptionCommand<InventorySummarizeArguments>
{
    private readonly ILogger<InventorySummarizeCommand> _logger = logger;

    private readonly Option<string> _projectNameOption = new Option<string>("--project-name", "The name of the migrate project.") { IsRequired = true };

    protected override string GetCommandName() => "summarize";

    protected override string GetCommandDescription() =>
        $"""
        Summarize inventory or discoverey details of a migration project. Requires {ArgumentDefinitions.Migration.ProjectName}.
        Returns a JsonDocument containing summary about the inventory/discovery of the project.
        """;

    protected override void RegisterOptions(Command command)
    {
        base.RegisterOptions(command);
        command.AddOption(_projectNameOption);
    }
    protected override void RegisterArguments()
    {
        base.RegisterArguments();
        AddArgument(CreateInventorySummarizationArgument());
    }

    protected override InventorySummarizeArguments BindArguments(ParseResult parseResult)
    {
        var args = base.BindArguments(parseResult);
        args.ProjectName = parseResult.GetValueForOption(_projectNameOption);
        return args;
    }

    private ArgumentBuilder<InventorySummarizeArguments> CreateInventorySummarizationArgument() =>
        ArgumentBuilder<InventorySummarizeArguments>
            .Create(ArgumentDefinitions.Migration.Project.Name, ArgumentDefinitions.Migration.Project.Description)
            .WithValueAccessor(args => args.ProjectName ?? string.Empty)
            .WithIsRequired(true);

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
            var summary = await _migrationService.SummarizeInventoryAsync(
                args.ProjectName!,
                args.Subscription!);

            context.Response.Results = new { Summary = summary };
        }
        catch (Exception ex)
        {
            HandleException(context.Response, ex);
        }

        return context.Response;
    }
}
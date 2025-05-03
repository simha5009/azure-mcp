using AzureMcp.Arguments.Migration;
using AzureMcp.Models.Argument;
using AzureMcp.Models.Command;
using AzureMcp.Services.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace AzureMcp.Commands.Migration;

public sealed class MigrateBusinessCaseSummarizeCommand(ILogger<MigrateBusinessCaseSummarizeCommand> logger) : SubscriptionCommand<MigrateBusinessCaseSummarizeArguments>
{
    private readonly ILogger<MigrateBusinessCaseSummarizeCommand> _logger = logger;
    private readonly Option<string> _projectNameOption = new Option<string>("--project-name", "The name of the migrate project.") { IsRequired = true };

    protected override string GetCommandName() => "summarize";

    protected override string GetCommandDescription() =>
        $"""
        Summarize business case of a migration project. Requires {ArgumentDefinitions.Migration.ProjectName}.
        Returns a JsonDocument containing summary about the businesscase of the project.
        Savings = totalOnPremisesCost - totalAzureCost
        Iaas Cost = futureAzureIaasCost
        Paas Cost = futureAzurePaaSCost
        Show Disvocery insights as well like total servers and their distribution by General servers which is represented by iaasOsDistribution 
        then Database Servers which is represented by sqlSupportStatusDistribution.
        then Servers running Web apps which is represented by paasDistribution
        """;

    protected override void RegisterOptions(Command command)
    {
        base.RegisterOptions(command);
        command.AddOption(_projectNameOption);
    }
    protected override void RegisterArguments()
    {
        base.RegisterArguments();
        AddArgument(CreateBusinessCaseSummarizationArgument());
    }

    protected override MigrateBusinessCaseSummarizeArguments BindArguments(ParseResult parseResult)
    {
        var args = base.BindArguments(parseResult);
        args.ProjectName = parseResult.GetValueForOption(_projectNameOption);
        return args;
    }

    private ArgumentBuilder<MigrateBusinessCaseSummarizeArguments> CreateBusinessCaseSummarizationArgument() =>
        ArgumentBuilder<MigrateBusinessCaseSummarizeArguments>
            .Create(ArgumentDefinitions.Migration.Project.Name, ArgumentDefinitions.Migration.Project.Description)
            .WithValueAccessor(args => args.ProjectName ?? string.Empty)
            .WithIsRequired(true);

    [McpServerTool(Destructive = false, ReadOnly = true)]
    public override async Task<CommandResponse> ExecuteAsync(CommandContext context, ParseResult parseResult)
    {
        var args = BindArguments(parseResult);

        try
        {
            var _migrationService = context.GetService<IMigrationService>();
            var result = await _migrationService.SummarizeBusinessCaseAsync(args.ProjectName!, args.Subscription!);
            context.Response.Results = result;
        }
        catch (Exception ex)
        {
            HandleException(context.Response, ex);
        }

        return context.Response;
    }
}
using AzureMcp.Arguments.Migration;
using AzureMcp.Models.Argument;
using AzureMcp.Models.Command;
using AzureMcp.Services.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace AzureMcp.Commands.Migration;

public sealed class ProjectDetailCommand(ILogger<ProjectDetailCommand> logger) : SubscriptionCommand<ProjectDetailArguments>
{
    private readonly ILogger<ProjectDetailCommand> _logger = logger;

    private readonly Option<string> _projectNameOption = new Option<string>("--project-name", "The name of the migrate project.") { IsRequired = true };

    protected override string GetCommandName() => "detail";

    protected override string GetCommandDescription() =>
        $"""
        Fetch projects details of a migration project. Requires {ArgumentDefinitions.Migration.ProjectName}.
        Returns a JsonDocument containing all the details about the project.
        """;

    protected override void RegisterOptions(Command command)
    {
        base.RegisterOptions(command);
        command.AddOption(_projectNameOption);
    }

    protected override void RegisterArguments()
    {
        base.RegisterArguments();
        AddArgument(CreateProjectDetailArgument());
    }

    protected override ProjectDetailArguments BindArguments(ParseResult parseResult)
    {
        var args = base.BindArguments(parseResult);
        args.ProjectName = parseResult.GetValueForOption(_projectNameOption);
        return args;
    }

    private ArgumentBuilder<ProjectDetailArguments> CreateProjectDetailArgument() =>
        ArgumentBuilder<ProjectDetailArguments>
            .Create(ArgumentDefinitions.Migration.Project.Name, ArgumentDefinitions.Migration.Project.Description)
            .WithValueAccessor(args => args.ProjectName ?? string.Empty)
            .WithIsRequired(true);

    [McpServerTool(Destructive = false, ReadOnly = true)]
    public override async Task<CommandResponse> ExecuteAsync(CommandContext context, ParseResult parseResult)
    {
        var args = BindArguments(parseResult);

        try
        {
            if(!await ProcessArguments(context, args))
            {
                return context.Response;
            }
            var migrationService = context.GetService<IMigrationService>();
            var projectDetails = await migrationService.GetProjectDetailsAsync(
                args.ProjectName!,
                args.Subscription!);

            context.Response.Results = projectDetails;
        }
        catch (Exception ex)
        {
            HandleException(context.Response, ex);
        }

        return context.Response;
    }
}
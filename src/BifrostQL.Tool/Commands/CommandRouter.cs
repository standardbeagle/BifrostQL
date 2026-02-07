namespace BifrostQL.Tool.Commands;

/// <summary>
/// Routes CLI arguments to the appropriate command handler.
/// </summary>
public sealed class CommandRouter
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ICommand> Commands => _commands;

    public CommandRouter Register(ICommand command)
    {
        _commands[command.Name] = command;
        return this;
    }

    /// <summary>
    /// Resolves and executes the command specified in the config.
    /// Returns 0 on success, 1 on failure, 2 on usage error.
    /// </summary>
    public async Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        if (string.IsNullOrWhiteSpace(config.CommandName))
        {
            WriteHelp(output);
            return 2;
        }

        if (string.Equals(config.CommandName, "help", StringComparison.OrdinalIgnoreCase))
        {
            WriteHelp(output);
            return 0;
        }

        if (!_commands.TryGetValue(config.CommandName, out var command))
        {
            output.WriteError($"Unknown command: {config.CommandName}");
            output.WriteInfo("");
            WriteHelp(output);
            return 2;
        }

        return await command.ExecuteAsync(config, output);
    }

    private void WriteHelp(OutputFormatter output)
    {
        if (output.IsJsonMode)
        {
            var commands = _commands.Values.Select(c => new { name = c.Name, description = c.Description }).ToArray();
            output.WriteJson(new { commands });
            return;
        }

        output.WriteHeader("BifrostQL CLI Tool");
        output.WriteInfo("");
        output.WriteInfo("Usage: bifrost <command> [options]");
        output.WriteInfo("");
        output.WriteHeader("Commands:");

        foreach (var cmd in _commands.Values.OrderBy(c => c.Name))
        {
            output.WriteInfo($"  {cmd.Name,-20} {cmd.Description}");
        }

        output.WriteInfo("");
        output.WriteHeader("Options:");
        output.WriteInfo("  --connection-string  Database connection string");
        output.WriteInfo("  --config             Path to bifrostql.json config file");
        output.WriteInfo("  --json               Output in JSON format for scripting");
        output.WriteInfo("  --port               Port for serve command (default: 5000)");
    }
}

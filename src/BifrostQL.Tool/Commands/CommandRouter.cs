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
    /// When the command name doesn't match a known command, defaults to serve
    /// and treats the unrecognized name as the first positional argument.
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

        if (_commands.TryGetValue(config.CommandName, out var command))
        {
            return await command.ExecuteAsync(config, output);
        }

        // Implicit serve: treat unrecognized command as positional arg
        if (_commands.TryGetValue("serve", out var serveCommand))
        {
            var implicitConfig = config.WithImplicitServe();
            return await serveCommand.ExecuteAsync(implicitConfig, output);
        }

        output.WriteError($"Unknown command: {config.CommandName}");
        output.WriteInfo("");
        WriteHelp(output);
        return 2;
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
        output.WriteInfo("       bifrost <server> <database> [options]");
        output.WriteInfo("       bifrost <config-file> [options]");
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
        output.WriteInfo("  --user               Database username (prompts for password securely)");
        output.WriteInfo("  --json               Output in JSON format for scripting");
        output.WriteInfo("  --port               Port for serve command (default: 5000)");
        output.WriteInfo("");
        output.WriteHeader("Examples:");
        output.WriteInfo("  bifrost myserver mydb                   Quick start with integrated auth");
        output.WriteInfo("  bifrost myserver mydb --user sa         Prompt for password securely");
        output.WriteInfo("  bifrost myserver mydb --port 3000       Custom port");
        output.WriteInfo("  bifrost bifrostql.json                  Use config file");
        output.WriteInfo("  bifrost serve                           Auto-discover bifrostql.json");
        output.WriteInfo("  bifrost serve --connection-string \"...\" Explicit connection string");
    }
}

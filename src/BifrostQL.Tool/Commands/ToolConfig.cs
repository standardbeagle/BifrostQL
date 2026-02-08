namespace BifrostQL.Tool.Commands;

/// <summary>
/// Parsed CLI arguments for the bifrost tool.
/// </summary>
public sealed class ToolConfig
{
    public string? ConnectionString { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? User { get; private set; }
    public bool JsonOutput { get; private set; }
    public string? CommandName { get; private set; }
    public string[] CommandArgs { get; private set; } = Array.Empty<string>();
    public int Port { get; private set; } = 5000;

    /// <summary>
    /// Parses command-line arguments into a ToolConfig instance.
    /// Arguments are positional for the command name, with named flags:
    ///   --connection-string &lt;connstr&gt;
    ///   --config &lt;path&gt;
    ///   --user &lt;username&gt;
    ///   --json
    ///   --port &lt;number&gt;
    /// </summary>
    public static ToolConfig Parse(string[] args)
    {
        var config = new ToolConfig();
        var remaining = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection-string" when i + 1 < args.Length:
                    config.ConnectionString = args[++i];
                    break;
                case "--config" when i + 1 < args.Length:
                    config.ConfigPath = args[++i];
                    break;
                case "--user" when i + 1 < args.Length:
                    config.User = args[++i];
                    break;
                case "--json":
                    config.JsonOutput = true;
                    break;
                case "--port" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var port))
                        config.Port = port;
                    break;
                default:
                    remaining.Add(args[i]);
                    break;
            }
        }

        if (remaining.Count > 0)
        {
            config.CommandName = remaining[0];
            config.CommandArgs = remaining.Skip(1).ToArray();
        }

        return config;
    }

    /// <summary>
    /// Creates a copy of this config for implicit serve routing.
    /// The unrecognized command name is prepended to CommandArgs.
    /// </summary>
    internal ToolConfig WithImplicitServe()
    {
        var args = new string[(CommandName != null ? 1 : 0) + CommandArgs.Length];
        if (CommandName != null)
            args[0] = CommandName;
        CommandArgs.CopyTo(args, CommandName != null ? 1 : 0);

        return new ToolConfig
        {
            ConnectionString = ConnectionString,
            ConfigPath = ConfigPath,
            User = User,
            JsonOutput = JsonOutput,
            CommandName = "serve",
            CommandArgs = args,
            Port = Port,
        };
    }
}

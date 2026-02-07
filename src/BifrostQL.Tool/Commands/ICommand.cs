namespace BifrostQL.Tool.Commands;

/// <summary>
/// Represents a CLI command that can be executed by the tool.
/// </summary>
public interface ICommand
{
    /// <summary>
    /// The command name used on the CLI (e.g., "init", "schema", "test").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short description shown in help output.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Executes the command with the given configuration and output formatter.
    /// Returns 0 on success, non-zero on failure.
    /// </summary>
    Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output);
}

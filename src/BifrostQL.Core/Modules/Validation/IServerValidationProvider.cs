using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Validation;

public sealed class ServerValidationContext
{
    public required IDbModel Model { get; init; }
    public required IDbTable Table { get; init; }
    public required MutationType MutationType { get; init; }
    public required Dictionary<string, object?> Data { get; init; }
    public required IDictionary<string, object?> UserContext { get; init; }
    public required string? ColumnName { get; init; }
    public IServiceProvider? Services { get; init; }
}

public interface IServerValidationProvider
{
    string Name { get; }

    IReadOnlyList<string> Validate(ServerValidationContext context);
}

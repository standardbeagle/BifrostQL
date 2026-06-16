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

    /// <summary>
    /// Synchronous validation. Return one message per failure, or an empty list when valid.
    /// </summary>
    IReadOnlyList<string> Validate(ServerValidationContext context);

    /// <summary>
    /// Asynchronous validation — use for uniqueness checks, FK-existence lookups, or
    /// service calls. The default bridges to <see cref="Validate"/> so existing sync
    /// providers keep working unchanged; async providers override this and may leave
    /// <see cref="Validate"/> returning an empty list (or delegating back).
    /// </summary>
    ValueTask<IReadOnlyList<string>> ValidateAsync(
        ServerValidationContext context,
        CancellationToken cancellationToken = default)
        => new(Validate(context));
}

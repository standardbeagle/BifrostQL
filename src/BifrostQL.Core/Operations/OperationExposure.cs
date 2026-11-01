using System.Text.RegularExpressions;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Operations;

/// <summary>An operation an endpoint exposes, with the policy its resolver enforces.</summary>
internal sealed record ExposedOperation(
    IBifrostOperation Operation, IReadOnlyCollection<string> Grants, bool TenantRequired);

/// <summary>
/// Reads the model-level <c>operations</c> and <c>operations-tenant</c> keys and matches them
/// against the registered operations. The one parser for both keys: model load
/// (<c>ModelConfigValidator</c>), schema emission and resolver wiring all call it, so the
/// validated config and the enforced config cannot differ.
/// </summary>
internal static partial class OperationExposure
{
    [GeneratedRegex("^[A-Za-z][_0-9A-Za-z]*$")]
    private static partial Regex OperationName();

    /// <summary>
    /// The listed operations, in config order, each with its grant list (empty = any caller
    /// the endpoint admits). Throws on a malformed bracket, an empty bracket (a fail-open
    /// typo), an invalid name, or a name listed twice.
    /// </summary>
    public static IReadOnlyList<(string Name, IReadOnlyCollection<string> Grants)> ParseExposed(IDbModel model)
    {
        var raw = model.GetMetadataValue(MetadataKeys.Operations.Exposed);
        var result = new List<(string, IReadOnlyCollection<string>)>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in BracketGrammar.SplitTopLevel(raw, ',', () => Malformed(raw)))
        {
            var (name, bracket) = BracketGrammar.SplitOptionalBracket(token.Trim(), () => Malformed(token));
            if (!OperationName().IsMatch(name))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Operations.Exposed}' names '{name}', which is not a valid operation name " +
                    "(a letter, then letters, digits or underscores; names starting with '_' are reserved).");
            if (!seen.Add(name))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Operations.Exposed}' lists '{name}' twice.");

            if (bracket is null)
            {
                result.Add((name, Array.Empty<string>()));
                continue;
            }

            var grants = PolicyConfigCollector.SplitList(bracket).ToArray();
            if (grants.Length == 0)
                throw Malformed(token);
            result.Add((name, grants));
        }
        return result;
    }

    /// <summary>
    /// Whether every operation refuses a caller with no tenant claim. Absent: required when any
    /// table declares <c>tenant-filter</c> (a multi-tenant model fails closed), optional otherwise.
    /// </summary>
    public static bool IsTenantRequired(IDbModel model)
    {
        var raw = model.GetMetadataValue(MetadataKeys.Operations.Tenant);
        if (string.IsNullOrWhiteSpace(raw))
            return model.Tables.Any(table =>
                !string.IsNullOrWhiteSpace(table.GetMetadataValue(MetadataKeys.Security.TenantFilter)));

        var value = raw.Trim();
        if (string.Equals(value, MetadataKeys.Operations.TenantRequired, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(value, MetadataKeys.Operations.TenantOptional, StringComparison.OrdinalIgnoreCase))
            return false;
        throw new InvalidOperationException(
            $"'{MetadataKeys.Operations.Tenant}' must be '{MetadataKeys.Operations.TenantRequired}' or " +
            $"'{MetadataKeys.Operations.TenantOptional}', not '{raw}'.");
    }

    /// <summary>
    /// The registered operations this model exposes, in config order. Throws when a listed
    /// name has no registration (the config promises a field that cannot exist), or when two
    /// registrations share a name.
    /// </summary>
    public static IReadOnlyList<ExposedOperation> Resolve(IDbModel model, IReadOnlyList<IBifrostOperation> registered)
    {
        var byName = new Dictionary<string, IBifrostOperation>(StringComparer.Ordinal);
        foreach (var operation in registered)
        {
            if (!byName.TryAdd(operation.Name, operation))
                throw new InvalidOperationException(
                    $"Two server operations are registered under the name '{operation.Name}'.");
        }

        var listed = ParseExposed(model);
        if (listed.Count == 0)
            return Array.Empty<ExposedOperation>();

        var tenantRequired = IsTenantRequired(model);
        return listed.Select(entry => byName.TryGetValue(entry.Name, out var operation)
                ? new ExposedOperation(operation, entry.Grants, tenantRequired)
                : throw new InvalidOperationException(
                    $"'{MetadataKeys.Operations.Exposed}' lists '{entry.Name}', but no server operation with " +
                    "that name is registered (AddOperation)."))
            .ToArray();
    }

    private static InvalidOperationException Malformed(string token) => new(
        $"Malformed entry '{token}' in '{MetadataKeys.Operations.Exposed}': each entry is an operation " +
        "name, optionally followed by one non-empty [grant,list] bracket.");
}

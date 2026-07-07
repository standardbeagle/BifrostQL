using System.Runtime.CompilerServices;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Parses a <see cref="TablePolicy"/> from table metadata. Follows the simple
/// per-table metadata-read pattern used by <c>TenantFilterTransformer</c> rather
/// than the multi-table collector pattern used for EAV — a table's policy is
/// fully described by its own metadata, so no cross-table resolution is needed.
/// </summary>
public static class PolicyConfigCollector
{
    // Table metadata is immutable once the model is built, and FromTable is called
    // per node per request from the policy filter/mutation transformers (and the
    // validator). Memoize the parsed, immutable TablePolicy per table instance so
    // the string parsing runs once. A parse failure (bad policy-actions token)
    // throws out of the factory and is not cached, so it re-throws next call —
    // behavior identical to the unmemoized path.
    private static readonly ConditionalWeakTable<IDbTable, TablePolicy> _cache = new();

    /// <summary>
    /// Builds the policy for a single table. Returns <see cref="TablePolicy.None"/>
    /// when the table carries no policy metadata (the documented opt-in default).
    /// </summary>
    public static TablePolicy FromTable(IDbTable table)
    {
        if (table is null)
            throw new ArgumentNullException(nameof(table));

        return _cache.GetValue(table, BuildPolicy);
    }

    private static TablePolicy BuildPolicy(IDbTable table)
    {
        var actionsRaw = table.GetMetadataValue(MetadataKeys.Policy.Actions);
        var readDenyRaw = table.GetMetadataValue(MetadataKeys.Policy.ReadDeny);
        var readDenyRolesRaw = table.GetMetadataValue(MetadataKeys.Policy.ReadDenyRoles);
        var writeDenyRaw = table.GetMetadataValue(MetadataKeys.Policy.WriteDeny);
        var rowScopeRaw = table.GetMetadataValue(MetadataKeys.Policy.RowScope);
        var rowScopeRolesRaw = table.GetMetadataValue(MetadataKeys.Policy.RowScopeRoles);

        var hasAny =
            !string.IsNullOrWhiteSpace(actionsRaw) ||
            !string.IsNullOrWhiteSpace(readDenyRaw) ||
            !string.IsNullOrWhiteSpace(writeDenyRaw) ||
            !string.IsNullOrWhiteSpace(rowScopeRaw);

        if (!hasAny)
            return TablePolicy.None;

        return new TablePolicy(
            allowedActions: ParseActions(actionsRaw),
            readDenyColumns: SplitList(readDenyRaw),
            writeDenyColumns: SplitList(writeDenyRaw),
            rowScopeExpression: rowScopeRaw,
            rowScopeRoles: SplitList(rowScopeRolesRaw),
            readDenyRoles: SplitList(readDenyRolesRaw));
    }

    private static IEnumerable<PolicyAction> ParseActions(string? raw)
    {
        foreach (var token in SplitList(raw))
        {
            if (Enum.TryParse<PolicyAction>(token, ignoreCase: true, out var action))
            {
                yield return action;
                continue;
            }

            // Fail fast on an unrecognized action token. Silently dropping it is a
            // fail-OPEN hazard: if `policy-actions` is the only policy metadata on a
            // table and every token is a typo, the resulting empty allow-list makes
            // TablePolicy.HasPolicy false, which the evaluator treats as "no policy
            // = unrestricted" — the intended lockdown silently becomes allow-all.
            throw new InvalidOperationException(
                $"Unknown policy action '{token}' in 'policy-actions'. " +
                $"Valid actions: {string.Join(", ", Enum.GetNames<PolicyAction>())}.");
        }
    }

    private static IEnumerable<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

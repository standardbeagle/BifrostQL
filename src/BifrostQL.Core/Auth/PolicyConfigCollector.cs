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
    /// <summary>
    /// Builds the policy for a single table. Returns <see cref="TablePolicy.None"/>
    /// when the table carries no policy metadata (the documented opt-in default).
    /// </summary>
    public static TablePolicy FromTable(IDbTable table)
    {
        if (table is null)
            throw new ArgumentNullException(nameof(table));

        var actionsRaw = table.GetMetadataValue(MetadataKeys.Policy.Actions);
        var readDenyRaw = table.GetMetadataValue(MetadataKeys.Policy.ReadDeny);
        var writeDenyRaw = table.GetMetadataValue(MetadataKeys.Policy.WriteDeny);
        var rowScopeRaw = table.GetMetadataValue(MetadataKeys.Policy.RowScope);

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
            rowScopeExpression: rowScopeRaw);
    }

    private static IEnumerable<PolicyAction> ParseActions(string? raw)
    {
        foreach (var token in SplitList(raw))
        {
            if (Enum.TryParse<PolicyAction>(token, ignoreCase: true, out var action))
                yield return action;
            // Unrecognized tokens are intentionally ignored — config typos
            // must not silently widen access.
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

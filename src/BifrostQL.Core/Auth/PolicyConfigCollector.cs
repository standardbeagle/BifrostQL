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
    // NOTE: do NOT memoize the parsed TablePolicy per table instance. Although the
    // table metadata is immutable, the returned TablePolicy (and its row-scope
    // filter) is consumed mutably downstream by the policy evaluator per caller, so
    // sharing one cached instance across requests leaks row-scope state between
    // callers — a security regression (out-of-scope rows become visible). Parse
    // fresh on every call.

    /// <summary>
    /// Builds the policy for a single table. Returns <see cref="TablePolicy.None"/>
    /// when the table carries no policy metadata (the documented opt-in default).
    /// </summary>
    public static TablePolicy FromTable(IDbTable table)
    {
        if (table is null)
            throw new ArgumentNullException(nameof(table));

        return BuildPolicy(table);
    }

    public static TablePolicy FromTable(IDbModel model, IDbTable table)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (table is null) throw new ArgumentNullException(nameof(table));
        return BuildPolicy(table, string.Equals(model.GetMetadataValue(MetadataKeys.Policy.Default), "deny", StringComparison.OrdinalIgnoreCase));
    }

    private static TablePolicy BuildPolicy(IDbTable table) => BuildPolicy(table, false);

    private static TablePolicy BuildPolicy(IDbTable table, bool denyByDefault)
    {
        var actionsRaw = table.GetMetadataValue(MetadataKeys.Policy.Actions);
        var readDenyRaw = table.GetMetadataValue(MetadataKeys.Policy.ReadDeny);
        var readDenyRolesRaw = table.GetMetadataValue(MetadataKeys.Policy.ReadDenyRoles);
        var writeDenyRaw = table.GetMetadataValue(MetadataKeys.Policy.WriteDeny);
        var writeDenyRolesRaw = table.GetMetadataValue(MetadataKeys.Policy.WriteDenyRoles);
        var rowScopeRaw = table.GetMetadataValue(MetadataKeys.Policy.RowScope);
        var rowScopeRolesRaw = table.GetMetadataValue(MetadataKeys.Policy.RowScopeRoles);
        var writeRequires = CollectWriteRequires(table);

        var hasAny =
            !string.IsNullOrWhiteSpace(actionsRaw) ||
            !string.IsNullOrWhiteSpace(readDenyRaw) ||
            !string.IsNullOrWhiteSpace(writeDenyRaw) ||
            writeRequires.Count > 0 ||
            !string.IsNullOrWhiteSpace(rowScopeRaw);

        if (!hasAny)
            return denyByDefault ? new TablePolicy(forceHasPolicy: true) : TablePolicy.None;

        return new TablePolicy(
            allowedActions: ParseActions(actionsRaw),
            readDenyColumns: SplitList(readDenyRaw),
            writeDenyColumns: SplitList(writeDenyRaw),
            rowScopeExpression: rowScopeRaw,
            rowScopeRoles: SplitList(rowScopeRolesRaw),
            readDenyRoles: SplitList(readDenyRolesRaw),
            writeDenyRoles: SplitList(writeDenyRolesRaw),
            writeRequires: writeRequires.Count > 0 ? writeRequires : null);
    }

    /// <summary>
    /// Collects the column-selector <c>write-requires</c> grants
    /// (<c>public.users.cost_rate { write-requires: team.manage }</c>) into a
    /// column → grants map keyed by the column's DB name. A column whose
    /// selector value parses to no grants is kept with an EMPTY set — fail
    /// closed: no caller can satisfy it.
    /// </summary>
    private static Dictionary<string, IEnumerable<string>> CollectWriteRequires(IDbTable table)
    {
        var result = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns)
        {
            var raw = column.GetMetadataValue(MetadataKeys.Policy.WriteRequires);
            if (raw is null)
                continue;
            result[column.DbName] = SplitList(raw).ToArray();
        }
        return result;
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

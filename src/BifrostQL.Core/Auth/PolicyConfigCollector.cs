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
    /// when the table carries no policy metadata and the model default is allow.
    /// </summary>
    public static TablePolicy FromTable(IDbTable table)
    {
        if (table is null)
            throw new ArgumentNullException(nameof(table));

        return BuildPolicy(table, string.Equals(
            table.GetMetadataValue(MetadataKeys.Policy.Default), "deny", StringComparison.OrdinalIgnoreCase));
    }

    private static TablePolicy BuildPolicy(IDbTable table, bool denyByDefault)
    {
        var actionsRaw = table.GetMetadataValue(MetadataKeys.Policy.Actions);
        var readDenyRaw = table.GetMetadataValue(MetadataKeys.Policy.ReadDeny);
        var readDenyRolesRaw = table.GetMetadataValue(MetadataKeys.Policy.ReadDenyRoles);
        var writeDenyRaw = table.GetMetadataValue(MetadataKeys.Policy.WriteDeny);
        var writeDenyRolesRaw = table.GetMetadataValue(MetadataKeys.Policy.WriteDenyRoles);
        var rowScopeRaw = table.GetMetadataValue(MetadataKeys.Policy.RowScope);
        var rowScopeRolesRaw = table.GetMetadataValue(MetadataKeys.Policy.RowScopeRoles);
        var rowScopeExemptRaw = table.GetMetadataValue(MetadataKeys.Policy.RowScopeExempt);
        var writeRequires = CollectWriteRequires(table);
        var readRequires = CollectReadRequires(table);
        var columnDenyModes = CollectColumnDenyModes(table);
        var tableDenyModeRaw = table.GetMetadataValue(MetadataKeys.Policy.DenyMode);

        var hasAny =
            !string.IsNullOrWhiteSpace(actionsRaw) ||
            !string.IsNullOrWhiteSpace(readDenyRaw) ||
            !string.IsNullOrWhiteSpace(writeDenyRaw) ||
            writeRequires.Count > 0 ||
            readRequires.Count > 0 ||
            !string.IsNullOrWhiteSpace(rowScopeRaw);

        if (!hasAny)
            return denyByDefault ? new TablePolicy(forceHasPolicy: true) : TablePolicy.None;

        return new TablePolicy(
            actionGrants: ParseActions(actionsRaw),
            readDenyColumns: SplitList(readDenyRaw),
            writeDenyColumns: SplitList(writeDenyRaw),
            rowScopeExpression: rowScopeRaw,
            rowScopeRoles: SplitList(rowScopeRolesRaw),
            rowScopeExemptGrants: SplitList(rowScopeExemptRaw),
            readDenyRoles: SplitList(readDenyRolesRaw),
            writeDenyRoles: SplitList(writeDenyRolesRaw),
            writeRequires: writeRequires.Count > 0 ? writeRequires : null,
            readRequires: readRequires.Count > 0 ? readRequires : null,
            columnDenyModes: columnDenyModes.Count > 0 ? columnDenyModes : null,
            tableDenyMode: string.IsNullOrWhiteSpace(tableDenyModeRaw)
                ? null
                : NormalizeDenyMode(tableDenyModeRaw));
    }

    /// <summary>
    /// Collects the column-selector <c>read-requires</c> grants
    /// (<c>public.members.cost_rate { read-requires: rates.view_cost }</c>) into a
    /// column → grants map keyed by the column's DB name. A column whose
    /// selector value parses to no grants is kept with an EMPTY set — fail
    /// closed: no caller can satisfy it.
    /// </summary>
    private static Dictionary<string, IEnumerable<string>> CollectReadRequires(IDbTable table)
    {
        var result = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns)
        {
            var raw = column.GetMetadataValue(MetadataKeys.Policy.ReadRequires);
            if (raw is null)
                continue;
            result[column.DbName] = SplitList(raw).ToArray();
        }
        return result;
    }

    /// <summary>
    /// Collects per-column <c>deny-mode</c> overrides (normalized lowercase).
    /// The closed grammar (null|refuse) is enforced at model load by
    /// <c>ModelConfigValidator</c>; an unrecognized value that somehow reaches
    /// the collector is treated as <c>refuse</c> — fail closed.
    /// </summary>
    private static Dictionary<string, string> CollectColumnDenyModes(IDbTable table)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns)
        {
            var raw = column.GetMetadataValue(MetadataKeys.Policy.DenyMode);
            if (raw is null)
                continue;
            result[column.DbName] = NormalizeDenyMode(raw);
        }
        return result;
    }

    /// <summary>
    /// Normalizes a <c>deny-mode</c> value. Shared by the collector and the
    /// load-time validator so both parse with the SAME rule. Throws on any
    /// value outside the closed grammar — a typo must fail load, never read as
    /// a mode.
    /// </summary>
    public static string NormalizeDenyMode(string raw)
    {
        var mode = raw.Trim().ToLowerInvariant();
        if (mode is "null" or "refuse")
            return mode;
        throw new InvalidOperationException(
            $"Unknown deny-mode '{raw}'. Valid modes: null, refuse.");
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

    private static Dictionary<PolicyAction, IEnumerable<string>> ParseActions(string? raw)
    {
        var result = new Dictionary<PolicyAction, IEnumerable<string>>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        // Bracket-aware top-level split: commas inside a grant bracket
        // (delete[projects.manage,invoices.manage]) do not split the token.
        foreach (var token in BracketGrammar.SplitTopLevel(raw, ',', () => MalformedBracket(raw)))
        {
            var (name, bracket) = BracketGrammar.SplitOptionalBracket(token, () => MalformedBracket(token));
            if (!Enum.TryParse<PolicyAction>(name, ignoreCase: true, out var action))
            {
                // Fail fast on an unrecognized action token. Silently dropping it is a
                // fail-OPEN hazard: if `policy-actions` is the only policy metadata on a
                // table and every token is a typo, the resulting empty allow-list makes
                // TablePolicy.HasPolicy false, which the evaluator treats as "no policy
                // = unrestricted" — the intended lockdown silently becomes allow-all.
                throw new InvalidOperationException(
                    $"Unknown policy action '{name}' in 'policy-actions'. " +
                    $"Valid actions: {string.Join(", ", Enum.GetNames<PolicyAction>())}.");
            }

            result[action] = bracket is null
                ? Array.Empty<string>()
                : SplitList(bracket).ToArray();
        }
        return result;
    }

    private static InvalidOperationException MalformedBracket(string token) => new(
        $"Malformed bracket in 'policy-actions' token '{token}'. " +
        $"Valid actions: {string.Join(", ", Enum.GetNames<PolicyAction>())}, " +
        "each optionally followed by one [grant,list] bracket.");

    private static IEnumerable<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

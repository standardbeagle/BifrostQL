namespace BifrostQL.Core.Auth;

/// <summary>
/// The four data-access actions an authorization policy can gate.
/// </summary>
public enum PolicyAction
{
    Read,
    Create,
    Update,
    Delete,
}

/// <summary>
/// The direction of a column access check: a column may be readable but not
/// writable (or vice versa).
/// </summary>
public enum PolicyDirection
{
    Read,
    Write,
}

/// <summary>
/// How a read of one column resolves for one caller.
/// </summary>
public enum ReadColumnDisposition
{
    /// <summary>The caller may read the value.</summary>
    Allow,

    /// <summary>
    /// The caller may NOT read the value; the selection succeeds with the
    /// column masked to null. Masked columns are still refused as filter,
    /// sort, and aggregate inputs (an ORDER BY oracle is still an oracle).
    /// </summary>
    Mask,

    /// <summary>The caller may NOT read the value; the query is rejected.</summary>
    Refuse,
}

/// <summary>
/// Result of a policy check. Carries the allow/deny verdict plus a
/// non-leaking human-readable reason when denied. The reason is deliberately
/// generic — it never names the table, column, or action involved, so it is
/// safe to surface to an unauthenticated caller.
/// </summary>
/// <param name="Allowed">True when the action is permitted.</param>
/// <param name="Reason">
/// A generic explanation when <paramref name="Allowed"/> is false; empty when allowed.
/// </param>
public sealed record PolicyDecision(bool Allowed, string Reason)
{
    /// <summary>A shared allow result.</summary>
    public static readonly PolicyDecision Allow = new(true, string.Empty);

    /// <summary>
    /// A shared deny result with a generic, non-leaking message. Used for every
    /// deny path so error output never reveals schema or data shape.
    /// </summary>
    public static readonly PolicyDecision Deny =
        new(false, "Access denied by authorization policy.");
}

/// <summary>
/// Pure-data authorization policy for a single table. Contains no behavior and
/// no dependency on Server or ASP.NET — it is produced by
/// <see cref="PolicyConfigCollector"/> from table metadata and consumed by the
/// stateless <see cref="PolicyEvaluator"/>.
///
/// Absent-policy default: a table with no policy metadata yields
/// <see cref="None"/>, whose <see cref="HasPolicy"/> is false. The evaluator
/// treats <see cref="None"/> as "no restriction" — the policy engine is opt-in,
/// matching how the tenant-filter and soft-delete modules behave when their
/// metadata is absent. Tables that need lockdown must declare a policy.
/// </summary>
public sealed record TablePolicy
{
    /// <summary>
    /// Sentinel for a table with no policy metadata. The evaluator allows all
    /// actions and columns for this value (documented opt-in default).
    /// </summary>
    public static readonly TablePolicy None = new();

    /// <summary>Actions explicitly permitted by this policy.</summary>
    public IReadOnlySet<PolicyAction> AllowedActions { get; }

    /// <summary>Columns that may not be read (case-insensitive match).</summary>
    public IReadOnlySet<string> ReadDenyColumns { get; }

    /// <summary>
    /// Optional set of role names the <see cref="ReadDenyColumns"/> list applies
    /// to (case-insensitive match). When empty, the read-deny columns are blocked
    /// for every non-admin caller. When non-empty, only a caller holding one of
    /// these roles is blocked from the read-deny columns — other non-admin
    /// callers may read them. This role-qualifies the column read deny so a
    /// finance field can be hidden from officer/member while remaining readable
    /// by finance_manager, mirroring <see cref="RowScopeRoles"/>.
    /// </summary>
    public IReadOnlySet<string> ReadDenyRoles { get; }

    /// <summary>Columns that may not be written (case-insensitive match).</summary>
    public IReadOnlySet<string> WriteDenyColumns { get; }

    /// <summary>
    /// Optional set of role names the <see cref="WriteDenyColumns"/> list applies
    /// to (case-insensitive match). When empty, the write-deny columns are blocked
    /// for every non-admin caller. When non-empty, only a caller holding one of
    /// these roles is blocked — other non-admin callers may write them. Mirrors
    /// <see cref="ReadDenyRoles"/>.
    /// </summary>
    public IReadOnlySet<string> WriteDenyRoles { get; }

    /// <summary>
    /// Per-column write grants (column name case-insensitive → grant names,
    /// case-insensitive). A column present here may be written only by a caller
    /// holding ANY of the listed grants; a caller holding none is denied.
    /// Collected from column-selector <c>write-requires</c> metadata. Never
    /// applies to DELETE — a delete carries no writable columns.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> WriteRequires { get; }

    /// <summary>
    /// Per-column read grants (column name case-insensitive → grant names,
    /// case-insensitive). A column present here may be read only by a caller
    /// holding ANY of the listed grants; a caller holding none is denied —
    /// MASKED to null by default, REFUSED when <c>deny-mode: refuse</c> applies.
    /// Collected from column-selector <c>read-requires</c> metadata.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> ReadRequires { get; }

    /// <summary>
    /// Per-column <c>deny-mode</c> overrides (column name case-insensitive →
    /// normalized "null" or "refuse"). Collected from column-selector
    /// <c>deny-mode</c> metadata; wins over <see cref="TableDenyMode"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ColumnDenyModes { get; }

    /// <summary>
    /// Table-level <c>deny-mode</c> default ("null" or "refuse"), or null when
    /// unset. Applies to every read-denied column without a
    /// <see cref="ColumnDenyModes"/> entry.
    /// </summary>
    public string? TableDenyMode { get; }

    /// <summary>
    /// Optional row-scope policy expression, stored verbatim. Compilation of this
    /// expression into a query filter is sub-task 2's responsibility; sub-task 1
    /// only parses and carries it.
    /// </summary>
    public string? RowScopeExpression { get; }

    /// <summary>
    /// Optional set of role names the <see cref="RowScopeExpression"/> applies to
    /// (case-insensitive match). When empty, the row scope applies to every
    /// non-admin caller. When non-empty, only a caller holding one of these roles
    /// is narrowed by the row-scope filter — other non-admin callers are left
    /// unscoped, so a tenant-scoped officer keeps full-table access while a
    /// member is constrained to their own rows.
    /// </summary>
    public IReadOnlySet<string> RowScopeRoles { get; }

    /// <summary>
    /// True when this policy carries any configured restriction. False only for
    /// <see cref="None"/>.
    /// </summary>
    public bool HasPolicy { get; }

    /// <summary>
    /// Creates a table policy. All collection arguments are normalized to
    /// non-null sets; column matching is case-insensitive.
    /// </summary>
    public TablePolicy(
        IEnumerable<PolicyAction>? allowedActions = null,
        IEnumerable<string>? readDenyColumns = null,
        IEnumerable<string>? writeDenyColumns = null,
        string? rowScopeExpression = null,
        IEnumerable<string>? rowScopeRoles = null,
        IEnumerable<string>? readDenyRoles = null,
        IEnumerable<string>? writeDenyRoles = null,
        IReadOnlyDictionary<string, IEnumerable<string>>? writeRequires = null,
        IReadOnlyDictionary<string, IEnumerable<string>>? readRequires = null,
        IReadOnlyDictionary<string, string>? columnDenyModes = null,
        string? tableDenyMode = null,
        bool forceHasPolicy = false)
    {
        AllowedActions = new HashSet<PolicyAction>(
            allowedActions ?? Enumerable.Empty<PolicyAction>());
        ReadDenyColumns = new HashSet<string>(
            readDenyColumns ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        ReadDenyRoles = new HashSet<string>(
            readDenyRoles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        WriteDenyColumns = new HashSet<string>(
            writeDenyColumns ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        WriteDenyRoles = new HashSet<string>(
            writeDenyRoles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        WriteRequires = NormalizeWriteRequires(writeRequires);
        ReadRequires = NormalizeWriteRequires(readRequires);
        ColumnDenyModes = NormalizeDenyModes(columnDenyModes);
        TableDenyMode = string.IsNullOrWhiteSpace(tableDenyMode)
            ? null
            : tableDenyMode.Trim().ToLowerInvariant();
        RowScopeExpression = string.IsNullOrWhiteSpace(rowScopeExpression)
            ? null
            : rowScopeExpression.Trim();
        RowScopeRoles = new HashSet<string>(
            rowScopeRoles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        HasPolicy =
            AllowedActions.Count > 0 ||
            ReadDenyColumns.Count > 0 ||
            WriteDenyColumns.Count > 0 ||
            WriteRequires.Count > 0 ||
            ReadRequires.Count > 0 ||
            RowScopeExpression is not null || forceHasPolicy;
    }

    private static IReadOnlyDictionary<string, string> NormalizeDenyModes(
        IReadOnlyDictionary<string, string>? columnDenyModes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (columnDenyModes is null)
            return result;
        foreach (var (column, mode) in columnDenyModes)
        {
            if (string.IsNullOrWhiteSpace(column) || string.IsNullOrWhiteSpace(mode))
                continue;
            result[column.Trim()] = mode.Trim().ToLowerInvariant();
        }
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> NormalizeWriteRequires(
        IReadOnlyDictionary<string, IEnumerable<string>>? writeRequires)
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        if (writeRequires is null)
            return result;
        foreach (var (column, grants) in writeRequires)
        {
            if (string.IsNullOrWhiteSpace(column))
                continue;
            // An empty grant list gates the column for EVERY non-admin caller
            // (fail closed): no grant can satisfy it.
            result[column.Trim()] = new HashSet<string>(
                grants ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }
}

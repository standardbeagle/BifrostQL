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

    /// <summary>Columns that may not be written (case-insensitive match).</summary>
    public IReadOnlySet<string> WriteDenyColumns { get; }

    /// <summary>
    /// Optional row-scope policy expression, stored verbatim. Compilation of this
    /// expression into a query filter is sub-task 2's responsibility; sub-task 1
    /// only parses and carries it.
    /// </summary>
    public string? RowScopeExpression { get; }

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
        string? rowScopeExpression = null)
    {
        AllowedActions = new HashSet<PolicyAction>(
            allowedActions ?? Enumerable.Empty<PolicyAction>());
        ReadDenyColumns = new HashSet<string>(
            readDenyColumns ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        WriteDenyColumns = new HashSet<string>(
            writeDenyColumns ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        RowScopeExpression = string.IsNullOrWhiteSpace(rowScopeExpression)
            ? null
            : rowScopeExpression.Trim();

        HasPolicy =
            AllowedActions.Count > 0 ||
            ReadDenyColumns.Count > 0 ||
            WriteDenyColumns.Count > 0 ||
            RowScopeExpression is not null;
    }
}

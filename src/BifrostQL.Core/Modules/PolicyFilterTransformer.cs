using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Query-path enforcement point for the server-side authorization policy engine
/// (sub-task 2/4). Reads a table's <see cref="TablePolicy"/> from metadata
/// (parsed by <see cref="PolicyConfigCollector"/>, sub-task 1) and applies it to
/// the read path:
///
///   - <b>Table read deny.</b> <see cref="GetAdditionalFilter"/> throws
///     <see cref="BifrostExecutionError"/> when the caller lacks the
///     <see cref="PolicyAction.Read"/> permission for the table.
///   - <b>Row scope.</b> When the policy carries a row-scope expression it is
///     compiled by <see cref="RowScopeCompiler"/> and returned as an additional
///     filter, so <see cref="FilterTransformersWrap"/> ANDs it alongside the
///     tenant filter rather than replacing it.
///   - <b>Column read deny.</b> Enforced by <see cref="AssertColumnsReadable"/>
///     (refuse half) and <see cref="MaskedColumns"/> (mask half). The mode is
///     per column: <c>deny-mode: refuse</c> rejects a query that references a
///     read-denied column with a clear, non-leaking error; <c>deny-mode:
///     null</c> answers the column in the mask set and the row materialiser
///     writes null for it. <c>read-requires</c>-gated columns mask by default;
///     <c>policy-read-deny</c> columns refuse by default (shipped behaviour).
///     Masked columns are still refused as filter/sort/aggregate inputs by
///     <see cref="QueryTransformerService"/> — masking is for selection only.
///     The deny may be role-qualified via <c>policy-read-deny-roles</c>: when
///     that metadata is present the deny applies only to callers holding one
///     of the named roles, so a finance field stays readable by
///     finance_manager while being hidden from officer/member.
///
/// Priority 1 — within the 0-99 security range, immediately after
/// <see cref="TenantFilterTransformer"/> at priority 0, matching
/// <see cref="AutoFilterTransformer"/>.
///
/// Identity is reconstructed from the per-request user context: the user id from
/// <c>user_id</c>, roles from <c>roles</c>, and permissions from <c>permissions</c> — the canonical claims
/// <c>IdentityContextMapper</c> writes. Admin-role bypass and the absent-policy
/// ALLOW default are delegated to the stateless <see cref="PolicyEvaluator"/>.
/// </summary>
public sealed class PolicyFilterTransformer : IFilterTransformer, IColumnReadGuard, IModuleNamed
{
    private const string TableReadDeniedMessage =
        "Access denied by authorization policy.";

    private const string ColumnReadDeniedMessage =
        "The query references a field that is not permitted by authorization policy.";

    private readonly PolicyEvaluator _evaluator;

    /// <summary>
    /// Creates a transformer. <paramref name="adminRole"/> is forwarded to the
    /// <see cref="PolicyEvaluator"/>; when null the evaluator's default admin
    /// role applies.
    /// </summary>
    public PolicyFilterTransformer(string? adminRole = null)
    {
        _evaluator = new PolicyEvaluator(adminRole);
    }

    public string ModuleName => "policy";

    // Security range, immediately after the tenant filter at priority 0.
    public int Priority => 1;

    public bool AppliesTo(IDbTable table, QueryTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        return PolicyConfigCollector.FromTable(table).HasPolicy;
    }

    /// <summary>
    /// Enforces table-level read permission and returns the compiled row-scope
    /// filter, or null when the policy carries no row-scope expression.
    /// </summary>
    public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var policy = PolicyConfigCollector.FromTable(table);
        var identity = BuildIdentity(context);

        if (!_evaluator.CanAct(policy, PolicyAction.Read, identity).Allowed)
            throw new BifrostExecutionError(TableReadDeniedMessage)
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };

        // No row-scope expression: nothing to AND onto the query.
        if (policy.RowScopeExpression is null)
            return null;

        // Admin bypass is consistent across the whole policy — admins are not
        // narrowed by the row-scope filter either.
        if (IsAdmin(identity))
            return null;

        // Grant-scoped row scope: when the policy names the grants it applies to,
        // a caller holding none of them is left unscoped (still tenant-filtered).
        if (!RowScopeApplies(policy, identity))
            return null;

        return RowScopeCompiler.Compile(policy.RowScopeExpression, table, context.UserContext);
    }

    /// <summary>
    /// Column-read-deny enforcement seam, refuse half. Throws
    /// <see cref="BifrostExecutionError"/> when <paramref name="requestedColumns"/>
    /// includes any column whose read disposition for this caller is
    /// <see cref="ReadColumnDisposition.Refuse"/>. Columns whose disposition is
    /// <see cref="ReadColumnDisposition.Mask"/> do NOT throw here — they are
    /// answered by <see cref="MaskedColumns"/> and nulled by the row
    /// materialiser; <see cref="QueryTransformerService"/> still rejects them
    /// in filter/sort/aggregate positions. The error message is generic and
    /// never names the column or table.
    /// </summary>
    public void AssertColumnsReadable(
        IDbTable table,
        IEnumerable<string> requestedColumns,
        QueryTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (requestedColumns is null) throw new ArgumentNullException(nameof(requestedColumns));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var policy = PolicyConfigCollector.FromTable(table);
        var identity = BuildIdentity(context);

        foreach (var column in requestedColumns)
        {
            if (string.IsNullOrWhiteSpace(column))
                continue;

            if (_evaluator.GetReadDisposition(policy, column, identity) == ReadColumnDisposition.Refuse)
                throw new BifrostExecutionError(ColumnReadDeniedMessage)
                { ErrorCode = BifrostExecutionError.AccessDeniedCode };
        }
    }

    /// <summary>
    /// Masking half of column-read-deny: the subset of
    /// <paramref name="requestedColumns"/> whose read disposition for this
    /// caller is <see cref="ReadColumnDisposition.Mask"/> — a column gated by
    /// <c>read-requires</c> the caller does not hold (default), or a
    /// <c>policy-read-deny</c> column carrying <c>deny-mode: null</c>. The
    /// caller selects the column and receives null; filter/sort/aggregate use
    /// stays refused. Returned names are DB column names.
    /// </summary>
    public IReadOnlySet<string> MaskedColumns(
        IDbTable table,
        IEnumerable<string> requestedColumns,
        QueryTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (requestedColumns is null) throw new ArgumentNullException(nameof(requestedColumns));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var policy = PolicyConfigCollector.FromTable(table);
        if (!policy.HasPolicy)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identity = BuildIdentity(context);

        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in requestedColumns)
        {
            if (string.IsNullOrWhiteSpace(column))
                continue;

            if (_evaluator.GetReadDisposition(policy, column, identity) == ReadColumnDisposition.Mask)
                masked.Add(column);
        }
        return masked;
    }

    // Identity projection is shared with every other policy-gated surface via
    // PolicyIdentity so the same user id, roles, and permissions are resolved everywhere; a local
    // reimplementation could drift into a weaker (fail-open) check.
    private static AppIdentity BuildIdentity(QueryTransformContext context)
        => PolicyIdentity.FromUserContext(context.UserContext);

    // A policy that has restrictions (HasPolicy is true) but permits no action.
    // Only the evaluator's admin bypass can pass a Read check against it, so it
    // is a reliable probe for "is this identity an admin".
    private static readonly TablePolicy AdminProbePolicy =
        new(rowScopeExpression: "probe");

    private bool IsAdmin(AppIdentity identity)
    {
        // The evaluator's admin bypass is internal; a denying policy that the
        // identity still passes is the observable signal of an admin.
        return _evaluator.CanAct(AdminProbePolicy, PolicyAction.Read, identity).Allowed;
    }

    // True when the policy's row-scope expression should narrow this caller: an
    // unqualified policy (no RowScopeRoles) applies to every non-admin caller;
    // a role-qualified policy applies only to a caller holding one of its roles.
    private static bool RowScopeApplies(TablePolicy policy, AppIdentity identity)
    {
        if (policy.RowScopeRoles.Count == 0)
            return true;

        return identity.Grants.Any(policy.RowScopeRoles.Contains);
    }
}

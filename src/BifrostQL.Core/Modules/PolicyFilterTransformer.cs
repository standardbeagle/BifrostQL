using System.Collections;
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
///   - <b>Column read deny.</b> Enforced by <see cref="AssertColumnsReadable"/>.
///     <i>Chosen mechanism:</i> reject — a query that references a read-denied
///     column fails with a clear, non-leaking error rather than silently
///     stripping the column. Rejecting is consistent with the table-deny path
///     above (both fail closed) and avoids returning a partial result the caller
///     did not ask for. <see cref="IFilterTransformer"/> only sees the table, not
///     the selected columns, so this is a public seam the column-selection path
///     (sub-task 4) calls; the policy logic itself lives here.
///
/// Priority 1 — within the 0-99 security range, immediately after
/// <see cref="TenantFilterTransformer"/> at priority 0, matching
/// <see cref="AutoFilterTransformer"/>.
///
/// Identity is reconstructed from the per-request user context: the user id from
/// <c>user_id</c> and roles from <c>roles</c> — the canonical claims
/// <c>IdentityContextMapper</c> writes. Admin-role bypass and the absent-policy
/// ALLOW default are delegated to the stateless <see cref="PolicyEvaluator"/>.
/// </summary>
public sealed class PolicyFilterTransformer : IFilterTransformer, IColumnReadGuard, IModuleNamed
{
    private const string UserIdContextKey = MetadataKeys.Auth.DefaultUserIdContextKey;
    private const string RolesContextKey = MetadataKeys.Auth.DefaultRolesContextKey;

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
            throw new BifrostExecutionError(TableReadDeniedMessage);

        // No row-scope expression: nothing to AND onto the query.
        if (policy.RowScopeExpression is null)
            return null;

        // Admin bypass is consistent across the whole policy — admins are not
        // narrowed by the row-scope filter either.
        if (IsAdmin(identity))
            return null;

        // Role-scoped row scope: when the policy names the roles it applies to,
        // a caller holding none of them is left unscoped (still tenant-filtered).
        if (!RowScopeApplies(policy, identity))
            return null;

        return RowScopeCompiler.Compile(policy.RowScopeExpression, table.DbName, context.UserContext);
    }

    /// <summary>
    /// Column-read-deny enforcement seam. Throws <see cref="BifrostExecutionError"/>
    /// when <paramref name="requestedColumns"/> includes any column the caller may
    /// not read under <paramref name="table"/>'s policy. The error message is
    /// generic and never names the column or table.
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

            if (!_evaluator.IsColumnAllowed(policy, column, PolicyDirection.Read, identity).Allowed)
                throw new BifrostExecutionError(ColumnReadDeniedMessage);
        }
    }

    private static AppIdentity BuildIdentity(QueryTransformContext context)
    {
        var userContext = context.UserContext;

        var userId = userContext.TryGetValue(UserIdContextKey, out var idValue)
                     && idValue is not null
            ? idValue.ToString()
            : null;

        // A request with no resolved user still needs an identity for the
        // evaluator; use a stable anonymous id so policy checks run normally.
        if (string.IsNullOrWhiteSpace(userId))
            userId = "anonymous";

        var roles = ExtractRoles(userContext);

        return new AppIdentity(userId, "query-context", roles: roles);
    }

    private static IReadOnlyList<string> ExtractRoles(IDictionary<string, object?> userContext)
    {
        if (!userContext.TryGetValue(RolesContextKey, out var rolesValue) || rolesValue is null)
            return Array.Empty<string>();

        if (rolesValue is string singleRole)
            return new[] { singleRole };

        if (rolesValue is IEnumerable<string> typedRoles)
            return typedRoles.ToArray();

        if (rolesValue is IEnumerable sequence)
        {
            var result = new List<string>();
            foreach (var item in sequence)
            {
                var role = item?.ToString();
                if (!string.IsNullOrWhiteSpace(role))
                    result.Add(role);
            }
            return result;
        }

        return Array.Empty<string>();
    }

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

        return identity.Roles.Any(policy.RowScopeRoles.Contains);
    }
}

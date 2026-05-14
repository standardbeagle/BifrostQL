using System.Collections;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Mutation-path enforcement point for the server-side authorization policy
/// engine (sub-task 3/4). The query-path counterpart is
/// <see cref="PolicyFilterTransformer"/>; this transformer applies the same
/// <see cref="TablePolicy"/> (parsed by <see cref="PolicyConfigCollector"/>) to
/// create/update/delete:
///
///   - <b>Table action deny.</b> A create/update/delete on a table the caller
///     lacks the matching <see cref="PolicyAction"/> for is rejected via
///     <see cref="MutationTransformResult.Errors"/> — <see cref="MutationTransformersWrap"/>
///     aborts the mutation when any transformer returns errors.
///   - <b>Column write-deny.</b> A mutation whose data dictionary writes a
///     write-denied column is rejected the same way.
///   - <b>Row scope on update/delete.</b> When the policy carries a row-scope
///     expression it is compiled by <see cref="RowScopeCompiler"/> and returned
///     as <see cref="MutationTransformResult.AdditionalFilter"/>, so the wrap
///     ANDs it alongside the tenant filter rather than replacing it. Row scope
///     is not applied to create — an insert has no existing row to scope.
///
/// Priority 1 — within the 0-99 security range, matching
/// <see cref="PolicyFilterTransformer"/> and immediately after a tenant-scoped
/// mutation transformer at priority 0.
///
/// Every rejection message is generic: it never names the table, column, or
/// action, so error output cannot be used to probe the schema. Identity is
/// reconstructed from the per-request user context exactly as
/// <see cref="PolicyFilterTransformer"/> does — the user id from <c>user_id</c>
/// and roles from <c>roles</c>, the canonical claims
/// <see cref="IdentityContextMapper"/> writes. Admin-role bypass and the
/// absent-policy ALLOW default are delegated to the stateless
/// <see cref="PolicyEvaluator"/>.
/// </summary>
public sealed class PolicyMutationTransformer : IMutationTransformer, IModuleNamed
{
    private const string UserIdContextKey = MetadataKeys.Auth.DefaultUserIdContextKey;
    private const string RolesContextKey = MetadataKeys.Auth.DefaultRolesContextKey;

    private const string ActionDeniedMessage =
        "Access denied by authorization policy.";

    private const string ColumnWriteDeniedMessage =
        "The mutation writes a field that is not permitted by authorization policy.";

    private readonly PolicyEvaluator _evaluator;

    /// <summary>
    /// Creates a transformer. <paramref name="adminRole"/> is forwarded to the
    /// <see cref="PolicyEvaluator"/>; when null the evaluator's default admin
    /// role applies.
    /// </summary>
    public PolicyMutationTransformer(string? adminRole = null)
    {
        _evaluator = new PolicyEvaluator(adminRole);
    }

    public string ModuleName => "policy";

    // Security range, matching PolicyFilterTransformer on the query path.
    public int Priority => 1;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        return PolicyConfigCollector.FromTable(table).HasPolicy;
    }

    /// <summary>
    /// Enforces table-level action permission and column write-deny, and returns
    /// the compiled row-scope filter for update/delete. Returns errors (which
    /// abort the mutation) rather than throwing, matching the
    /// <see cref="IMutationTransformer"/> contract.
    /// </summary>
    public MutationTransformResult Transform(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var policy = PolicyConfigCollector.FromTable(table);
        var identity = BuildIdentity(context);

        // Table action deny — fail closed before inspecting the data.
        if (!_evaluator.CanAct(policy, ToPolicyAction(mutationType), identity).Allowed)
        {
            return new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = new[] { ActionDeniedMessage },
            };
        }

        // Column write-deny — any written column on the deny list aborts.
        foreach (var column in data.Keys)
        {
            if (string.IsNullOrWhiteSpace(column))
                continue;

            if (!_evaluator.IsColumnAllowed(policy, column, PolicyDirection.Write, identity).Allowed)
            {
                return new MutationTransformResult
                {
                    MutationType = mutationType,
                    Data = data,
                    Errors = new[] { ColumnWriteDeniedMessage },
                };
            }
        }

        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
            AdditionalFilter = BuildRowScopeFilter(policy, mutationType, table, identity, context),
        };
    }

    /// <summary>
    /// Compiles the policy's row-scope expression for update/delete, or returns
    /// null when there is no expression, the mutation is a create (no existing
    /// row to scope), or the caller is an admin (consistent with the query-path
    /// admin bypass — admins are not narrowed by the row-scope filter).
    /// </summary>
    private TableFilter? BuildRowScopeFilter(
        TablePolicy policy,
        MutationType mutationType,
        IDbTable table,
        AppIdentity identity,
        MutationTransformContext context)
    {
        if (policy.RowScopeExpression is null)
            return null;

        // A create has no existing row — row scope only narrows update/delete.
        if (mutationType == MutationType.Insert)
            return null;

        if (IsAdmin(identity))
            return null;

        return RowScopeCompiler.Compile(policy.RowScopeExpression, table.DbName, context.UserContext);
    }

    private static PolicyAction ToPolicyAction(MutationType mutationType) => mutationType switch
    {
        MutationType.Insert => PolicyAction.Create,
        MutationType.Update => PolicyAction.Update,
        MutationType.Delete => PolicyAction.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(mutationType), mutationType, null),
    };

    private static AppIdentity BuildIdentity(MutationTransformContext context)
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

        return new AppIdentity(userId, "mutation-context", roles: roles);
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
    // Only the evaluator's admin bypass can pass a check against it, so it is a
    // reliable probe for "is this identity an admin".
    private static readonly TablePolicy AdminProbePolicy =
        new(rowScopeExpression: "probe");

    private bool IsAdmin(AppIdentity identity)
    {
        // The evaluator's admin bypass is internal; a denying policy that the
        // identity still passes is the observable signal of an admin.
        return _evaluator.CanAct(AdminProbePolicy, PolicyAction.Create, identity).Allowed;
    }
}

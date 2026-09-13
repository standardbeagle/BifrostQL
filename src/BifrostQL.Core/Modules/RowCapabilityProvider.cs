using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;

namespace BifrostQL.Core.Modules;

/// <summary>Computes per-row update and delete capability for row-scoped tables.</summary>
public sealed class RowCapabilityProvider : IComputedColumnProvider
{
    public const string ProviderName = "row-capability";
    public const string FieldName = "_can";
    /// <summary>
    /// A named object type, declared once in <see cref="Schema.SchemaGenerator.GetGenericTableTypes"/>.
    /// An inline `{ ... }` literal here is not GraphQL SDL and broke every row-scoped table's schema.
    /// </summary>
    public const string FieldType = "RowCapabilities!";

    private readonly PolicyEvaluator _evaluator;

    public RowCapabilityProvider(string? adminRole = null) => _evaluator = new PolicyEvaluator(adminRole);

    public string Name => ProviderName;

    public ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        TablePolicy policy;
        try
        {
            policy = PolicyConfigCollector.FromTable(context.Table);
        }
        catch (InvalidOperationException)
        {
            // Unevaluable policy: fail closed on the answer, never throw from a row.
            return new ValueTask<object?>(new RowCapabilities(false, false));
        }
        if (policy.RowScopeExpression is null || !RowScopeCompiler.TryGetContextKey(policy.RowScopeExpression, out _))
            return new ValueTask<object?>(new RowCapabilities(false, false));

        var identity = PolicyIdentity.FromUserContext(context.UserContext);
        var update = CanActOnRow(context, policy, identity, PolicyAction.Update);
        var delete = CanActOnRow(context, policy, identity, PolicyAction.Delete);
        return new ValueTask<object?>(new RowCapabilities(update, delete));
    }

    private bool CanActOnRow(ComputedColumnContext context, TablePolicy policy, AppIdentity identity, PolicyAction action)
    {
        if (!_evaluator.CanAct(policy, action, identity).Allowed)
            return false;
        if (IsAdmin(identity) || identity.Grants.Any(policy.RowScopeExemptGrants.Contains))
            return true;
        if (policy.RowScopeRoles.Count > 0 && !identity.Grants.Any(policy.RowScopeRoles.Contains))
            return true;

        var equals = policy.RowScopeExpression!.IndexOf('=');
        var column = policy.RowScopeExpression[..equals].Trim();
        if (!context.Row.TryGetValue(column, out var rowValue) || rowValue is null)
            return false;
        if (!context.UserContext.TryGetValue(MetadataKeys.Auth.DefaultUserIdContextKey, out var userValue) || userValue is null)
            return false;
        return string.Equals(rowValue.ToString(), userValue.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAdmin(AppIdentity identity)
        => _evaluator.CanAct(new TablePolicy(rowScopeExpression: "probe"), PolicyAction.Read, identity).Allowed;
}

/// <summary>Per-row answer to `_can`: resolved by property name (`update`, `delete`).</summary>
public sealed record RowCapabilities(bool Update, bool Delete);

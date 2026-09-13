using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Computes per-row update and delete capability for tables carrying a row scope
/// (<c>policy-row-scope</c>) and/or a self deny (<c>policy-self-deny</c>). The answer
/// composes the same three rules the mutation path enforces: the table's action
/// allow-list, the row-scope match (or its admin / exempt-grant / role bypass), and
/// the self-deny refusal of the caller's own row, which no bypass lifts.
/// </summary>
public sealed class RowCapabilityProvider : IComputedColumnProvider
{
    public const string ProviderName = "row-capability";
    public const string FieldName = "_can";
    /// <summary>
    /// A named object type, declared once in <see cref="Schema.SchemaGenerator.GetGenericTableTypes"/>.
    /// An inline `{ ... }` literal here is not GraphQL SDL and broke every row-scoped table's schema.
    /// </summary>
    public const string FieldType = "RowCapabilities!";

    private static readonly RowCapabilities Denied = new(false, false);

    private readonly PolicyEvaluator _evaluator;

    public RowCapabilityProvider(string? adminRole = null) => _evaluator = new PolicyEvaluator(adminRole);

    public string Name => ProviderName;

    /// <summary>
    /// The row columns (DB names) the provider reads for <paramref name="policy"/>: the
    /// row-scope column and the self column, each when its rule is configured. Empty
    /// when the table has neither rule, in which case no <c>_can</c> field is emitted.
    /// </summary>
    internal static IReadOnlyList<string> DependencyColumns(TablePolicy policy)
    {
        var columns = new List<string>(2);
        if (RowScopeCompiler.TryParse(policy.RowScopeExpression, out var scope))
            columns.Add(scope.Column);
        if (policy.SelfDenyColumns.Count > 0 && !columns.Contains(policy.SelfColumn, StringComparer.OrdinalIgnoreCase))
            columns.Add(policy.SelfColumn);
        return columns;
    }

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
            return new ValueTask<object?>(Denied);
        }

        var scoped = RowScopeCompiler.TryParse(policy.RowScopeExpression, out var scope);
        var selfDeny = policy.SelfDenyColumns.Count > 0;
        if (!scoped && !selfDeny)
            return new ValueTask<object?>(Denied);

        var identity = PolicyIdentity.FromUserContext(context.UserContext);
        var update = _evaluator.CanAct(policy, PolicyAction.Update, identity).Allowed
            && !(selfDeny && SelfDenied(context, policy))
            && (!scoped || WithinScope(context, policy, scope, identity));
        var delete = _evaluator.CanAct(policy, PolicyAction.Delete, identity).Allowed
            && (!scoped || WithinScope(context, policy, scope, identity));
        return new ValueTask<object?>(new RowCapabilities(update, delete));
    }

    /// <summary>
    /// The self-deny rule narrows update only, and the admin bypass is deliberately not
    /// consulted (PolicyMutationTransformer). A missing user id fails closed.
    /// </summary>
    private static bool SelfDenied(ComputedColumnContext context, TablePolicy policy)
        => RowMatchesContext(context, policy.SelfColumn, MetadataKeys.Auth.DefaultUserIdContextKey) != false;

    private bool WithinScope(ComputedColumnContext context, TablePolicy policy, RowScopeTerm scope, AppIdentity identity)
    {
        if (_evaluator.IsAdmin(identity) || identity.Grants.Any(policy.RowScopeExemptGrants.Contains))
            return true;
        if (policy.RowScopeRoles.Count > 0 && !identity.Grants.Any(policy.RowScopeRoles.Contains))
            return true;
        return RowMatchesContext(context, scope.Column, scope.ContextKey) == true;
    }

    /// <summary>
    /// Compares the row's <paramref name="column"/> with the caller's <paramref name="contextKey"/>
    /// value, both coerced to the column's CLR type the way the transformers coerce the
    /// context value (<see cref="ContextValueCoercer"/>). Null when either side is missing
    /// or cannot be coerced — the caller decides which way that fails.
    /// </summary>
    private static bool? RowMatchesContext(ComputedColumnContext context, string column, string contextKey)
    {
        var rowValue = ReadRowValue(context, column);
        if (rowValue is null)
            return null;
        if (!context.UserContext.TryGetValue(contextKey, out var contextValue) || contextValue is null)
            return null;
        if (!context.Table.ColumnLookup.TryGetValue(column, out var columnDto))
            return null;

        try
        {
            var expected = ContextValueCoercer.Coerce(context.Table, column, contextValue);
            var actual = ContextValueCoercer.ConvertToClrType(rowValue, columnDto.DataType);
            return actual is string actualText && expected is string expectedText
                ? string.Equals(actualText, expectedText, StringComparison.OrdinalIgnoreCase)
                : Equals(actual, expected);
        }
        catch (Exception ex) when (ex is BifrostExecutionError or FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static object? ReadRowValue(ComputedColumnContext context, string column)
    {
        // The synthesized definition declares the column (DB name) as a dependency, so
        // the projected row is keyed by that name. Fall back to the GraphQL name.
        if (context.Row.TryGetValue(column, out var value) && value is not null)
            return value;

        if (context.Table.ColumnLookup.TryGetValue(column, out var byDb)
            && context.Row.TryGetValue(byDb.GraphQlName, out var graphQlValue))
            return graphQlValue;

        return null;
    }
}

/// <summary>Per-row answer to `_can`: resolved by property name (`update`, `delete`).</summary>
public sealed record RowCapabilities(bool Update, bool Delete);

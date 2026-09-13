using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Request-scoped table/column policy facade. This is not a row-scope check;
/// row-level decisions come from the data path or <c>_can</c>.
/// </summary>
public sealed class PolicyGate : IPolicyGate
{
    private readonly IDbModel _model;
    private readonly AppIdentity _identity;
    private readonly PolicyEvaluator _evaluator;

    /// <summary>
    /// The gate a caller gets when the host could not project an identity for them
    /// (anonymous, or a principal whose claims map to nothing). Every question is answered
    /// <see cref="PolicyDecision.Deny"/> and <see cref="Require"/> raises the same generic
    /// ACCESS_DENIED the data path raises, so an app endpoint refuses such a caller with the
    /// wire shape the GraphQL mount would give them — never a 500 out of DI.
    /// </summary>
    public static IPolicyGate Refused { get; } = new RefusedGate();

    public PolicyGate(IDbModel model, AppIdentity identity, PolicyEvaluator? evaluator = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _evaluator = evaluator ?? new PolicyEvaluator();
    }

    public PolicyDecision CanAct(string qualifiedTable, PolicyAction action)
        => Evaluate(qualifiedTable, (policy, table) => _evaluator.CanAct(policy, action, _identity));

    public PolicyDecision CanWriteColumn(string qualifiedTable, string column)
        => Evaluate(qualifiedTable, (policy, table) =>
            ResolveColumn(table, column) is { } resolved
                ? _evaluator.IsColumnAllowed(policy, resolved, PolicyDirection.Write, _identity)
                : PolicyDecision.Deny);

    public PolicyDecision CanReadColumn(string qualifiedTable, string column)
        => Evaluate(qualifiedTable, (policy, table) =>
            ResolveColumn(table, column) is { } resolved
            && _evaluator.GetReadDisposition(policy, resolved, _identity) == ReadColumnDisposition.Allow
                ? PolicyDecision.Allow
                : PolicyDecision.Deny);

    public void Require(string qualifiedTable, PolicyAction action)
    {
        if (!CanAct(qualifiedTable, action).Allowed)
            throw new BifrostExecutionError(PolicyDecision.Deny.Reason)
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };
    }

    private PolicyDecision Evaluate(string name, Func<TablePolicy, IDbTable, PolicyDecision> check)
    {
        if (string.IsNullOrWhiteSpace(name)) return PolicyDecision.Deny;
        var dot = name.IndexOf('.');
        if (dot <= 0 || dot == name.Length - 1 ||
            !_model.TryGetTableFromDbName(name[..dot], name[(dot + 1)..], out var table))
            return PolicyDecision.Deny;
        return check(PolicyConfigCollector.FromTable(table), table);
    }

    private sealed class RefusedGate : IPolicyGate
    {
        public PolicyDecision CanAct(string qualifiedTable, PolicyAction action) => PolicyDecision.Deny;
        public PolicyDecision CanWriteColumn(string qualifiedTable, string column) => PolicyDecision.Deny;
        public PolicyDecision CanReadColumn(string qualifiedTable, string column) => PolicyDecision.Deny;
        public void Require(string qualifiedTable, PolicyAction action)
            => throw new BifrostExecutionError(PolicyDecision.Deny.Reason)
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };
    }

    /// <summary>
    /// The column's db name, or null when the table has no such column. A name the model does
    /// not carry has no policy to allow it, so both column questions answer Deny for null
    /// rather than passing the raw name to the evaluator, where "unmentioned" reads as Allow.
    /// </summary>
    private static string? ResolveColumn(IDbTable table, string column)
    {
        if (string.IsNullOrWhiteSpace(column)) return null;
        return table.Columns.FirstOrDefault(c =>
            string.Equals(c.DbName, column, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.GraphQlName, column, StringComparison.OrdinalIgnoreCase))?.DbName;
    }
}

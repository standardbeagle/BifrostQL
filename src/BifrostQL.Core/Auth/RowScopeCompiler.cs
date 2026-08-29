using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Compiles a table's verbatim row-scope policy expression — carried on
/// <see cref="TablePolicy.RowScopeExpression"/> by sub-task 1 — into a
/// <see cref="TableFilter"/> that the query path ANDs alongside the tenant
/// filter rather than replacing it.
///
/// Expression grammar: a single <c>column = {context-key}</c> term, matching
/// the example sub-task 1 carries verbatim (<c>"tenant_id = {tenant_id}"</c>).
/// The <c>{context-key}</c> placeholder is resolved against the per-request
/// user context. Equality is the only supported operator; a broader expression
/// language is out of scope for this sub-task.
///
/// Fail-closed: any malformed expression, missing context key, or null context
/// value throws <see cref="BifrostExecutionError"/> with a generic, non-leaking
/// message — the error never names the table, column, or context key, so it
/// cannot be used to probe the schema.
/// </summary>
public static class RowScopeCompiler
{
    private const string MalformedMessage =
        "Row-scope authorization policy is misconfigured.";

    private const string MissingContextMessage =
        "Row-scope authorization context is required but was not provided.";

    /// <summary>
    /// Compiles <paramref name="expression"/> against <paramref name="userContext"/>
    /// into an equality <see cref="TableFilter"/> on <paramref name="tableName"/>.
    /// </summary>
    public static TableFilter Compile(
        string? expression,
        string tableName,
        IDictionary<string, object?> userContext)
    {
        if (userContext is null)
            throw new ArgumentNullException(nameof(userContext));
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name is required.", nameof(tableName));

        var (column, contextKey) = Parse(expression);

        if (!userContext.TryGetValue(contextKey, out var value))
            throw new BifrostExecutionError(MissingContextMessage);
        if (value is null)
            throw new BifrostExecutionError(MissingContextMessage);

        return TableFilterFactory.Equals(tableName, column, value);
    }

    /// <summary>
    /// Extracts the <c>{context-key}</c> placeholder of a row-scope expression without
    /// throwing. Used by config-time consumers that must know which user-context keys a
    /// security filter will resolve at request time (e.g. the wire-context merger, which
    /// must never let a wire-supplied value fill one of them). Malformed expressions yield
    /// <c>false</c> here — the request-time <see cref="Compile"/> path still fails closed on
    /// them, so a malformed expression cannot be exploited, only rejected later.
    /// </summary>
    internal static bool TryGetContextKey(string? expression, out string contextKey)
    {
        contextKey = string.Empty;
        var parsed = TryParse(expression);
        if (parsed is null)
            return false;
        contextKey = parsed.Value.ContextKey;
        return true;
    }

    /// <summary>
    /// Splits a <c>column = {context-key}</c> expression into its column name
    /// and context key. Throws <see cref="BifrostExecutionError"/> on any
    /// deviation from the grammar.
    /// </summary>
    private static (string Column, string ContextKey) Parse(string? expression)
        => TryParse(expression)
           ?? throw new BifrostExecutionError(MalformedMessage);

    private static (string Column, string ContextKey)? TryParse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var equalsIndex = expression.IndexOf('=');
        if (equalsIndex <= 0 || equalsIndex >= expression.Length - 1)
            return null;

        var column = expression[..equalsIndex].Trim();
        var rhs = expression[(equalsIndex + 1)..].Trim();

        // Reject a second operator character (e.g. "==").
        if (rhs.StartsWith('='))
            return null;

        if (column.Length == 0)
            return null;

        if (rhs.Length < 3 || !rhs.StartsWith('{') || !rhs.EndsWith('}'))
            return null;

        var contextKey = rhs[1..^1].Trim();
        if (contextKey.Length == 0)
            return null;

        return (column, contextKey);
    }
}

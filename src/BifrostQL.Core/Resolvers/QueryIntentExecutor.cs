using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using GraphQL;
using GraphQL.Types;

namespace BifrostQL.Core.Resolvers;

/// <summary>
/// A protocol-adapter read request expressed directly as a programmatic
/// <see cref="GqlObjectQuery"/> — no GraphQL text, no <see cref="SqlVisitor"/>.
/// Adapters (OData, gRPC, custom binary ops, …) build the query tree from their
/// own wire format and hand it to <see cref="IQueryIntentExecutor"/>.
/// </summary>
public sealed class QueryIntent
{
    /// <summary>
    /// The programmatic query tree to execute. Build it against the model
    /// returned by <see cref="IQueryIntentExecutor.GetModelAsync"/> for the same
    /// endpoint, so <see cref="GqlObjectQuery.DbTable"/> references the cached
    /// model the intent executes against.
    /// </summary>
    public required GqlObjectQuery Query { get; init; }

    /// <summary>
    /// Authenticated caller context (tenant id, user id, roles, …). Security
    /// filter transformers read this; an intent with a missing tenant context on
    /// a tenant-filtered table fails closed exactly like a GraphQL request.
    /// </summary>
    public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// The registered GraphQL endpoint path (e.g. <c>/graphql</c>) whose cached
    /// DbModel/schema/connection the intent executes against. Null selects the
    /// single registered endpoint; with multiple endpoints registered it is
    /// required, and an unknown path fails fast — never a silent fallback to a
    /// different database.
    /// </summary>
    public string? Endpoint { get; init; }
}

/// <summary>
/// Materialized result of a query intent: the root result set's rows (column
/// name → value, converted with the same <see cref="ReaderEnum.DbConvert"/>
/// coercions GraphQL responses get) plus the executed parameterized SQL text
/// for diagnostics/auditing.
/// </summary>
public sealed class QueryIntentResult
{
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    /// <summary>
    /// Unpaged total row count; populated only when the intent set
    /// <see cref="GqlObjectQuery.IncludeResult"/>.
    /// </summary>
    public int? TotalCount { get; init; }

    /// <summary>
    /// The executed SQL (parameter placeholders, never inlined values).
    /// </summary>
    public required string Sql { get; init; }
}

/// <summary>
/// Read-only execution entry point for protocol adapters. Implementations MUST
/// route execution through <see cref="ISqlExecutionManager.ExecuteIntentAsync"/>
/// so the security transformer pipeline (tenant isolation, soft-delete, policy
/// row scope, column read guards) is applied unconditionally — an adapter has no
/// API surface to skip it. Mutations are out of scope by design.
/// </summary>
public interface IQueryIntentExecutor
{
    /// <summary>
    /// Resolves the cached <see cref="IDbModel"/> for an endpoint so an adapter
    /// can build <see cref="GqlObjectQuery"/> trees against the same model
    /// instance the intent will execute with. Fails fast on unknown endpoints.
    /// </summary>
    Task<IDbModel> GetModelAsync(string? endpoint = null);

    /// <summary>
    /// Executes a read intent: resolves the endpoint's cached model/connection,
    /// applies all registered filter transformers and column read guards, then
    /// runs the parameterized SQL. Unknown endpoint or table throws; there is no
    /// fallback.
    /// </summary>
    Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IQueryIntentExecutor"/>: a thin composition over the
/// existing plumbing. Endpoint resolution reuses the same <see cref="PathCache{T}"/>
/// the HTTP middleware and binary transport key their schemas by; execution is
/// delegated to <see cref="SqlExecutionManager"/>, which owns transformer
/// application — this class deliberately has no code path that could run SQL
/// without it.
/// </summary>
public sealed class QueryIntentExecutor : IQueryIntentExecutor
{
    private readonly PathCache<Inputs> _endpoints;
    private readonly IQueryTransformerService _transformerService;
    private readonly IQueryObservers? _observers;

    public QueryIntentExecutor(
        PathCache<Inputs> endpoints,
        IQueryTransformerService transformerService,
        IQueryObservers? observers = null)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _transformerService = transformerService ?? throw new ArgumentNullException(nameof(transformerService));
        _observers = observers;
    }

    public async Task<IDbModel> GetModelAsync(string? endpoint = null)
    {
        var inputs = await IntentEndpointResolver.ResolveAsync(_endpoints, endpoint);
        return IntentEndpointResolver.GetRequired<IDbModel>(inputs, "model", endpoint);
    }

    public async Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
    {
        if (intent is null) throw new ArgumentNullException(nameof(intent));

        var inputs = await IntentEndpointResolver.ResolveAsync(_endpoints, intent.Endpoint);
        var model = IntentEndpointResolver.GetRequired<IDbModel>(inputs, "model", intent.Endpoint);
        var schema = IntentEndpointResolver.GetRequired<ISchema>(inputs, "dbSchema", intent.Endpoint);
        var connFactory = IntentEndpointResolver.GetRequired<IDbConnFactory>(inputs, "connFactory", intent.Endpoint);

        var query = intent.Query;
        if (query.DbTable is null)
            throw new BifrostExecutionError("Query intent has no table: GqlObjectQuery.DbTable must be set.");

        // Fail fast when the intent's table is not part of the resolved endpoint's
        // model (wrong endpoint, or a stale model after a schema reset). Throws
        // with the table name — no fallback.
        model.GetTableFromDbName(query.DbTable.DbName);

        var manager = new SqlExecutionManager(model, schema, _transformerService, _observers);
        return await manager.ExecuteIntentAsync(query, intent.UserContext, connFactory, cancellationToken);
    }

}

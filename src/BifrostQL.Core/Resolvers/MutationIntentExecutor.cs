using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using GraphQL;

namespace BifrostQL.Core.Resolvers;

/// <summary>The mutation verb a <see cref="MutationIntent"/> executes.</summary>
public enum MutationIntentAction
{
    Insert,
    Update,
    Delete,
}

/// <summary>
/// A protocol-adapter write request expressed as plain data — no GraphQL text.
/// Adapters (OData, gRPC, custom binary ops, …) build the intent from their own
/// wire format and hand it to <see cref="IMutationIntentExecutor"/>, which runs
/// the full mutation transformer chain (policy, state machine, enum mapping,
/// validation, soft delete, tenant isolation, audit, optimistic concurrency)
/// exactly as a GraphQL mutation would.
/// </summary>
public sealed class MutationIntent
{
    /// <summary>Database table name (e.g. <c>orders</c>); unknown tables fail fast.</summary>
    public required string Table { get; init; }

    public required MutationIntentAction Action { get; init; }

    /// <summary>
    /// Column values, keyed by GraphQL field name or database column name
    /// (case-insensitive). For an update this carries the SET columns (plus the
    /// primary key when <see cref="PrimaryKey"/> is not used); for a delete it
    /// carries the predicate columns. A table with a concurrency token requires
    /// the token's last-read value here on update, exactly like the GraphQL path.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Data { get; init; }

    /// <summary>
    /// Optional positional primary-key values in declared key-column order — the
    /// composite-key-safe equivalent of the GraphQL <c>_primaryKey</c> argument.
    /// When supplied it wins over any key columns present in <see cref="Data"/>.
    /// Invalid on insert (a new row has no addressed identity).
    /// </summary>
    public IReadOnlyList<object?>? PrimaryKey { get; init; }

    /// <summary>
    /// Authenticated caller context (tenant id, user id, roles, …). Security
    /// mutation transformers read this; a missing tenant context on a
    /// tenant-filtered table fails closed exactly like a GraphQL request.
    /// </summary>
    public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// The registered GraphQL endpoint path (e.g. <c>/graphql</c>) whose cached
    /// DbModel/connection the intent executes against. Null selects the single
    /// registered endpoint; with multiple endpoints registered it is required,
    /// and an unknown path fails fast.
    /// </summary>
    public string? Endpoint { get; init; }
}

/// <summary>
/// Result of a mutation intent, mirroring the GraphQL mutation fields' scalar
/// returns: an insert yields the generated identity, a single-key update yields
/// the key value, a composite-key update and a delete yield the affected row
/// count (0 when a tenant/policy scope made the write a no-op).
/// </summary>
public sealed class MutationIntentResult
{
    public object? Value { get; init; }
}

/// <summary>
/// A multi-row write request executed as ONE transaction: every action runs the
/// full mutation transformer chain and hook choreography, and a veto anywhere
/// rolls the whole batch back — no partial batch, ever. The plan chat connector's
/// confirmed writes and multi-row protocol adapters use this instead of looping
/// <see cref="MutationIntent"/>s, which would commit row-by-row.
/// </summary>
public sealed class MutationBatchIntent
{
    /// <summary>Database table name (e.g. <c>orders</c>); unknown tables fail fast.</summary>
    public required string Table { get; init; }

    /// <summary>The actions, executed in order inside one transaction.</summary>
    public required IReadOnlyList<MutationBatchAction> Actions { get; init; }

    /// <inheritdoc cref="MutationIntent.UserContext"/>
    public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();

    /// <inheritdoc cref="MutationIntent.Endpoint"/>
    public string? Endpoint { get; init; }
}

/// <summary>
/// One action of a <see cref="MutationBatchIntent"/>. <paramref name="Data"/>
/// carries column values keyed by GraphQL field name or database column name
/// (case-insensitive): the full row for an insert, primary key + SET columns for
/// an update, and the key/predicate columns for a delete.
/// </summary>
public sealed record MutationBatchAction(MutationIntentAction Action, IReadOnlyDictionary<string, object?> Data);

/// <summary>Result of a batch intent: the total affected row count, matching the GraphQL batch field.</summary>
public sealed class MutationBatchIntentResult
{
    public required int TotalAffected { get; init; }
}

/// <summary>
/// Write entry point for protocol adapters. Implementations MUST route execution
/// through <see cref="TableMutationPipeline"/> (single row) and
/// <see cref="BatchMutationPipeline"/> (batch) — the seams shared with the GraphQL
/// resolvers — so the mutation transformer chain, before-commit hooks, and
/// parameterized SQL apply unconditionally; an adapter has no API surface to
/// skip them.
/// </summary>
public interface IMutationIntentExecutor
{
    /// <summary>
    /// Executes a mutation intent against the endpoint's cached model and
    /// connection. Unknown endpoint or table throws; there is no fallback.
    /// </summary>
    Task<MutationIntentResult> ExecuteAsync(MutationIntent intent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes every action of the batch inside ONE transaction — a transformer
    /// veto or database failure on any action rolls back the entire batch. The
    /// per-table batch size cap (<c>batch-max-size</c>, default 100) applies.
    /// </summary>
    Task<MutationBatchIntentResult> ExecuteBatchAsync(MutationBatchIntent intent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IMutationIntentExecutor"/>: endpoint resolution reuses the
/// same <see cref="PathCache{T}"/> the HTTP middleware keys its schemas by
/// (shared with <see cref="QueryIntentExecutor"/>), argument shaping reuses
/// <see cref="MutationArgumentBinder"/> (shared with the GraphQL resolver), and
/// execution is delegated to <see cref="TableMutationPipeline"/>, which owns
/// transformer application — this class deliberately has no code path that could
/// run SQL without it.
/// </summary>
public sealed class MutationIntentExecutor : IMutationIntentExecutor
{
    private readonly PathCache<Inputs> _endpoints;
    private readonly IMutationTransformers _transformers;
    private readonly IServiceProvider? _services;

    public MutationIntentExecutor(
        PathCache<Inputs> endpoints,
        IMutationTransformers transformers,
        IServiceProvider? services = null)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _transformers = transformers ?? throw new ArgumentNullException(nameof(transformers));
        _services = services;
    }

    public async Task<MutationIntentResult> ExecuteAsync(MutationIntent intent, CancellationToken cancellationToken = default)
    {
        if (intent is null) throw new ArgumentNullException(nameof(intent));

        var inputs = await IntentEndpointResolver.ResolveAsync(_endpoints, intent.Endpoint);
        var model = IntentEndpointResolver.GetRequired<IDbModel>(inputs, "model", intent.Endpoint);
        var connFactory = IntentEndpointResolver.GetRequired<IDbConnFactory>(inputs, "connFactory", intent.Endpoint);

        // Fail fast on a table outside the resolved endpoint's model (wrong
        // endpoint, or a stale caller after a schema reset).
        var table = model.GetTableFromDbName(intent.Table);

        var ctx = new MutationPipelineContext
        {
            Model = model,
            ConnFactory = connFactory,
            // Filter by the intent's active profile so a protocol-adapter write applies the
            // same per-profile module set the GraphQL write path does. A transport that never
            // stamped a profile leaves the full set active (fail-closed for writes).
            Transformers = BifrostProfileRegistry.FilterBy(_transformers, intent.UserContext),
            UserContext = intent.UserContext,
            Services = _services,
            CancellationToken = cancellationToken,
        };

        var value = intent.Action switch
        {
            MutationIntentAction.Insert => await InsertAsync(table, intent, ctx),
            MutationIntentAction.Update => await TableMutationPipeline.UpdateAsync(
                table, MutationArgumentBinder.SplitProperties(table, intent.Data, intent.PrimaryKey), ctx),
            MutationIntentAction.Delete => await TableMutationPipeline.DeleteAsync(
                table, MergePrimaryKey(table, intent), ctx),
            _ => throw new BifrostExecutionError($"Unsupported mutation intent action '{intent.Action}'."),
        };
        return new MutationIntentResult { Value = value };
    }

    public async Task<MutationBatchIntentResult> ExecuteBatchAsync(
        MutationBatchIntent intent, CancellationToken cancellationToken = default)
    {
        if (intent is null) throw new ArgumentNullException(nameof(intent));

        var inputs = await IntentEndpointResolver.ResolveAsync(_endpoints, intent.Endpoint);
        var model = IntentEndpointResolver.GetRequired<IDbModel>(inputs, "model", intent.Endpoint);
        var connFactory = IntentEndpointResolver.GetRequired<IDbConnFactory>(inputs, "connFactory", intent.Endpoint);
        var table = model.GetTableFromDbName(intent.Table);

        var actions = intent.Actions.Select(action => new BatchMutationPipeline.BatchAction(
            action.Action switch
            {
                MutationIntentAction.Insert => MutationAction.Insert,
                MutationIntentAction.Update => MutationAction.Update,
                MutationIntentAction.Delete => MutationAction.Delete,
                _ => throw new BifrostExecutionError($"Unsupported mutation intent action '{action.Action}'."),
            },
            new Dictionary<string, object?>(action.Data, StringComparer.OrdinalIgnoreCase))).ToList();

        var ctx = new MutationPipelineContext
        {
            Model = model,
            ConnFactory = connFactory,
            // Same per-profile module filtering as the single-row intent path.
            Transformers = BifrostProfileRegistry.FilterBy(_transformers, intent.UserContext),
            UserContext = intent.UserContext,
            Services = _services,
            CancellationToken = cancellationToken,
        };

        var totalAffected = await BatchMutationPipeline.ExecuteBatchAsync(table, actions, ctx);
        return new MutationBatchIntentResult { TotalAffected = totalAffected };
    }

    private static Task<object?> InsertAsync(IDbTable table, MutationIntent intent, MutationPipelineContext ctx)
    {
        // An insert creates a new row; a positional PrimaryKey addresses an
        // existing one. Accepting-and-ignoring it could silently turn an intended
        // update into a duplicate insert, so fail fast instead.
        if (intent.PrimaryKey is { Count: > 0 })
            throw new BifrostExecutionError(
                $"MutationIntent.PrimaryKey is not valid for an insert into '{table.DbName}'; put key values in Data or use Update.");

        return TableMutationPipeline.InsertAsync(
            table, new Dictionary<string, object?>(intent.Data, StringComparer.OrdinalIgnoreCase), ctx);
    }

    /// <summary>
    /// Overlays the positional primary key onto the delete predicate data — the
    /// same merge the GraphQL delete performs for <c>_primaryKey</c>, arity-checked
    /// against the table's key columns (composite-key safe).
    /// </summary>
    private static Dictionary<string, object?> MergePrimaryKey(IDbTable table, MutationIntent intent)
    {
        var data = new Dictionary<string, object?>(intent.Data, StringComparer.OrdinalIgnoreCase);
        var pkKeyData = MutationArgumentBinder.ResolvePrimaryKey(table, intent.PrimaryKey);
        if (pkKeyData != null)
        {
            foreach (var kv in pkKeyData)
                data[kv.Key] = kv.Value;
        }
        return data;
    }
}

using System.Collections.ObjectModel;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Operations;

/// <summary>
/// What a server operation runs with: its arguments, the caller, and data access that always
/// acts as that caller. There is no way to read or write as anyone else, and no raw SQL: every
/// read goes through the same read seam protocol adapters use, and every write through the
/// pipeline generated mutations use.
/// </summary>
public sealed class BifrostOperationContext
{
    private readonly IBifrostFieldContext _field;
    private readonly IDictionary<string, object?> _userContext;
    private readonly IDbConnFactory _connFactory;
    private readonly ISqlExecutionManager _reads;
    private readonly IMutationTransformers _transformers;
    private readonly OperationWriteScope? _scope;

    /// <summary>Stand-in until row locking lands.</summary>
    public const string LockRowNotFoundCode = "LOCK_ROW_NOT_FOUND";

    /// <summary>Stand-in until row locking lands.</summary>
    public const string RowLockUnsupportedCode = "ROW_LOCK_UNSUPPORTED";

    /// <summary>Stand-in until the IS NOT NULL test lands.</summary>
    public static readonly object NotNull = new();

    internal BifrostOperationContext(
        IBifrostOperation operation,
        IBifrostFieldContext field,
        IDbModel model,
        IDbConnFactory connFactory,
        ISqlExecutionManager reads,
        IMutationTransformers transformers,
        OperationWriteScope? scope)
    {
        Operation = operation;
        _field = field;
        _userContext = field.UserContext;
        _connFactory = connFactory;
        _reads = reads;
        _transformers = transformers;
        _scope = scope;
        Model = model;
        UserContext = new ReadOnlyDictionary<string, object?>(field.UserContext);
        Identity = PolicyIdentity.FromUserContext(
            field.UserContext, TenantFilterTransformer.ResolveTenantContextKey(model));
    }

    /// <summary>The operation being executed.</summary>
    public IBifrostOperation Operation { get; }

    /// <summary>The endpoint's model (tables, columns, metadata) for this request's profile.</summary>
    public IDbModel Model { get; }

    /// <summary>The caller's user context, read-only.</summary>
    public IReadOnlyDictionary<string, object?> UserContext { get; }

    /// <summary>The caller as the policy engine sees them: id, tenant, roles and permissions.</summary>
    public AppIdentity Identity { get; }

    /// <summary>The request's services, for the operation's own dependencies.</summary>
    public IServiceProvider? Services => _field.RequestServices;

    /// <summary>The request's cancellation token.</summary>
    public CancellationToken CancellationToken => _field.CancellationToken;

    /// <summary>True when the request supplied the named argument.</summary>
    public bool HasArgument(string name) => _field.HasArgument(name);

    /// <summary>The named argument converted to <typeparamref name="T"/>.</summary>
    public T? GetArgument<T>(string name) => _field.GetArgument<T>(name);

    /// <summary>
    /// Reads rows of <paramref name="table"/> as the caller. <paramref name="where"/> is an AND of
    /// column equalities; <paramref name="columns"/> defaults to every column. Tenant isolation,
    /// policy row scope, soft delete, column read guards and field decryption apply. Rows are
    /// keyed by GraphQL field name. Inside a mutation operation the read runs in the operation's
    /// transaction and sees its uncommitted writes.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string table,
        IReadOnlyDictionary<string, object?>? where = null,
        IReadOnlyCollection<string>? columns = null,
        int? limit = null,
        IReadOnlyCollection<string>? sort = null)
    {
        var dbTable = ResolveTable(table);
        var query = new GqlObjectQuery
        {
            DbTable = dbTable,
            SchemaName = dbTable.TableSchema,
            TableName = dbTable.DbName,
            GraphQlName = dbTable.GraphQlName,
            Path = dbTable.GraphQlName,
            Limit = limit,
        };
        foreach (var column in columns is null ? dbTable.Columns : columns.Select(name => ResolveColumn(dbTable, name)))
            query.ScalarColumns.Add(new GqlObjectColumn(column.DbName, column.GraphQlName));

        foreach (var (name, value) in where ?? new Dictionary<string, object?>())
        {
            var equality = TableFilterFactory.Equals(dbTable, ResolveColumn(dbTable, name).DbName, value);
            query.Filter = query.Filter is null ? equality : TableFilter.CombineAnd(query.Filter, equality);
        }

        var result = _scope is null
            ? await _reads.ExecuteIntentAsync(query, _userContext, _connFactory, CancellationToken, KeyManager)
            : await ScopedReads().ExecuteIntentAsync(query, _userContext, _connFactory, _scope, CancellationToken, KeyManager);
        return result.Rows;
    }

    /// <summary>
    /// Inserts one row of <paramref name="table"/> as the caller and returns its key. Runs the
    /// full mutation pipeline: policy, state machine, validation, tenant pinning, audit stamping,
    /// encryption, and the before-commit and in-transaction hooks.
    /// </summary>
    public Task<object?> InsertAsync(string table, IReadOnlyDictionary<string, object?> values)
    {
        var dbTable = ResolveWritableTable(table);
        return TableMutationPipeline.InsertAsync(
            dbTable, new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase), PipelineContext());
    }

    /// <summary>
    /// Updates the row of <paramref name="table"/> whose key columns are in <paramref name="values"/>,
    /// as the caller, and returns the number of rows changed: 0 when the row does not exist or is
    /// outside the caller's tenant or row scope.
    /// </summary>
    public async Task<int> UpdateAsync(string table, IReadOnlyDictionary<string, object?> values)
    {
        var dbTable = ResolveWritableTable(table);
        var (_, affectedRows) = await TableMutationPipeline.UpdateWithAffectedRowsAsync(
            dbTable, MutationArgumentBinder.SplitProperties(dbTable, values, null), PipelineContext());
        return affectedRows;
    }

    /// <summary>
    /// Deletes the row of <paramref name="table"/> named by <paramref name="key"/>, as the caller
    /// (a soft delete where the table declares one), and returns the number of rows affected.
    /// </summary>
    public async Task<int> DeleteAsync(string table, IReadOnlyDictionary<string, object?> key)
    {
        var dbTable = ResolveWritableTable(table);
        var affected = await TableMutationPipeline.DeleteAsync(
            dbTable, new Dictionary<string, object?>(key, StringComparer.OrdinalIgnoreCase), PipelineContext());
        return Convert.ToInt32(affected);
    }

    /// <summary>Stand-in until row locking lands.</summary>
    public Task LockRowAsync(string table, IReadOnlyDictionary<string, object?> key)
        => throw new NotImplementedException();

    private BifrostQL.Core.Crypto.EnvelopeKeyManager? KeyManager =>
        Services?.GetService(typeof(BifrostQL.Core.Crypto.EnvelopeKeyManager)) as BifrostQL.Core.Crypto.EnvelopeKeyManager;

    private SqlExecutionManager ScopedReads() => _reads as SqlExecutionManager
        ?? throw new BifrostExecutionError(
            "A mutation operation's reads need the built-in SqlExecutionManager to join the operation's transaction.");

    private MutationPipelineContext PipelineContext()
    {
        if (Operation.Kind != BifrostOperationKind.Mutation)
            throw new BifrostExecutionError(
                $"Operation '{Operation.Name}' is a query operation; only a mutation operation may write.");
        return new MutationPipelineContext
        {
            Model = Model,
            ConnFactory = _connFactory,
            Transformers = _transformers,
            UserContext = _userContext,
            Services = Services,
            CancellationToken = CancellationToken,
            WriteScope = _scope,
        };
    }

    private IDbTable ResolveTable(string table)
        => Model.TryGetTableFromClientName(table, out var dbTable)
            ? dbTable
            : throw BifrostErrorSink.LookupMiss(
                "The operation names a table that is not part of the endpoint's model.",
                $"Operation '{Operation.Name}' table miss: '{table}'.",
                nameof(BifrostOperationContext));

    // An approval-gated write diverts: it commits a pending row and then fails. That cannot be
    // one step of an all-or-nothing operation, so it is refused before anything runs.
    private IDbTable ResolveWritableTable(string table)
    {
        var dbTable = ResolveTable(table);
        if (ApprovalConfig.FromTable(dbTable).RequiresApproval)
            throw new BifrostExecutionError(
                $"Operation '{Operation.Name}' cannot write '{dbTable.GraphQlName}': the table is approval-gated, " +
                "and a gated write is queued for approval rather than applied.");
        return dbTable;
    }

    private static ColumnDto ResolveColumn(IDbTable table, string name)
        => table.Columns.FirstOrDefault(column => string.Equals(column.GraphQlName, name, StringComparison.Ordinal))
           ?? table.Columns.FirstOrDefault(column => string.Equals(column.DbName, name, StringComparison.OrdinalIgnoreCase))
           ?? throw new BifrostExecutionError($"Table '{table.GraphQlName}' has no column '{name}'.");
}

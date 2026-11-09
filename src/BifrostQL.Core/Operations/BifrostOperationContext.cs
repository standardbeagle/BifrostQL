using System.Collections.ObjectModel;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Operations;

/// <summary>
/// What a server operation runs with: its arguments, the caller, and data access that acts as
/// that caller. There is no way to read or write as anyone else, and no raw SQL: every read goes
/// through the same read seam protocol adapters use, and every write through the pipeline
/// generated mutations use. The one step that does not act as the caller is
/// <see cref="LockRowAsync"/>: it takes a row lock for the operation's own concurrency control,
/// neither reads nor changes anything the caller can see beyond whether the row exists, and
/// grants no read or write.
/// </summary>
public sealed class BifrostOperationContext
{
    private readonly IBifrostFieldContext _field;
    private readonly IDictionary<string, object?> _userContext;
    private readonly IDbConnFactory _connFactory;
    private readonly ISqlExecutionManager _reads;
    private readonly IMutationTransformers _transformers;
    private readonly OperationWriteScope? _scope;

    private static readonly string[] SortDirectionSuffixes = { "_asc", "_desc" };

    /// <summary>The error code <see cref="LockRowAsync"/> raises when no row has the key.</summary>
    public const string LockRowNotFoundCode = "LOCK_ROW_NOT_FOUND";

    /// <summary>The error code <see cref="LockRowAsync"/> raises on a dialect with no row locks.</summary>
    public const string RowLockUnsupportedCode = "ROW_LOCK_UNSUPPORTED";

    /// <summary>
    /// A value in <see cref="QueryAsync"/>'s <c>where</c> that tests the column IS NOT NULL.
    /// A <c>null</c> value tests IS NULL.
    /// </summary>
    public static readonly object NotNull = new();

    /// <summary>
    /// A value in <see cref="QueryAsync"/>'s <c>where</c> that tests the column is less than
    /// <paramref name="value"/>, the same comparison a generated field's <c>_lt</c> runs.
    /// A <c>null</c> is refused with <see cref="BifrostExecutionError.InvalidValueCode"/>:
    /// nothing is less than null, so the read would silently find nothing.
    /// </summary>
    public static object LessThan(object value) => new LessThanTest(
        value ?? throw new BifrostExecutionError("LessThan needs a value to compare with; it was given null.")
        {
            ErrorCode = BifrostExecutionError.InvalidValueCode,
        });

    private sealed record LessThanTest(object Value);

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
    /// column tests: a value tests equality, <c>null</c> tests IS NULL, <see cref="NotNull"/>
    /// tests IS NOT NULL and <see cref="LessThan"/> tests the column is less than a value. <paramref name="columns"/> defaults to every column.
    /// <paramref name="sort"/> holds <c>column_asc</c> or <c>column_desc</c> tokens, most
    /// significant first, the same tokens a generated field's <c>sort</c> argument takes.
    /// <paramref name="limit"/> behaves as a generated field's <c>limit</c>: omitted reads the
    /// default window of 100 rows, -1 reads up to the endpoint's row ceiling, and a limit above
    /// the ceiling is capped at it. Tenant isolation, policy row scope, soft delete, column read
    /// guards (which also cover columns named in <paramref name="where"/> and
    /// <paramref name="sort"/>) and field decryption apply. Rows are keyed by GraphQL field name.
    /// Inside a mutation operation the read runs in the operation's transaction and sees its
    /// uncommitted writes.
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
            var columnName = ResolveColumn(dbTable, name).DbName;
            var test = value switch
            {
                LessThanTest lessThan => TableFilterFactory.LessThan(dbTable, columnName, lessThan.Value),
                _ when ReferenceEquals(value, NotNull) => TableFilterFactory.IsNotNull(dbTable, columnName),
                _ => TableFilterFactory.Equals(dbTable, columnName, value),
            };
            query.Filter = query.Filter is null ? test : TableFilter.CombineAnd(query.Filter, test);
        }

        foreach (var token in sort ?? Array.Empty<string>())
            query.Sort.Add(ResolveSortToken(dbTable, token));

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

    /// <summary>
    /// Locks the row of <paramref name="table"/> named by <paramref name="key"/> (its whole primary
    /// key) until the operation's transaction commits or rolls back, so concurrent operations that
    /// take the same lock run one after another. Take it before the read it protects.
    /// <para>Operation code is trusted with its keys, so the lock takes no caller scope: a caller
    /// with no read or write on the row can still hold it, in any tenant, and it grants neither.
    /// Raises <see cref="LockRowNotFoundCode"/> when no row has the key, which leaves the operation
    /// able to continue; <see cref="RowLockUnsupportedCode"/> on a dialect with no row locks, rather
    /// than running on without one; and
    /// <see cref="MutationArgumentBinder.PartialPrimaryKeyCode"/> when the key is anything but the
    /// table's whole primary key. A query operation has no transaction to hold a lock in, and is
    /// refused.</para>
    /// </summary>
    public async Task LockRowAsync(string table, IReadOnlyDictionary<string, object?> key)
    {
        if (_scope is null)
            throw new BifrostExecutionError(
                $"Operation '{Operation.Name}' is a query operation; only a mutation operation has a transaction to hold a row lock.");

        var dbTable = ResolveTable(table);
        var dialect = _connFactory.Dialect;
        if (dialect.UpdateLockClause.Length == 0 && dialect.UpdateLockTableHint.Length == 0)
            throw new BifrostExecutionError(
                $"Operation '{Operation.Name}' asked for a row lock, which this database has no form of.")
            { ErrorCode = RowLockUnsupportedCode };

        var keyData = ResolveWholePrimaryKey(dbTable, key);
        IReadOnlyDictionary<string, object?>? locked = null;
        await _scope.JoinAsync(async (connection, transaction) =>
            locked = await MutationCommandExecutor.LoadRowByKey(
                connection, transaction, dialect, dbTable, keyData.Keys, keyData, forUpdate: true, CancellationToken));

        // Thrown outside the join: no row is an answer, not a failed statement, so the operation
        // may catch it and carry on.
        if (locked is null)
            throw new BifrostExecutionError($"Table '{dbTable.GraphQlName}' has no row with that key to lock.")
            { ErrorCode = LockRowNotFoundCode };
    }

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

    private static string ResolveSortToken(IDbTable table, string token)
    {
        var suffix = SortDirectionSuffixes.FirstOrDefault(direction => token.EndsWith(direction, StringComparison.Ordinal))
            ?? throw new BifrostExecutionError($"Sort token '{token}' must end in _asc or _desc.");
        return ResolveColumn(table, token[..^suffix.Length]).GraphQlName + suffix;
    }

    // Only the whole primary key addresses exactly one row: a partial or non-key "key" would lock
    // some row the caller did not mean.
    private static Dictionary<string, object?> ResolveWholePrimaryKey(IDbTable table, IReadOnlyDictionary<string, object?> key)
    {
        var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in key)
            keyData[ResolveColumn(table, name).DbName] = value;

        var primaryKey = table.KeyColumns.Select(column => column.DbName).ToList();
        if (primaryKey.Count == 0 || key.Count != primaryKey.Count || !primaryKey.All(keyData.ContainsKey))
            throw new BifrostExecutionError(
                $"A row lock on '{table.GraphQlName}' needs exactly its primary key ({string.Join(", ", primaryKey)}).")
            { ErrorCode = MutationArgumentBinder.PartialPrimaryKeyCode };
        return keyData;
    }

    private static ColumnDto ResolveColumn(IDbTable table, string name)
        => table.Columns.FirstOrDefault(column => string.Equals(column.GraphQlName, name, StringComparison.Ordinal))
           ?? table.Columns.FirstOrDefault(column => string.Equals(column.DbName, name, StringComparison.OrdinalIgnoreCase))
           ?? throw new BifrostExecutionError($"Table '{table.GraphQlName}' has no column '{name}'.");
}

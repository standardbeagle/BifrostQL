using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Core.Storage;

/// <summary>
/// The read and write seam the GraphQL file mutations share — the resolver-side
/// counterpart of <see cref="FileObjectSeam"/>, which does the same job for the
/// programmatic (adapter) surface.
///
/// <para><b>Reads</b> go through <see cref="ISqlExecutionManager.ExecuteIntentAsync"/>,
/// so the filter transformers (tenant isolation, soft delete, row-scope policy) and
/// the column read guards decide row visibility. <b>Writes</b> go through
/// <see cref="TableMutationPipeline"/>, so the history-target guard, the mutation
/// transformer chain, the before-commit hooks (approval), the in-transaction hooks
/// (change history, CDC outbox, deferred delta), the transaction and the
/// cancellation token all apply — none of which the resolvers' previous
/// hand-rolled UPDATE on a fresh connection ran (finding H2).</para>
///
/// <para>Only the row's own primary key is ever supplied. This builds NO predicate
/// beyond it: the pipeline narrows the write from the caller's identity, so an
/// out-of-scope key matches zero rows structurally rather than because a resolver
/// remembered to filter (protocol-adapter-security invariant 7).</para>
/// </summary>
internal static class FilePointerAccess
{
    /// <summary>
    /// Reads the row's file pointer under the caller's identity.
    /// <c>RowVisible</c> false means the row does not exist or the read chain
    /// scoped it away — indistinguishable by design; <c>Pointer</c> null means the
    /// row holds no file.
    /// </summary>
    public static async Task<(bool RowVisible, string? Pointer)> ReadPointerAsync(
        BifrostContextAdapter bifrost,
        IDbTable table,
        ColumnDto column,
        IReadOnlyDictionary<string, object?> keyData,
        CancellationToken cancellationToken)
    {
        var query = new GqlObjectQuery
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
            Filter = KeyFilter(table, keyData),
            Limit = 1,
        };
        query.ScalarColumns.Add(new GqlObjectColumn(column.DbName, column.GraphQlName));

        var result = await bifrost.Executor.ExecuteIntentAsync(
            query, bifrost.UserContext, bifrost.ConnFactory, cancellationToken);

        if (result.Rows.Count == 0)
            return (false, null);

        var pointer = result.Rows[0].TryGetValue(column.GraphQlName, out var value) ? value?.ToString() : null;
        return (true, string.IsNullOrWhiteSpace(pointer) ? null : pointer);
    }

    /// <summary>
    /// Writes (or, with a null <paramref name="pointerJson"/>, clears) the row's
    /// file pointer through the mutation pipeline, and returns the REAL number of
    /// rows the UPDATE affected.
    ///
    /// <para>Callers must test that count, never the pipeline's scalar return: on a
    /// single-key table that scalar is the KEY, so a <c>== 0</c> test on it is inert
    /// for every nonzero key and misfires on key value 0 (invariant 8b).</para>
    /// </summary>
    public static async Task<int> WritePointerAsync(
        IBifrostFieldContext context,
        BifrostContextAdapter bifrost,
        IDbTable table,
        ColumnDto column,
        Dictionary<string, object?> keyData,
        string? pointerJson)
    {
        // Clearing or repointing a file column is an ordinary UPDATE of that column
        // — never a row Delete, which would destroy the row and every other column
        // on it (invariant 7c, re-derived for a column-valued object).
        var data = new Dictionary<string, object?>(keyData, StringComparer.OrdinalIgnoreCase)
        {
            [column.ColumnName] = pointerJson,
        };
        var standardData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [column.ColumnName] = pointerJson,
        };

        var ctx = new MutationPipelineContext
        {
            Model = bifrost.Model,
            ConnFactory = bifrost.ConnFactory,
            Transformers = MutationTransformers(context),
            UserContext = context.UserContext,
            Services = context.RequestServices,
            CancellationToken = context.CancellationToken,
        };

        var (_, affectedRows) = await TableMutationPipeline.UpdateWithAffectedRowsAsync(
            table, (data, keyData, standardData), ctx);
        return affectedRows;
    }

    /// <summary>
    /// The request's write-path transformer set, filtered by its active profile so a
    /// file write applies the same module set a normal update does. Absent
    /// registration is a misconfiguration, not a permissive default: a file write
    /// that ran with no chain would skip tenant isolation and policy entirely.
    /// </summary>
    private static IMutationTransformers MutationTransformers(IBifrostFieldContext context)
    {
        var registered = context.RequestServices?.GetService<IMutationTransformers>()
            ?? throw new BifrostExecutionError(
                "File mutations require the mutation transformer pipeline, which is not registered for this request.");

        return BifrostProfileRegistry.FilterBy(registered, context.UserContext);
    }

    /// <summary>
    /// The row's own identity and nothing else — one equality per key column, in
    /// declared key order, so a composite key addresses exactly one row.
    /// </summary>
    private static TableFilter KeyFilter(IDbTable table, IReadOnlyDictionary<string, object?> keyData)
    {
        var clauses = table.KeyColumns
            .Select(c => TableFilterFactory.Equals(table.DbName, c.ColumnName, keyData[c.ColumnName]))
            .ToList();

        return clauses.Count == 1
            ? clauses[0]
            : new TableFilter { FilterType = FilterType.And, And = clauses };
    }
}

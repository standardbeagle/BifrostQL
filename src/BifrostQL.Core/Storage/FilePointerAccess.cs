using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using System.Globalization;
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
        string? pointerJson,
        string? concurrencyToken = null)
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

        ApplyConcurrencyToken(bifrost.Model, table, concurrencyToken, data, standardData);

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
    /// Places the caller's optimistic-concurrency token into the write data under the
    /// table's token column, so <c>ConcurrencyMutationTransformer</c> guards the file
    /// write exactly as it guards a table update. A table mutation carries the token as
    /// the token column inside its fieldset; the file mutations have no fieldset, so
    /// they carry it as a named argument and this is where the two meet.
    ///
    /// <para>Two rejections, both deliberate. A token supplied for a table that declares
    /// none is refused rather than ignored: a client that believes it is guarding a write
    /// and is not is the exact lost update the token exists to prevent. A MISSING token on
    /// a token table is NOT rejected here — it falls through to the transformer, so every
    /// write path in the product produces one message for that condition.</para>
    /// </summary>
    private static void ApplyConcurrencyToken(
        IDbModel model,
        IDbTable table,
        string? concurrencyToken,
        Dictionary<string, object?> data,
        Dictionary<string, object?> standardData)
    {
        var tokenColumnName = table.GetMetadataValue(MetadataKeys.Concurrency.Token);
        if (string.IsNullOrWhiteSpace(tokenColumnName))
        {
            if (!string.IsNullOrWhiteSpace(concurrencyToken))
                throw new BifrostExecutionError(
                    $"Table '{table.TableSchema}.{table.DbName}' does not use a concurrency token, " +
                    "so 'concurrencyToken' must not be supplied.");
            return;
        }

        if (string.IsNullOrWhiteSpace(concurrencyToken))
            return;

        if (!table.ColumnLookup.TryGetValue(tokenColumnName, out var tokenColumn))
            throw new BifrostExecutionError(
                $"Table '{table.TableSchema}.{table.DbName}' declares a concurrency token column that does not exist.");

        var value = CoerceToken(model, tokenColumn, concurrencyToken);
        data[tokenColumn.ColumnName] = value;
        standardData[tokenColumn.ColumnName] = value;
    }

    /// <summary>
    /// Converts the token argument (a string on the wire, because one field serves
    /// numeric and datetime tokens alike) to the token column's own type. Without this
    /// the guard predicate would compare a text literal to a numeric column and match no
    /// row — a silent CONFLICT on every correct token. The token is invariant-culture
    /// wire data (dot-decimal, ISO-8601), so every parse here pins
    /// <see cref="CultureInfo.InvariantCulture"/>: culture-sensitive parsing would read
    /// "1.5" as 15 under a comma-decimal host culture and shift the guard off the row.
    /// An unparseable value is refused
    /// with an adapter-owned message; the parse exception itself never reaches the wire.
    /// </summary>
    private static object CoerceToken(IDbModel model, ColumnDto tokenColumn, string token)
    {
        var graphQlType = model.TypeMapper.GetGraphQlType(tokenColumn.EffectiveDataType);
        try
        {
            return graphQlType switch
            {
                "Int" or "Short" or "Byte" or "BigInt" => long.Parse(token, CultureInfo.InvariantCulture),
                "Decimal" => decimal.Parse(token, CultureInfo.InvariantCulture),
                "DateTime" or "DateTimeOffset" => DateTimeOffset.Parse(token, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                // An unsupported token type is the transformer's error to report, with the
                // one message every write path shares; pass the value through untouched.
                _ => token,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new BifrostExecutionError(
                $"The concurrency token supplied for '{tokenColumn.GraphQlName}' is not a valid " +
                $"{graphQlType} value.");
        }
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

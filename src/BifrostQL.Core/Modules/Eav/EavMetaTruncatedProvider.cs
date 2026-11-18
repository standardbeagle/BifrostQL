using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// Computed-column provider backing the read-only <c>_metaTruncated</c> field emitted beside
/// <c>_meta</c> on EAV parent tables. True when the parent has more attribute rows than the
/// row ceiling lets <c>_meta</c> return (<c>max-query-rows - 1</c>), so <c>_meta</c> holds only
/// the first ones in key order; false when <c>_meta</c> is complete.
/// </summary>
/// <remarks>
/// The flag is a sibling field rather than a reserved key inside <c>_meta</c>, so every
/// attribute key round-trips and no attribute row can collide with a marker. It counts the
/// parent's attribute rows (<c>GROUP BY fk HAVING COUNT(*) &gt; page</c>) under the same guards
/// and row-scope filter as <c>_meta</c>. The two fields are separate statements: selecting both
/// reads the meta table twice per page, and outside a transaction a concurrent attribute write
/// can land between them.
/// </remarks>
public sealed class EavMetaTruncatedProvider : IBatchComputedColumnProvider
{
    /// <summary>Provider name referenced by the synthesized computed column.</summary>
    public const string ProviderName = "eav-meta-truncated";

    /// <summary>GraphQL field name emitted on EAV parent tables.</summary>
    public const string FieldName = "_metaTruncated";

    /// <summary>GraphQL type of the emitted field.</summary>
    public const string FieldType = "Boolean";

    public string Name => ProviderName;

    public ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
        => EavMetaRead.ComputeOneAsync(this, context, cancellationToken);

    /// <summary>
    /// <c>_metaTruncated</c> for every row of the page in one read (chunked at
    /// <see cref="ParentKey.MaxKeysPerQuery"/> keys). A row without a key, or on a table with
    /// no parent configuration, gets null, matching <c>_meta</c>.
    /// </summary>
    public async ValueTask<IReadOnlyList<object?>> ComputeBatchAsync(ComputedColumnBatchContext context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var values = new object?[context.Rows.Count];
        var read = await EavMetaRead.PrepareAsync(context);
        if (read is null)
            return values;

        // The page _meta returns per parent; more matching rows than that means _meta was cut.
        var window = RowProbeWindow.For(context.Model, -1);
        var truncatedParents = new Dictionary<ParentKey, bool>();
        await using var conn = context.ConnFactory!.GetConnection();
        await conn.OpenAsync(cancellationToken);
        foreach (var chunk in read.KeyChunks())
        {
            await using var command = conn.CreateCommand();
            var whereClause = read.BindChunk(command, chunk);
            var pageParameter = $"{read.Dialect.ParameterPrefix}metaPage";
            var page = command.CreateParameter();
            page.ParameterName = pageParameter;
            page.Value = window.Page;
            command.Parameters.Add(page);
            command.CommandText =
                $"SELECT {read.FkCol} FROM {read.MetaTableRef} WHERE {whereClause} " +
                $"GROUP BY {read.FkCol} HAVING COUNT(*) > {pageParameter}";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                    truncatedParents[ParentKey.Of(reader.GetValue(0))] = true;
            }
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (read.RowKeys[i] is { } pk)
                values[i] = ParentKey.TryGetGroup(truncatedParents, pk, out _);
        }
        return values;
    }
}

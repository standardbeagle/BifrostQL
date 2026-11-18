using System.Text.Json;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// Computed-column provider backing the read-only <c>_meta</c> field
/// emitted on EAV (Entity-Attribute-Value) parent tables. For each parent row it
/// reads the row's primary-key value, queries the linked meta table for that
/// row's <c>(key, value)</c> attribute pairs, and returns them aggregated into a
/// JSON object string (e.g. <c>{"color":"red","size":"L"}</c>). Rows with no
/// attributes yield <c>"{}"</c>.
/// </summary>
/// <remarks>
/// EAV participation is entirely metadata-driven via <see cref="EavConfig"/>; the
/// provider never infers it from table names. As an
/// <see cref="IBatchComputedColumnProvider"/> it reads the attribute rows of a whole
/// result page in one query, not one per parent row.
/// </remarks>
public sealed class EavMetaProvider : IBatchComputedColumnProvider
{
    /// <summary>Provider name referenced by the synthesized computed column.</summary>
    public const string ProviderName = "eav-meta";

    /// <summary>GraphQL field name emitted on EAV parent tables.</summary>
    public const string FieldName = "_meta";

    /// <summary>
    /// GraphQL type of the emitted field — the registered JSON scalar
    /// (<see cref="BifrostQL.Core.Schema.JsonScalarGraphType"/>). ComputeAsync returns a
    /// raw JSON object string; the scalar's Serialize parses it into a real object in the
    /// response, so clients get a structured object rather than an escaped string.
    /// </summary>
    public const string FieldType = "JSON";

    public string Name => ProviderName;

    public ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
        => EavMetaRead.ComputeOneAsync(this, context, cancellationToken);

    /// <summary>
    /// <c>_meta</c> for every row of the page in one read: the attribute rows of all the
    /// page's parents come back from <c>WHERE fk IN (...)</c> (chunked at
    /// <see cref="ParentKey.MaxKeysPerQuery"/> keys), windowed per parent to the row ceiling
    /// (<see cref="RowProbeWindow"/>), and grouped by parent key. A parent cut by the window
    /// holds only its first <c>max-query-rows - 1</c> attributes; the sibling
    /// <c>_metaTruncated</c> field (<see cref="EavMetaTruncatedProvider"/>) reports the cut.
    /// <c>_meta</c> itself reserves no key. A row without a key, or on a table with no parent
    /// configuration, gets null; a keyed row with no attribute rows gets <c>"{}"</c>.
    /// </summary>
    public async ValueTask<IReadOnlyList<object?>> ComputeBatchAsync(ComputedColumnBatchContext context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var values = new object?[context.Rows.Count];
        var read = await EavMetaRead.PrepareAsync(context);
        if (read is null)
            return values;

        var config = read.Config;
        var dialect = read.Dialect;
        var keyCol = dialect.EscapeIdentifier(config.KeyColumn);
        var selectList = $"{read.FkCol},{keyCol},{dialect.EscapeIdentifier(config.ValueColumn)}";

        // Each parent's attributes are windowed IN THE QUERY (ROW_NUMBER per parent key) to
        // the row ceiling, plus one sentinel row that tells a complete set from a cut one.
        // _meta takes no limit argument, so it reads "every row" the way a flat collection
        // does, clamped by max-query-rows. The order (attribute key, then the meta table's
        // primary key) makes the kept window deterministic.
        var window = RowProbeWindow.For(context.Model, -1);
        var windowOrder = new[] { keyCol }
            .Concat(read.MetaTable.KeyColumns.Select(c => dialect.EscapeIdentifier(c.DbName)))
            .ToArray();

        // Keyed by the window's row number: a windowed result's outer row order is unspecified
        // (MySQL returns it scrambled), so the kept attributes are put back in window order here.
        var attributesByParent = new Dictionary<ParentKey, SortedList<long, KeyValuePair<string, object?>>>();
        await using var conn = context.ConnFactory!.GetConnection();
        await conn.OpenAsync(cancellationToken);
        foreach (var chunk in read.KeyChunks())
        {
            await using var command = conn.CreateCommand();
            var whereClause = read.BindChunk(command, chunk);
            command.CommandText = dialect.ConnectedPaging(
                selectList,
                $"FROM {read.MetaTableRef} WHERE {whereClause}",
                new[] { read.FkCol },
                windowOrder,
                PagedKeys.RowNumber,
                PagedKeys.Total,
                offset: 0,
                limit: window.Fetch);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rowNumberOrdinal = reader.GetOrdinal(PagedKeys.RowNumber);
            while (await reader.ReadAsync(cancellationToken))
            {
                // The sentinel row past the window only marks the cut, which _metaTruncated reports.
                if (reader.IsDBNull(0))
                    continue;
                var rowNumber = Convert.ToInt64(reader.GetValue(rowNumberOrdinal));
                if (rowNumber > window.Page)
                    continue;
                // Both cells are raw meta-table column values, so both go through the
                // read chain's crypto projection before they reach the _meta JSON.
                var rawKey = reader.IsDBNull(1) ? null : reader.GetValue(1);
                var key = read.ReadChain.ProjectValue(config.KeyColumn, rawKey)?.ToString();
                if (string.IsNullOrEmpty(key))
                    continue;
                var rawValue = reader.IsDBNull(2) ? null : reader.GetValue(2);
                var parentKey = ParentKey.Of(reader.GetValue(0));
                if (!attributesByParent.TryGetValue(parentKey, out var attributes))
                    attributesByParent[parentKey] = attributes = new SortedList<long, KeyValuePair<string, object?>>();
                attributes.Add(rowNumber, new KeyValuePair<string, object?>(
                    key, read.ReadChain.ProjectValue(config.ValueColumn, rawValue)));
            }
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (read.RowKeys[i] is not { } pk)
                continue;
            values[i] = SerializeAttributes(
                ParentKey.TryGetGroup(attributesByParent, pk, out var attributes)
                    ? attributes.Values
                    : Array.Empty<KeyValuePair<string, object?>>());
        }
        return values;
    }

    private static string SerializeAttributes(IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in attributes)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case ulong ul:
                // Native ulong overload — Convert.ToInt64 would overflow above long.MaxValue.
                writer.WriteNumberValue(ul);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value));
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case float or double:
                // NaN / Infinity are not representable in JSON numbers — emit as string.
                var d = Convert.ToDouble(value);
                if (double.IsFinite(d))
                    writer.WriteNumberValue(d);
                else
                    writer.WriteStringValue(value!.ToString());
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}

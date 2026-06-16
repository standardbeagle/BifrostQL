using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Modules.ComputedColumns;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// Per-row computed-column provider backing the read-only <c>_meta</c> field
/// emitted on EAV (Entity-Attribute-Value) parent tables. For each parent row it
/// reads the row's primary-key value, queries the linked meta table for that
/// row's <c>(key, value)</c> attribute pairs, and returns them aggregated into a
/// JSON object string (e.g. <c>{"color":"red","size":"L"}</c>). Rows with no
/// attributes yield <c>"{}"</c>.
/// </summary>
/// <remarks>
/// EAV participation is entirely metadata-driven via <see cref="EavConfig"/>; the
/// provider never infers it from table names. This slice issues one auxiliary
/// query per parent row (N+1). A batched per-result-set fetch is a documented
/// follow-up — see the call site in <c>SqlExecutionManager</c>.
/// </remarks>
public sealed class EavMetaProvider : IComputedColumnProvider
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

    public async ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        // Metadata-driven: locate the EAV config whose parent table is the table
        // currently being projected. No name-prefix detection.
        var config = context.Model.EavConfigs.FirstOrDefault(e =>
            string.Equals(e.ParentTableDbName, context.Table.DbName, StringComparison.OrdinalIgnoreCase));
        if (config is null)
            return null;

        // Composite-PK limitation: the meta table references the parent via a
        // single foreign-key column, so a parent must have exactly one PK column
        // for the _meta lookup to be unambiguous. Tables with composite keys are
        // not supported by this provider (documented limitation).
        var keyColumns = context.Table.KeyColumns.ToArray();
        if (keyColumns.Length != 1)
            return null;

        var pkValue = ReadKeyValue(context, keyColumns[0]);
        if (context.ConnFactory is null || pkValue is null)
            return null;

        var dialect = context.ConnFactory.Dialect;
        var metaTableRef = dialect.TableReference(null, config.MetaTableDbName);
        var keyCol = dialect.EscapeIdentifier(config.KeyColumn);
        var valueCol = dialect.EscapeIdentifier(config.ValueColumn);
        var fkCol = dialect.EscapeIdentifier(config.ForeignKeyColumn);
        var paramName = $"{dialect.ParameterPrefix}pk";

        var sql = $"SELECT {keyCol},{valueCol} FROM {metaTableRef} WHERE {fkCol}={paramName}";

        await using var conn = context.ConnFactory.GetConnection();
        await conn.OpenAsync(cancellationToken);
        await using var command = conn.CreateCommand();
        command.CommandText = sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = paramName;
        parameter.Value = pkValue;
        command.Parameters.Add(parameter);

        var attributes = new List<KeyValuePair<string, object?>>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString();
                if (string.IsNullOrEmpty(key))
                    continue;
                var value = reader.IsDBNull(1) ? null : reader.GetValue(1);
                attributes.Add(new KeyValuePair<string, object?>(key, value));
            }
        }

        return SerializeAttributes(attributes);
    }

    private static object? ReadKeyValue(ComputedColumnContext context, ColumnDto keyColumn)
    {
        // The synthesized definition declares the PK (DB name) as its only
        // dependency, so the projected row is keyed by that name. Fall back to
        // the GraphQL name defensively (mirrors StateMachineTransitionsProvider).
        if (context.Row.TryGetValue(keyColumn.DbName, out var byDb) && byDb is not null)
            return byDb;

        if (context.Row.TryGetValue(keyColumn.GraphQlName, out var byGraphQl) && byGraphQl is not null)
            return byGraphQl;

        return null;
    }

    private static string SerializeAttributes(IReadOnlyList<KeyValuePair<string, object?>> attributes)
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

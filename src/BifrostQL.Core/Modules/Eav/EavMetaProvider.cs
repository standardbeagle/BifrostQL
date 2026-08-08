using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;

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
        // currently being projected. Match on schema too — a same-named parent in a
        // different schema (app.settings vs dbo.settings) must not bind here. The
        // parent shares the meta table's schema (see EavConfigCollector), which the
        // config carries as TableSchema. No name-prefix detection.
        var config = context.Model.EavConfigs.FirstOrDefault(e =>
            string.Equals(e.ParentTableDbName, context.Table.DbName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.TableSchema, context.Table.TableSchema, StringComparison.OrdinalIgnoreCase));
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
        var metaTableRef = dialect.TableReference(config.TableSchema, config.MetaTableDbName);
        var keyCol = dialect.EscapeIdentifier(config.KeyColumn);
        var valueCol = dialect.EscapeIdentifier(config.ValueColumn);
        var fkCol = dialect.EscapeIdentifier(config.ForeignKeyColumn);
        var paramName = $"{dialect.ParameterPrefix}pk";

        // The FULL read chain for the meta table, not just its row filter. `_meta`
        // previously applied only IFilterTransformers.GetCombinedFilter, so:
        //   - the key/value columns never met IColumnReadGuard — a policy-denied
        //     value column was serialized into _meta in full, while the same
        //     caller's ordinary query of the meta table is rejected;
        //   - the FK column never met IColumnFilterGuard though it is the WHERE
        //     predicate of this read;
        //   - there was no CryptoReadProjector, so an envelope-encrypted value
        //     column went out as RAW CIPHERTEXT inside the _meta JSON.
        // TableReadChain is the single seam that applies all of it; guard decisions
        // stay with the guards (protocol-adapter-security invariant 4).
        var securityParams = new SqlParameterCollection();
        var securityWhere = "";

        // Resolve the meta table by BOTH schema and DbName. GetTableFromDbName is
        // DbName-only (first-wins), so it can return a same-named meta table in a
        // different schema and apply the wrong table's security filter.
        var metaTable = context.Model.Tables.FirstOrDefault(t =>
            string.Equals(t.DbName, config.MetaTableDbName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.TableSchema, config.TableSchema, StringComparison.OrdinalIgnoreCase));

        if (metaTable == null)
            // Without the meta table in the model there is no policy to consult and no
            // column metadata to drive the crypto projection, so its rows cannot be
            // shown safely. Refuse rather than emit unguarded, possibly encrypted values.
            throw new BifrostExecutionError(
                $"The EAV meta table for '{context.Table.GraphQlName}' is not present in the model.");

        var readChain = TableReadChain.For(
            context.Services, context.Model, metaTable, context.UserContext,
            ReadProjection.Client, QueryType.Standard, metaTable.GraphQlName, isNestedQuery: true);

        // `_meta` IS a client selection (the caller asked for the field), so a denied
        // column aborts rather than being silently dropped — the same reject semantics
        // the ordinary query path uses. The FK is the read's WHERE predicate and so
        // must clear the filter guard too.
        readChain.AssertReadable(new[] { config.KeyColumn, config.ValueColumn });
        readChain.AssertPredicateColumns(new[] { config.ForeignKeyColumn });

        var rowFilter = readChain.RowFilter;
        if (rowFilter != null)
        {
            var rendered = rowFilter.ToSqlParameterized(context.Model, dialect, securityParams, alias: metaTable.DbName);
            securityWhere = rendered.Sql;
        }

        var whereClause = string.IsNullOrEmpty(securityWhere)
            ? $"{fkCol}={paramName}"
            : $"{fkCol}={paramName} AND ({securityWhere})";
        var sql = $"SELECT {keyCol},{valueCol} FROM {metaTableRef} WHERE {whereClause}";

        await using var conn = context.ConnFactory.GetConnection();
        await conn.OpenAsync(cancellationToken);
        await using var command = conn.CreateCommand();
        command.CommandText = sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = paramName;
        parameter.Value = pkValue;
        command.Parameters.Add(parameter);

        foreach (var securityParameter in securityParams.Parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = securityParameter.Name;
            p.Value = securityParameter.Value ?? DBNull.Value;
            command.Parameters.Add(p);
        }

        var attributes = new List<KeyValuePair<string, object?>>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                // Both cells are raw meta-table column values, so both go through the
                // read chain's crypto projection before they reach the _meta JSON.
                var rawKey = reader.IsDBNull(0) ? null : reader.GetValue(0);
                var key = readChain.ProjectValue(config.KeyColumn, rawKey)?.ToString();
                if (string.IsNullOrEmpty(key))
                    continue;
                var rawValue = reader.IsDBNull(1) ? null : reader.GetValue(1);
                attributes.Add(new KeyValuePair<string, object?>(
                    key, readChain.ProjectValue(config.ValueColumn, rawValue)));
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

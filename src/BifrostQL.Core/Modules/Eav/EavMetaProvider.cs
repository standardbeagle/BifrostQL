using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;

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

    public async ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var values = await ComputeBatchAsync(new ComputedColumnBatchContext
        {
            Model = context.Model,
            Table = context.Table,
            Column = context.Column,
            Rows = new[] { context.Row },
            UserContext = context.UserContext,
            Services = context.Services,
            ConnFactory = context.ConnFactory,
        }, cancellationToken);
        return values[0];
    }

    /// <summary>
    /// <c>_meta</c> for every row of the page in one read: the attribute rows of all the
    /// page's parents come back from <c>WHERE fk IN (...)</c> (chunked at
    /// <see cref="ParentKey.MaxKeysPerQuery"/> keys) and are grouped by parent key. A row
    /// without a key, or on a table with no parent configuration, gets null; a keyed row
    /// with no attribute rows gets <c>"{}"</c>.
    /// </summary>
    public async ValueTask<IReadOnlyList<object?>> ComputeBatchAsync(ComputedColumnBatchContext context, CancellationToken cancellationToken = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var values = new object?[context.Rows.Count];

        // Metadata-driven: locate the EAV config whose parent table is the table
        // currently being projected. Match on schema too — a same-named parent in a
        // different schema (app.settings vs dbo.settings) must not bind here. The
        // parent shares the meta table's schema (see EavConfigCollector), which the
        // config carries as TableSchema. No name-prefix detection.
        var config = context.Model.EavConfigs.FirstOrDefault(e =>
            string.Equals(e.ParentTableDbName, context.Table.DbName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.TableSchema, context.Table.TableSchema, StringComparison.OrdinalIgnoreCase));
        if (config is null)
            return values;

        // Composite-PK limitation: the meta table references the parent via a
        // single foreign-key column, so a parent must have exactly one PK column
        // for the _meta lookup to be unambiguous. Tables with composite keys are
        // not supported by this provider (documented limitation).
        var keyColumns = context.Table.KeyColumns.ToArray();
        if (keyColumns.Length != 1 || context.ConnFactory is null)
            return values;

        var rowKeys = new object?[values.Length];
        var distinctKeys = new List<object>();
        var seen = new HashSet<ParentKey>();
        for (var i = 0; i < rowKeys.Length; i++)
        {
            var pk = ReadKeyValue(context.Rows[i], keyColumns[0]);
            rowKeys[i] = pk;
            if (pk is not null && seen.Add(ParentKey.Of(pk)))
                distinctKeys.Add(pk);
        }
        if (distinctKeys.Count == 0)
            return values;

        // Resolve the meta table by BOTH schema and DbName via the schema-qualified lookup:
        // a bare-name lookup can return a same-named meta table in a different schema and
        // apply the wrong table's security filter. Without the meta table in the model there
        // is no policy to consult and no column metadata to drive the crypto projection, so
        // its rows cannot be shown safely — refuse rather than emit unguarded values.
        IDbTable metaTable;
        try
        {
            metaTable = context.Model.GetTableFromDbName(config.TableSchema, config.MetaTableDbName);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new BifrostExecutionError(
                $"The EAV meta table for '{context.Table.GraphQlName}' is not present in the model.");
        }

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
        // stay with the guards (protocol-adapter-security invariant 4). One chain
        // serves the whole page.
        var readChain = await TableReadChain.ForAsync(
            context.Services, context.Model, metaTable, context.UserContext,
            ReadProjection.Client, QueryType.Standard, metaTable.GraphQlName, isNestedQuery: true);

        // `_meta` IS a client selection (the caller asked for the field), so a denied
        // column aborts rather than being silently dropped — the same reject semantics
        // the ordinary query path uses. The FK is the read's WHERE predicate and so
        // must clear the filter guard too.
        readChain.AssertReadable(new[] { config.KeyColumn, config.ValueColumn });
        readChain.AssertPredicateColumns(new[] { config.ForeignKeyColumn });

        var dialect = context.ConnFactory.Dialect;
        var securityParams = new SqlParameterCollection();
        var securityWhere = "";
        var rowFilter = readChain.RowFilter;
        if (rowFilter != null)
        {
            // Spliced into an existing WHERE, so a relationship-shaped filter (one
            // that needs an INNER JOIN) cannot be honoured; refuse it rather than
            // emit "AND (INNER JOIN ...)" and let the database reject it.
            var parts = rowFilter.RenderParts(context.Model, dialect, securityParams, alias: metaTable.DbName);
            if (!string.IsNullOrWhiteSpace(parts.Joins))
                throw new BifrostExecutionError(
                    "EAV _meta cannot apply a row-scope filter that requires a relationship join.");
            securityWhere = parts.Where;
        }

        var metaTableRef = dialect.TableReference(config.TableSchema, config.MetaTableDbName);
        var fkCol = dialect.EscapeIdentifier(config.ForeignKeyColumn);
        var selectList = $"{fkCol},{dialect.EscapeIdentifier(config.KeyColumn)},{dialect.EscapeIdentifier(config.ValueColumn)}";
        var fkDataType = metaTable.ColumnLookup[config.ForeignKeyColumn].DataType;

        var attributesByParent = new Dictionary<ParentKey, List<KeyValuePair<string, object?>>>();
        await using var conn = context.ConnFactory.GetConnection();
        await conn.OpenAsync(cancellationToken);
        for (var offset = 0; offset < distinctKeys.Count; offset += ParentKey.MaxKeysPerQuery)
        {
            var chunk = distinctKeys.Skip(offset).Take(ParentKey.MaxKeysPerQuery).ToList();
            await using var command = conn.CreateCommand();
            var placeholders = new string[chunk.Count];
            for (var i = 0; i < chunk.Count; i++)
            {
                var name = $"{dialect.ParameterPrefix}pk{i}";
                placeholders[i] = DbParameterBinder.ComparisonPlaceholder(dialect, name, fkDataType, chunk[i]);
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                DbParameterBinder.SetValue(parameter, chunk[i]);
                command.Parameters.Add(parameter);
            }
            foreach (var securityParameter in securityParams.Parameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = securityParameter.Name;
                DbParameterBinder.SetValue(p, securityParameter.Value);
                command.Parameters.Add(p);
            }

            var keyPredicate = $"{fkCol} IN ({string.Join(",", placeholders)})";
            var whereClause = string.IsNullOrEmpty(securityWhere)
                ? keyPredicate
                : $"{keyPredicate} AND ({securityWhere})";
            command.CommandText = $"SELECT {selectList} FROM {metaTableRef} WHERE {whereClause}";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0))
                    continue;
                // Both cells are raw meta-table column values, so both go through the
                // read chain's crypto projection before they reach the _meta JSON.
                var rawKey = reader.IsDBNull(1) ? null : reader.GetValue(1);
                var key = readChain.ProjectValue(config.KeyColumn, rawKey)?.ToString();
                if (string.IsNullOrEmpty(key))
                    continue;
                var rawValue = reader.IsDBNull(2) ? null : reader.GetValue(2);
                var parentKey = ParentKey.Of(reader.GetValue(0));
                if (!attributesByParent.TryGetValue(parentKey, out var attributes))
                    attributesByParent[parentKey] = attributes = new List<KeyValuePair<string, object?>>();
                attributes.Add(new KeyValuePair<string, object?>(
                    key, readChain.ProjectValue(config.ValueColumn, rawValue)));
            }
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (rowKeys[i] is not { } pk)
                continue;
            values[i] = SerializeAttributes(ParentKey.TryGetGroup(attributesByParent, pk, out var attributes)
                ? attributes
                : Array.Empty<KeyValuePair<string, object?>>());
        }
        return values;
    }

    private static object? ReadKeyValue(IReadOnlyDictionary<string, object?> row, ColumnDto keyColumn)
    {
        // The synthesized definition declares the PK (DB name) as its only
        // dependency, so the projected row is keyed by that name. Fall back to
        // the GraphQL name defensively (mirrors StateMachineTransitionsProvider).
        if (row.TryGetValue(keyColumn.DbName, out var byDb) && byDb is not null)
            return byDb;

        if (row.TryGetValue(keyColumn.GraphQlName, out var byGraphQl) && byGraphQl is not null)
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

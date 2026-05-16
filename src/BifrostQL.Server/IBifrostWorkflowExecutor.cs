using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Workflows;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using System.Globalization;
using System.Text;

namespace BifrostQL.Server
{
    /// <summary>
    /// Server-side abstraction for running Bifrost queries and mutations from
    /// sidecar workflow endpoints (see the "Workflow Mutations &amp; Audit Trail"
    /// guide). Every call runs through the SAME GraphQL execution pipeline as a
    /// direct <c>/graphql</c> request — <c>tenant-filter</c>,
    /// <c>PolicyFilterTransformer</c>, <c>PolicyMutationTransformer</c>, and the
    /// audit module all still apply. A workflow endpoint composes business logic
    /// on top of the pipeline; it never bypasses it.
    /// </summary>
    public interface IBifrostWorkflowExecutor : IWorkflowDataExecutor;

    /// <summary>
    /// Default <see cref="IBifrostWorkflowExecutor"/>. It builds a GraphQL
    /// document for the requested operation and executes it through
    /// <see cref="BifrostDocumentExecutor"/> — the identical executor the
    /// <see cref="BifrostHttpMiddleware"/> uses — so the operation traverses the
    /// full transformer/module pipeline. No SQL is generated here and no
    /// authorization is reimplemented.
    /// </summary>
    public sealed class BifrostWorkflowExecutor : IBifrostWorkflowExecutor
    {
        private readonly BifrostDocumentExecutor _executor;
        private readonly PathCache<Inputs> _extensionsLoader;
        private readonly IServiceProvider _services;

        public BifrostWorkflowExecutor(
            IDocumentExecuter documentExecuter,
            PathCache<Inputs> extensionsLoader,
            IServiceProvider services)
        {
            _executor = new BifrostDocumentExecutor(
                documentExecuter ?? throw new ArgumentNullException(nameof(documentExecuter)));
            _extensionsLoader = extensionsLoader ?? throw new ArgumentNullException(nameof(extensionsLoader));
            _services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public async Task<IDictionary<string, object?>?> QuerySingleAsync(
            string table, object id, IDictionary<string, object?> userContext)
        {
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("Table name is required.", nameof(table));
            if (id is null) throw new ArgumentNullException(nameof(id));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var dbTable = ResolveTable(table);
            var columns = dbTable.Columns.Select(c => c.GraphQlName);
            var query =
                $"query {{ {dbTable.GraphQlName}(_primaryKey: [{ToGraphQlString(id.ToString())}]) " +
                $"{{ data {{ {string.Join(' ', columns)} }} }} }}";

            var result = await ExecuteAsync(query, userContext);
            var rows = ExtractRows(result, dbTable.GraphQlName);
            return rows.Count == 0 ? null : rows[0];
        }

        public Task InsertAsync(string table, object values, IDictionary<string, object?> userContext)
            => MutateAsync(table, "insert", values, userContext);

        public Task UpdateAsync(string table, object values, IDictionary<string, object?> userContext)
            => MutateAsync(table, "update", values, userContext);

        private async Task MutateAsync(
            string table, string action, object values, IDictionary<string, object?> userContext)
        {
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("Table name is required.", nameof(table));
            if (values is null) throw new ArgumentNullException(nameof(values));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var dbTable = ResolveTable(table);
            var fields = ToFieldMap(values, dbTable);
            if (fields.Count == 0)
                throw new ArgumentException($"No writable columns supplied for '{table}'.", nameof(values));

            var literal = string.Join(", ", fields.Select(kv => $"{kv.Key}: {ToGraphQlLiteral(kv.Value)}"));
            var mutation = $"mutation {{ {dbTable.GraphQlName}({action}: {{ {literal} }}) }}";

            var result = await ExecuteAsync(mutation, userContext);
            ThrowOnErrors(result);
        }

        private async Task<ExecutionResult> ExecuteAsync(
            string document, IDictionary<string, object?> userContext)
        {
            var options = new ExecutionOptions
            {
                Query = document,
                RequestServices = _services,
                UserContext = userContext,
            };
            return await _executor.ExecuteAsync(options);
        }

        /// <summary>
        /// Resolves a database table name to its model definition. The model is
        /// read from the same <see cref="PathCache{T}"/> the document executor
        /// uses, so the workflow executor and the GraphQL endpoint always agree
        /// on the schema.
        /// </summary>
        private IDbTable ResolveTable(string table)
        {
            var extensions = _extensionsLoader.GetFirstValue()
                ?? throw new InvalidOperationException(
                    "No BifrostQL schemas are configured. Set a connection string first.");
            var model = (IDbModel)(extensions["model"]
                ?? throw new InvalidDataException("model not configured"));
            return model.GetTableFromDbName(table);
        }

        private static Dictionary<string, object?> ToFieldMap(object values, IDbTable table)
        {
            var fields = new Dictionary<string, object?>();
            if (values is IDictionary<string, object?> dictionary)
            {
                foreach (var (key, value) in dictionary)
                    AddField(fields, table, key, value);
                return fields;
            }

            foreach (var property in values.GetType().GetProperties())
            {
                AddField(fields, table, property.Name, property.GetValue(values));
            }
            return fields;
        }

        private static void AddField(
            Dictionary<string, object?> fields,
            IDbTable table,
            string name,
            object? value)
        {
            var column = table.Columns.FirstOrDefault(c =>
                string.Equals(c.DbName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.GraphQlName, name, StringComparison.OrdinalIgnoreCase));
            if (column is null)
                throw new ArgumentException(
                    $"'{name}' is not a column of '{table.DbName}'.", nameof(name));
            fields[column.GraphQlName] = value;
        }

        private static IReadOnlyList<IDictionary<string, object?>> ExtractRows(
            ExecutionResult result, string field)
        {
            ThrowOnErrors(result);
            if (result.Data is not RootExecutionNode root
                || root.ToValue() is not IDictionary<string, object?> data
                || !data.TryGetValue(field, out var paged)
                || paged is not IDictionary<string, object?> pagedMap
                || !pagedMap.TryGetValue("data", out var rows)
                || rows is not IEnumerable<object?> rowList)
            {
                return Array.Empty<IDictionary<string, object?>>();
            }

            return rowList
                .OfType<IDictionary<string, object?>>()
                .ToList();
        }

        private static void ThrowOnErrors(ExecutionResult result)
        {
            if (result.Errors is { Count: > 0 })
                throw new InvalidOperationException(
                    string.Join("; ", result.Errors.Select(e => e.Message)));
        }

        /// <summary>
        /// Renders a CLR value as a GraphQL literal for embedding in a document.
        /// </summary>
        private static string ToGraphQlLiteral(object? value)
        {
            return value switch
            {
                null => "null",
                bool b => b ? "true" : "false",
                string s => ToGraphQlString(s),
                DateTime dt => ToGraphQlString(dt.ToString("o", CultureInfo.InvariantCulture)),
                DateTimeOffset dto => ToGraphQlString(dto.ToString("o", CultureInfo.InvariantCulture)),
                Guid g => ToGraphQlString(g.ToString()),
                byte or sbyte or short or ushort or int or uint or long or ulong
                    => Convert.ToString(value, CultureInfo.InvariantCulture)!,
                float f => f.ToString("R", CultureInfo.InvariantCulture),
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                decimal m => m.ToString(CultureInfo.InvariantCulture),
                _ => ToGraphQlString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
            };
        }

        private static string ToGraphQlString(string? value)
        {
            var builder = new StringBuilder(value?.Length + 2 ?? 2);
            builder.Append('"');
            foreach (var ch in value ?? string.Empty)
            {
                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default: builder.Append(ch); break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }
    }
}

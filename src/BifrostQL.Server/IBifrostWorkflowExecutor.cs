using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Workflows;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using BifrostQL.Core.Auth;

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
            string table, object id, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("Table name is required.", nameof(table));
            if (id is null) throw new ArgumentNullException(nameof(id));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            var dbTable = await ResolveTableAsync(table);
            var columns = dbTable.Columns.Select(c => c.GraphQlName);
            var query =
                $"query {{ {dbTable.GraphQlName}(_primaryKey: [{ToGraphQlString(id.ToString())}]) " +
                $"{{ data {{ {string.Join(' ', columns)} }} }} }}";

            var result = await ExecuteAsync(query, userContext, cancellationToken);
            var rows = ExtractRows(result, dbTable.GraphQlName);
            return rows.Count == 0 ? null : rows[0];
        }

        public Task InsertAsync(
            string table, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
            => MutateAsync(table, "insert", values, userContext, cancellationToken);

        public Task UpdateAsync(
            string table, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
            => MutateAsync(table, "update", values, userContext, cancellationToken);

        /// <summary>
        /// Sends only the key and the state column, with a <see cref="WorkflowTransitionGuard"/> in a
        /// copy of the user context: the pipeline adds <c>state = from</c> to the UPDATE's WHERE and
        /// turns zero affected rows into a CONFLICT error.
        /// </summary>
        public async Task TransitionAsync(
            WorkflowTransition transition, IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(transition);
            ArgumentNullException.ThrowIfNull(userContext);
            if (string.IsNullOrWhiteSpace(transition.Table))
                throw new ArgumentException("Table name is required.", nameof(transition));

            var dbTable = await ResolveTableAsync(transition.Table);
            var keys = dbTable.KeyColumns.ToArray();
            if (keys.Length != 1)
                throw new ArgumentException(
                    $"A workflow transition needs a single-column primary key; '{dbTable.DbName}' has {keys.Length}.",
                    nameof(transition));
            var stateColumn = dbTable.Columns.FirstOrDefault(c =>
                    string.Equals(c.DbName, transition.StateColumn, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"'{transition.StateColumn}' is not a column of '{dbTable.DbName}'.", nameof(transition));

            var guardedContext = UserContexts.Copy(userContext);
            guardedContext[WorkflowTransitionGuard.ContextKey] = new WorkflowTransitionGuard(
                dbTable, keys[0].DbName, transition.Id, stateColumn.DbName, transition.From);
            await MutateAsync(dbTable, "update", new Dictionary<string, object?>
            {
                [keys[0].DbName] = transition.Id,
                [stateColumn.DbName] = transition.To,
            }, guardedContext, cancellationToken);
        }

        private async Task MutateAsync(
            string table, string action, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("Table name is required.", nameof(table));
            if (values is null) throw new ArgumentNullException(nameof(values));
            if (userContext is null) throw new ArgumentNullException(nameof(userContext));

            await MutateAsync(await ResolveTableAsync(table), action, values, userContext, cancellationToken);
        }

        private async Task MutateAsync(
            IDbTable dbTable, string action, object values, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            var fields = ToFieldMap(values, dbTable);
            if (fields.Count == 0)
                throw new ArgumentException($"No writable columns supplied for '{dbTable.DbName}'.", nameof(values));

            var literal = string.Join(", ", fields.Select(kv => $"{kv.Key}: {ToGraphQlLiteral(kv.Value)}"));
            var mutation = $"mutation {{ {dbTable.GraphQlName}({action}: {{ {literal} }}) }}";

            var result = await ExecuteAsync(mutation, userContext, cancellationToken);
            ThrowOnErrors(result);
        }

        private async Task<ExecutionResult> ExecuteAsync(
            string document, IDictionary<string, object?> userContext, CancellationToken cancellationToken)
        {
            var options = new ExecutionOptions
            {
                Query = document,
                RequestServices = _services,
                UserContext = userContext,
                CancellationToken = cancellationToken,
            };
            return await _executor.ExecuteAsync(options);
        }

        /// <summary>
        /// Resolves a database table name to its model definition through the SAME endpoint
        /// resolution the document executor runs for this request. A first-endpoint pick here
        /// resolved tables against endpoint one while the document ran against the request's
        /// endpoint; on a multi-endpoint host with no matching path both now refuse
        /// (<see cref="UnknownBifrostEndpointException"/>) instead of guessing.
        /// </summary>
        private async Task<IDbTable> ResolveTableAsync(string table)
        {
            var httpContext = _services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext;
            var extensions = await BifrostDocumentExecutor.ResolveExtensionsAsync(_extensionsLoader, httpContext);
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

        /// <summary>
        /// Raises the document's errors as one exception. Its inner exception is the
        /// conflict error when one occurred, else the first error, so a caller reads the
        /// condition from <see cref="BifrostExecutionError.ErrorCode"/> on the chain: the
        /// joined messages can quote the caller's own values.
        /// </summary>
        private static void ThrowOnErrors(ExecutionResult result)
        {
            if (result.Errors is not { Count: > 0 } errors)
                return;
            var cause = errors.FirstOrDefault(e => e.Code == BifrostExecutionError.ConflictCode) ?? errors[0];
            throw new InvalidOperationException(string.Join("; ", errors.Select(e => e.Message)), cause);
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

        internal static string ToGraphQlString(string? value)
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
                    // Any other control character is illegal raw inside a GraphQL string.
                    case < ' ': builder.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)); break;
                    default: builder.Append(ch); break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }
    }
}

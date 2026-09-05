using System.Collections;
using System.Globalization;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// A stand-in for the query pipeline that behaves like the real one in the two ways these tests
    /// depend on.
    ///
    /// <para><b>It scopes by identity, unconditionally.</b> A row carrying a <c>tenant</c> column is
    /// visible only to a user context carrying the same <c>tenant</c> value, and that narrowing is
    /// applied to EVERY intent before anything else — exactly as the real pipeline ANDs its tenant,
    /// policy, and soft-delete predicates on. This is what lets a test assert that the LDAP layer
    /// cannot emit a foreign row's DN: the row never reaches it.</para>
    ///
    /// <para><b>It applies the intent's own filter, limit, and offset</b>, so paging and predicate
    /// pushdown are exercised rather than assumed. The filter interpreter covers the operator set
    /// the LDAP compiler emits and nothing else; an operator it does not know throws, so a compiler
    /// change that started emitting something new fails loudly here instead of being silently
    /// ignored and making a test pass for the wrong reason.</para>
    ///
    /// <para>Every executed intent is recorded, so a test can assert what was actually asked of the
    /// pipeline — the pushed-down predicate, the ordering, the row bound.</para>
    /// </summary>
    internal sealed class LdapFakeIntentExecutor : IQueryIntentExecutor
    {
        private readonly IDbModel _model;
        private readonly Dictionary<string, List<Dictionary<string, object?>>> _rows =
            new(StringComparer.OrdinalIgnoreCase);

        public LdapFakeIntentExecutor(IDbModel model) => _model = model;

        /// <summary>Every intent executed, in order.</summary>
        public List<QueryIntent> Intents { get; } = new();

        /// <summary>Set to throw from the next execute, standing in for a pipeline fault.</summary>
        public Exception? Fault { get; set; }

        public LdapFakeIntentExecutor WithRows(string table, params Dictionary<string, object?>[] rows)
        {
            if (!_rows.TryGetValue(table, out var list))
                _rows[table] = list = new List<Dictionary<string, object?>>();
            list.AddRange(rows);
            return this;
        }

        /// <summary>Adds <paramref name="count"/> sequentially-named people rows.</summary>
        public LdapFakeIntentExecutor WithPeople(int count, string? tenant = null, int startId = 1)
        {
            for (var i = 0; i < count; i++)
            {
                var id = startId + i;
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = id,
                    ["username"] = $"user{id:D4}",
                    ["full_name"] = $"User {id:D4}",
                    ["email"] = $"user{id:D4}@example.com",
                    ["password_hash"] = $"$2y$hash-for-user{id:D4}",
                    ["uid_number"] = 1000 + id,
                };
                if (tenant is not null)
                    row["tenant"] = tenant;
                WithRows("users", row);
            }
            return this;
        }

        public Task<IDbModel> GetModelAsync(string? endpoint = null) => Task.FromResult(_model);

        public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intents.Add(intent);

            if (Fault is { } fault)
                throw fault;

            // The column-read guard, exactly as the real pipeline's PolicyFilterTransformer
            // (IColumnReadGuard) applies it: selecting a column this identity may not read rejects
            // the WHOLE query with an ACCESS_DENIED-tagged error. This is what makes a wildcard
            // selection that drags in a denied column fail the search it rode in on.
            if (intent.Query.DbTable is { } intentTable)
            {
                var visible = SchemaReadVisibility.ProjectTable(intentTable, intent.UserContext);
                var denied = intent.Query.ScalarColumns
                    .Select(c => c.DbDbName)
                    .FirstOrDefault(c => visible is null || !visible.HasColumn(c));
                if (denied is not null)
                    throw new BifrostExecutionError("The query references a field that is not permitted by authorization policy.")
                    { ErrorCode = BifrostExecutionError.AccessDeniedCode };
            }

            var table = intent.Query.TableName;
            var source = _rows.TryGetValue(table, out var list)
                ? list
                : new List<Dictionary<string, object?>>();

            // Identity scoping FIRST, like the pipeline. Nothing the caller supplied can displace it.
            var scoped = source.Where(row => IsVisible(row, intent.UserContext)).ToList();

            // Deterministic order, standing in for the query's ORDER BY.
            if (intent.Query.Sort.Count > 0)
                scoped = scoped
                    .OrderBy(r => SortKey(r, intent.Query.Sort), Comparer<string>.Default)
                    .ToList();

            var filtered = intent.Query.Filter is { } filter
                ? scoped.Where(row => Evaluate(filter, row)).ToList()
                : scoped;

            IEnumerable<Dictionary<string, object?>> page = filtered;
            if (intent.Query.Offset is { } offset)
                page = page.Skip(offset);
            if (intent.Query.Limit is { } limit)
                page = page.Take(limit);

            // Only the columns the intent selected, so a test can prove a column was never fetched.
            var selected = intent.Query.ScalarColumns.Select(c => c.DbDbName).ToList();
            var rows = page
                .Select(row => (IReadOnlyDictionary<string, object?>)selected
                    .Where(row.ContainsKey)
                    .ToDictionary(c => c, c => row[c], StringComparer.OrdinalIgnoreCase))
                .ToList();

            return Task.FromResult(new QueryIntentResult
            {
                Rows = rows,
                Sql = "-- fake",
                TotalCount = intent.Query.IncludeResult ? filtered.Count : null,
            });
        }

        // The tenant narrowing every real intent carries. A row with no tenant column is
        // unscoped test data and visible to everyone.
        private static bool IsVisible(IReadOnlyDictionary<string, object?> row, IDictionary<string, object?> userContext)
        {
            if (!row.TryGetValue("tenant", out var rowTenant) || rowTenant is null)
                return true;
            return userContext.TryGetValue("tenant", out var callerTenant)
                && string.Equals(
                    Convert.ToString(rowTenant, CultureInfo.InvariantCulture),
                    Convert.ToString(callerTenant, CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
        }

        private static string SortKey(IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> sort)
        {
            var parts = new List<string>();
            foreach (var token in sort)
            {
                var column = token.EndsWith("_asc", StringComparison.Ordinal) ? token[..^4]
                    : token.EndsWith("_desc", StringComparison.Ordinal) ? token[..^5]
                    : token;
                var value = row.TryGetValue(column, out var v) ? v : null;
                // Numeric keys are zero-padded so an ordinal comparison orders them numerically.
                parts.Add(value switch
                {
                    null => string.Empty,
                    int i => i.ToString("D12", CultureInfo.InvariantCulture),
                    long l => l.ToString("D18", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                });
            }
            return string.Join("\0", parts);
        }

        // ---- filter interpretation ----

        private static bool Evaluate(TableFilter filter, IReadOnlyDictionary<string, object?> row)
        {
            switch (filter.FilterType)
            {
                case FilterType.And:
                    return filter.And.All(f => Evaluate(f, row));
                case FilterType.Or:
                    return filter.Or.Any(f => Evaluate(f, row));
                case FilterType.Join:
                {
                    var column = filter.ColumnName;
                    var relation = filter.Next
                        ?? throw new InvalidOperationException($"filter on '{column}' carries no relation.");
                    return Relation(relation, Value(row, column));
                }
                case FilterType.Relation:
                    return Relation(filter, Value(row, filter.ColumnName));
                default:
                    throw new InvalidOperationException($"unsupported filter type '{filter.FilterType}'.");
            }
        }

        private static bool Relation(TableFilter relation, object? stored)
        {
            var op = relation.RelationName;
            var asserted = relation.Value;

            // Null-valued equality is how the builders render IS NULL / IS NOT NULL.
            if (asserted is null && op is FilterOperators.Eq)
                return stored is null;
            if (asserted is null && op is FilterOperators.Neq)
                return stored is not null;

            // SQL's null semantics: a comparison against NULL is never true.
            if (stored is null)
                return false;

            switch (op)
            {
                case FilterOperators.Eq: return Compare(stored, asserted) == 0;
                case FilterOperators.Neq: return Compare(stored, asserted) != 0;
                case FilterOperators.Lt: return Compare(stored, asserted) < 0;
                case FilterOperators.Lte: return Compare(stored, asserted) <= 0;
                case FilterOperators.Gt: return Compare(stored, asserted) > 0;
                case FilterOperators.Gte: return Compare(stored, asserted) >= 0;
                case FilterOperators.In:
                    return ((IEnumerable)asserted!).Cast<object?>().Any(v => Compare(stored, v) == 0);
                case FilterOperators.Contains:
                    return Text(stored).Contains(Text(asserted), StringComparison.OrdinalIgnoreCase);
                case FilterOperators.NContains:
                    return !Text(stored).Contains(Text(asserted), StringComparison.OrdinalIgnoreCase);
                case FilterOperators.StartsWith:
                    return Text(stored).StartsWith(Text(asserted), StringComparison.OrdinalIgnoreCase);
                case FilterOperators.EndsWith:
                    return Text(stored).EndsWith(Text(asserted), StringComparison.OrdinalIgnoreCase);
                default:
                    // Loud on purpose: a silently-ignored operator would make a test pass because
                    // the filter did nothing, which is the opposite of what it claims to prove.
                    throw new InvalidOperationException(
                        $"the fake pipeline does not implement operator '{op}'.");
            }
        }

        private static object? Value(IReadOnlyDictionary<string, object?> row, string column) =>
            row.TryGetValue(column, out var value) && value is not DBNull ? value : null;

        private static string Text(object? value) =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        private static int Compare(object? left, object? right)
        {
            if (left is null || right is null)
                return left is null && right is null ? 0 : 1;
            if (left is IConvertible && right is IConvertible
                && (left is int or long or decimal or double || right is int or long or decimal or double))
            {
                return Convert.ToDecimal(left, CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
            }
            return string.Compare(Text(left), Text(right), StringComparison.OrdinalIgnoreCase);
        }
    }
}

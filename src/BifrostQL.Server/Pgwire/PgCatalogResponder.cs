using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// A pre-materialized answer to a catalog / introspection query: the ordered
    /// output columns and the rows (column-name → value) to encode, exactly like the
    /// real read path's <c>(Columns, Rows)</c> — so the connection handler streams it
    /// with the same RowDescription/DataRow code, no special catalog wire path.
    /// </summary>
    internal sealed record PgCatalogResponse(
        IReadOnlyList<PgResultColumn> Columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

    /// <summary>
    /// Recognizes the introspection queries <c>psql \d</c> and BI tools issue against
    /// the emulated PostgreSQL catalog and answers them from a DbModel-derived,
    /// identity-filtered projection. Returns null for any query it does not own, so
    /// the connection handler falls through to the normal SQL read path unchanged.
    /// </summary>
    internal interface IPgCatalogResponder
    {
        /// <summary>
        /// Answers a catalog/introspection query, or returns null when
        /// <paramref name="sql"/> is not one this responder handles. A recognized but
        /// malformed catalog query throws <see cref="PgQueryTranslationException"/>
        /// (mapped to a clean pg error), never a silent wrong answer.
        /// </summary>
        Task<PgCatalogResponse?> TryRespondAsync(
            IQueryIntentExecutor executor,
            string sql,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default <see cref="IPgCatalogResponder"/>. Reuses <see cref="PgSqlSubsetParser"/>
    /// (the same allowlist grammar the read path uses, so literals are validated and
    /// injection-safe), routes a SELECT whose FROM names an emulated catalog relation
    /// to <see cref="PgCatalog"/>, and evaluates the parsed WHERE / projection / ORDER
    /// BY over the synthesized rows in memory. Table/column visibility is enforced by
    /// <see cref="PgCatalogVisibility"/> under the SAME authoritative policy check the
    /// query path uses. Also answers the handful of scalar introspection functions
    /// (<c>version()</c>, <c>current_schema()</c>, …) drivers issue at connect.
    /// </summary>
    internal sealed class PgCatalogResponder : IPgCatalogResponder
    {
        private const string ServerVersion = "PostgreSQL 16.0 (BifrostQL)";
        private const string DefaultSchema = "public";
        private const string DatabaseName = "bifrost";

        // The emulated catalog has no real pg roles; every synthesized relation is
        // owned by one honest synthetic owner, reported where psql renders
        // pg_get_userbyid(relowner) as the "Owner" column of \d / \dt.
        private const string SyntheticOwnerName = "bifrost";

        // Structural signature of the query psql issues for \d and \dt: a pg_class ⋈
        // pg_namespace join carrying the relkind IN (...) filter and the
        // pg_table_is_visible(oid) guard, ending in the positional ORDER BY 1[,2].
        // This shape (CASE, function calls, LEFT JOIN, quoted aliases) is far outside
        // the slice-3 subset grammar, so it is recognized here and answered by an
        // in-memory join over the visibility-filtered projection — never routed
        // through PgSqlSubsetParser, which is deliberately left untouched. Group 1
        // captures the relkind list so the filter is applied faithfully. Requiring the
        // pg_class + pg_namespace + pg_table_is_visible signature keeps this narrow: a
        // user query does not carry pg_table_is_visible.
        private static readonly Regex DescribeRelationsPattern = new(
            @"from\s+(?:pg_catalog\.)?pg_class\b.*\bjoin\s+(?:pg_catalog\.)?pg_namespace\b.*\brelkind\s+in\s*\(([^)]*)\).*\bpg_table_is_visible\b.*\border\s+by\s+1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public async Task<PgCatalogResponse?> TryRespondAsync(
            IQueryIntentExecutor executor,
            string sql,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            if (executor is null) throw new ArgumentNullException(nameof(executor));

            // 1. Scalar introspection functions/keywords (version(), current_schema(), …)
            //    are not in the SELECT-FROM grammar, so match them before parsing.
            var scalar = TryScalar(sql, userContext);
            if (scalar is not null)
                return scalar;

            // 2. psql \d / \dt relation-list query: a pg_class ⋈ pg_namespace join with
            //    the relkind / pg_table_is_visible signature. Answered by an in-memory
            //    join over the identity-visible projection — the subset parser does not
            //    accept this shape and is intentionally not loosened for it.
            var describe = DescribeRelationsPattern.Match(sql);
            if (describe.Success)
            {
                var describeModel = await executor.GetModelAsync(endpoint);
                var describeVisible = PgCatalogVisibility.Project(describeModel, userContext);
                return BuildDescribeRelations(describeVisible, describe);
            }

            // 3. Single-relation catalog SELECT. Decide catalog-vs-user by the PARSED
            //    FROM target, not a substring anywhere in the text: a user query that
            //    merely carries "information_schema.tables" / "pg_class" inside a string
            //    literal or column value must fall through to the read path, not be
            //    misrouted here. A query that does not parse under the subset is not one
            //    we own either — the read path emits the canonical error for it.
            PgSelectStatement stmt;
            try
            {
                stmt = PgSqlSubsetParser.Parse(sql);
            }
            catch (PgQueryTranslationException)
            {
                return null;
            }

            var relation = PgCatalog.ResolveRelation(stmt.From.Schema, stmt.From.Name);
            if (relation is null)
            {
                // An explicit pg_catalog / information_schema qualification we do not
                // emulate is a genuine catalog query → clean fail-closed error. Any other
                // FROM target is a user table → fall through to the read path unchanged.
                if (IsCatalogSchema(stmt.From.Schema))
                    throw new PgQueryTranslationException(
                        $"pgwire: catalog relation \"{stmt.From.Name}\" is not emulated.",
                        PgWireProtocol.SqlStateFeatureNotSupported);
                return null;
            }

            if (stmt.Join is not null)
                throw new PgQueryTranslationException(
                    "pgwire: joins over catalog relations are not emulated.",
                    PgWireProtocol.SqlStateFeatureNotSupported);

            var model = await executor.GetModelAsync(endpoint);
            var visible = PgCatalogVisibility.Project(model, userContext);
            var built = PgCatalog.Build(relation.Value, visible);

            return ProjectStatement(stmt, built);
        }

        // ---- scalar introspection -------------------------------------------

        private static PgCatalogResponse? TryScalar(string sql, IDictionary<string, object?> userContext)
        {
            var normalized = sql.Trim().TrimEnd(';').Trim();
            var lower = normalized.ToLowerInvariant();

            (string Column, object? Value)? answer = lower switch
            {
                "select version()" => ("version", ServerVersion),
                "select current_schema()" or "select current_schema" => ("current_schema", DefaultSchema),
                "select current_database()" or "select current_database" => ("current_database", DatabaseName),
                "select current_catalog" => ("current_catalog", DatabaseName),
                "select current_user" or "select session_user" or "select user"
                    => ("current_user", CurrentUser(userContext)),
                _ => null,
            };

            if (answer is null)
                return null;

            var columns = new[] { new PgResultColumn(answer.Value.Column, "varchar") };
            var row = new Dictionary<string, object?> { [answer.Value.Column] = answer.Value.Value };
            return new PgCatalogResponse(columns, new[] { (IReadOnlyDictionary<string, object?>)row });
        }

        private static object CurrentUser(IDictionary<string, object?> userContext) =>
            userContext.TryGetValue(MetadataKeys.Auth.DefaultUserIdContextKey, out var id) && id is not null
                ? id
                : DatabaseName;

        // ---- recognition -----------------------------------------------------

        private static bool IsCatalogSchema(string? schema) =>
            string.Equals(schema, "pg_catalog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schema, "information_schema", StringComparison.OrdinalIgnoreCase);

        // ---- psql \d / \dt relation list (in-memory pg_class ⋈ pg_namespace) -

        /// <summary>
        /// Answers the psql \d / \dt relation-list query by joining the synthesized
        /// pg_class and pg_namespace relations in memory over the identity-visible
        /// projection, honoring the query's relkind IN (...) filter and treating every
        /// visible table as pg_table_is_visible = true. Output mirrors psql's expected
        /// Schema / Name / Type / Owner columns, positionally ordered by (schema, name)
        /// to satisfy the query's ORDER BY 1,2. Because the input is the SAME
        /// PgCatalogVisibility projection the other catalog relations use, an
        /// identity-unreadable table is absent here too (fail closed).
        /// </summary>
        private static PgCatalogResponse BuildDescribeRelations(
            IReadOnlyList<PgCatalogTable> visible, Match describe)
        {
            var relkinds = ParseRelkindList(describe.Groups[1].Value);

            // Reuse the canonical relation builders so the join operates on exactly the
            // rows the catalog would otherwise expose (no re-derivation of visibility).
            var pgClass = PgCatalog.Build(PgCatalog.RelationKind.PgClass, visible);
            var pgNamespace = PgCatalog.Build(PgCatalog.RelationKind.PgNamespace, visible);

            var nspByOid = new Dictionary<int, object?>();
            foreach (var ns in pgNamespace.Rows)
                nspByOid[Convert.ToInt32(ns["oid"])] = ns["nspname"];

            var joined = new List<(object? Schema, object? Name, object? Type)>();
            foreach (var row in pgClass.Rows)
            {
                var relkind = row["relkind"] as string ?? "";
                if (!relkinds.Contains(relkind))
                    continue; // relkind IN (...) filter from the query

                nspByOid.TryGetValue(Convert.ToInt32(row["relnamespace"]), out var schema);
                if (IsSystemNamespace(schema as string))
                    continue; // the query excludes pg_catalog / information_schema / pg_toast

                // pg_table_is_visible(c.oid): every table surviving the identity-visible
                // projection is, by construction, visible.
                joined.Add((schema, row["relname"], DescribeRelkind(relkind)));
            }

            var columns = new[]
            {
                new PgResultColumn("Schema", "varchar"),
                new PgResultColumn("Name", "varchar"),
                new PgResultColumn("Type", "varchar"),
                new PgResultColumn("Owner", "varchar"),
            };

            var rows = joined
                .OrderBy(x => x.Schema, ValueComparer.Instance)
                .ThenBy(x => x.Name, ValueComparer.Instance)
                .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["Schema"] = x.Schema,
                    ["Name"] = x.Name,
                    ["Type"] = x.Type,
                    ["Owner"] = SyntheticOwnerName,
                })
                .ToList();

            return new PgCatalogResponse(columns, rows);
        }

        private static HashSet<string> ParseRelkindList(string inner)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(inner, "'([^']*)'"))
                set.Add(m.Groups[1].Value);
            return set;
        }

        // The synthesized pg_class only ever emits relkind 'r' (ordinary table); the
        // partitioned-table kind is mapped for faithfulness to psql's CASE.
        private static string DescribeRelkind(string relkind) => relkind switch
        {
            "p" => "partitioned table",
            _ => "table",
        };

        // The pg system namespaces psql's \d / \dt query explicitly excludes. The
        // synthesized namespaces are user schemas, so this is normally a no-op, but it
        // is applied defensively to match the query's WHERE.
        private static bool IsSystemNamespace(string? nsp) =>
            nsp is null
            || string.Equals(nsp, "pg_catalog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nsp, "information_schema", StringComparison.OrdinalIgnoreCase)
            || nsp.StartsWith("pg_toast", StringComparison.OrdinalIgnoreCase);

        // ---- projection / filtering over synthesized rows --------------------

        private static PgCatalogResponse ProjectStatement(PgSelectStatement stmt, PgCatalogRelation relation)
        {
            var columnNames = new HashSet<string>(
                relation.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

            // Filter.
            IEnumerable<IReadOnlyDictionary<string, object?>> rows = relation.Rows;
            if (stmt.Where is not null)
                rows = rows.Where(r => Match(stmt.Where, r, columnNames));

            // Order.
            if (stmt.OrderBy.Count > 0)
                rows = ApplyOrder(rows, stmt.OrderBy, columnNames);

            var materialized = rows.ToList();

            // Offset / limit.
            if (stmt.Offset is { } offset)
                materialized = materialized.Skip(offset).ToList();
            if (stmt.Limit is { } limit)
                materialized = materialized.Take(limit).ToList();

            // Projection.
            var outputColumns = SelectColumns(stmt, relation, columnNames);
            return new PgCatalogResponse(outputColumns, materialized);
        }

        private static IReadOnlyList<PgResultColumn> SelectColumns(
            PgSelectStatement stmt, PgCatalogRelation relation, HashSet<string> columnNames)
        {
            if (stmt.IsSelectStar)
                return relation.Columns;

            var selected = new List<PgResultColumn>();
            foreach (var reference in stmt.Columns)
            {
                if (!columnNames.Contains(reference.Name))
                    throw new PgQueryTranslationException(
                        $"pgwire: column \"{reference.Name}\" does not exist on the catalog relation.");

                selected.Add(relation.Columns.First(
                    c => string.Equals(c.Name, reference.Name, StringComparison.OrdinalIgnoreCase)));
            }

            return selected;
        }

        private static IEnumerable<IReadOnlyDictionary<string, object?>> ApplyOrder(
            IEnumerable<IReadOnlyDictionary<string, object?>> rows,
            IReadOnlyList<PgOrderTerm> orderBy,
            HashSet<string> columnNames)
        {
            IOrderedEnumerable<IReadOnlyDictionary<string, object?>>? ordered = null;
            foreach (var term in orderBy)
            {
                var column = RequireColumn(term.Column.Name, columnNames);
                Func<IReadOnlyDictionary<string, object?>, object?> key = r => Get(r, column);

                if (ordered is null)
                    ordered = term.Descending
                        ? rows.OrderByDescending(key, ValueComparer.Instance)
                        : rows.OrderBy(key, ValueComparer.Instance);
                else
                    ordered = term.Descending
                        ? ordered.ThenByDescending(key, ValueComparer.Instance)
                        : ordered.ThenBy(key, ValueComparer.Instance);
            }

            return ordered ?? rows;
        }

        private static bool Match(PgBoolExpr expr, IReadOnlyDictionary<string, object?> row, HashSet<string> columnNames)
        {
            switch (expr)
            {
                case PgBoolCombine combine:
                    return combine.IsAnd
                        ? combine.Terms.All(t => Match(t, row, columnNames))
                        : combine.Terms.Any(t => Match(t, row, columnNames));
                case PgPredicate predicate:
                    return MatchPredicate(predicate, row, columnNames);
                default:
                    throw new PgQueryTranslationException("pgwire: unsupported WHERE expression on a catalog relation.");
            }
        }

        private static bool MatchPredicate(PgPredicate predicate, IReadOnlyDictionary<string, object?> row, HashSet<string> columnNames)
        {
            var column = RequireColumn(predicate.Column.Name, columnNames);
            var value = Get(row, column);

            return predicate.Op switch
            {
                PgCompareOp.IsNull => value is null,
                PgCompareOp.IsNotNull => value is not null,
                PgCompareOp.Eq => ValueComparer.AreEqual(value, predicate.Value),
                PgCompareOp.Neq => !ValueComparer.AreEqual(value, predicate.Value),
                PgCompareOp.Lt => ValueComparer.Order(value, predicate.Value) < 0,
                PgCompareOp.Lte => ValueComparer.Order(value, predicate.Value) <= 0,
                PgCompareOp.Gt => ValueComparer.Order(value, predicate.Value) > 0,
                PgCompareOp.Gte => ValueComparer.Order(value, predicate.Value) >= 0,
                PgCompareOp.In => predicate.Values!.Any(v => ValueComparer.AreEqual(value, v)),
                PgCompareOp.Between => ValueComparer.Order(value, predicate.Values![0]) >= 0
                                       && ValueComparer.Order(value, predicate.Values![1]) <= 0,
                PgCompareOp.Like => Like(value, predicate.Value),
                _ => throw new PgQueryTranslationException("pgwire: unsupported comparison on a catalog relation."),
            };
        }

        private static bool Like(object? value, object? pattern)
        {
            if (value is null || pattern is null)
                return false;

            var regex = "^" + Regex.Escape(pattern.ToString() ?? "")
                .Replace("%", ".*").Replace("_", ".") + "$";
            return Regex.IsMatch(value.ToString() ?? "", regex);
        }

        private static string RequireColumn(string name, HashSet<string> columnNames)
        {
            if (!columnNames.Contains(name))
                throw new PgQueryTranslationException(
                    $"pgwire: column \"{name}\" does not exist on the catalog relation.");
            return name;
        }

        private static object? Get(IReadOnlyDictionary<string, object?> row, string column)
        {
            // Rows are keyed by the relation's own column names (built here); a
            // case-insensitive lookup mirrors pg's identifier folding.
            foreach (var kv in row)
                if (string.Equals(kv.Key, column, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        /// <summary>
        /// Compares synthesized catalog values against parsed SQL literals: numeric
        /// when both sides are numbers, else ordinal string comparison. NULLs sort
        /// last and never equal a non-null.
        /// </summary>
        private sealed class ValueComparer : IComparer<object?>
        {
            public static readonly ValueComparer Instance = new();

            public int Compare(object? x, object? y) => Order(x, y);

            public static bool AreEqual(object? a, object? b)
            {
                if (a is null || b is null) return a is null && b is null;
                if (TryNumeric(a, out var da) && TryNumeric(b, out var db)) return da == db;
                return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
            }

            public static int Order(object? a, object? b)
            {
                if (a is null && b is null) return 0;
                if (a is null) return 1;   // NULLs last
                if (b is null) return -1;
                if (TryNumeric(a, out var da) && TryNumeric(b, out var db)) return da.CompareTo(db);
                return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
            }

            private static bool TryNumeric(object value, out decimal number)
            {
                switch (value)
                {
                    case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                        number = Convert.ToDecimal(value);
                        return true;
                    default:
                        number = 0;
                        return false;
                }
            }
        }
    }
}

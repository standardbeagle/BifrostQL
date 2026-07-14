using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// The pgwire simple-query translator: parses the SAFE SQL subset
    /// (<see cref="PgSqlSubsetParser"/>), validates every table/column against the
    /// endpoint's <see cref="IDbModel"/>, and builds a PROGRAMMATIC
    /// <see cref="GqlObjectQuery"/> — the caller's SQL text is never rebuilt or
    /// forwarded. Because the query runs through <see cref="IQueryIntentExecutor"/>,
    /// the security transformer pipeline (tenant isolation, soft-delete, policy row
    /// scope) is applied unconditionally: an adapter has no API to skip it. WHERE
    /// literals become bound SQL parameters (never string-concatenated), so an
    /// injection attempt in a literal is data, not SQL. This replaces the slice-2
    /// thin recognizer with no change to the protocol loop or result encoders.
    /// </summary>
    internal sealed class PgSubsetQueryTranslator : IPgQueryTranslator
    {
        public async Task<PgQueryPlan> TranslateAsync(
            IQueryIntentExecutor executor,
            string sql,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var stmt = PgSqlSubsetParser.Parse(sql);
            var model = await executor.GetModelAsync(endpoint);
            var fromTable = ResolveTable(model, stmt.From.Name);

            // Resolve an optional single schema-relationship join into a forward
            // single-column FK link on the FROM table. A join that does not map to a
            // known single-link relationship is rejected — never guessed.
            var joinLink = stmt.Join is null ? null : ResolveJoin(model, fromTable, stmt);

            var scope = new QualifierScope(stmt.From, fromTable, stmt.Join, joinLink?.ConnectedTable);

            var query = new GqlObjectQuery
            {
                DbTable = fromTable,
                SchemaName = fromTable.TableSchema,
                TableName = fromTable.DbName,
                GraphQlName = fromTable.GraphQlName,
                Path = fromTable.GraphQlName,
            };

            var outputColumns = new List<PgResultColumn>();
            var joinNode = joinLink is null ? null : BuildJoinLink(query, joinLink);

            ProjectColumns(stmt, scope, query, joinNode, joinLink, outputColumns);

            if (stmt.Where is not null)
                query.Filter = TableFilter.FromObject(BuildFilter(stmt.Where, scope), fromTable.DbName);

            foreach (var term in stmt.OrderBy)
            {
                var col = ResolveRootColumn(term.Column, scope, "ORDER BY");
                query.Sort.Add($"{col.GraphQlName}{(term.Descending ? "_desc" : "_asc")}");
            }

            query.Limit = stmt.Limit;
            query.Offset = stmt.Offset;

            return new PgQueryPlan
            {
                Intent = new QueryIntent { Query = query, UserContext = userContext, Endpoint = endpoint },
                Columns = outputColumns,
            };
        }

        // ---- projection ------------------------------------------------------

        private static void ProjectColumns(
            PgSelectStatement stmt, QualifierScope scope, GqlObjectQuery query,
            GqlObjectQuery? joinNode, JoinResolution? joinLink, List<PgResultColumn> output)
        {
            if (stmt.IsSelectStar)
            {
                foreach (var c in scope.FromTable.Columns.OrderBy(c => c.OrdinalPosition))
                {
                    query.ScalarColumns.Add(new GqlObjectColumn(c.DbName));
                    output.Add(new PgResultColumn(c.DbName, c.DataType));
                }
                // A join's columns are only surfaced for '*' when the join was written,
                // qualified so they never collide with the base columns.
                if (joinNode is not null && joinLink is not null)
                    foreach (var c in joinLink.ConnectedTable.Columns.OrderBy(c => c.OrdinalPosition))
                        AddJoinColumn(joinNode, joinLink, c, output);
                return;
            }

            foreach (var reference in stmt.Columns)
            {
                var (table, column, isJoin) = scope.Resolve(reference, "SELECT");
                if (isJoin)
                {
                    if (joinNode is null || joinLink is null)
                        throw Reject($"column reference '{reference.Qualifier}.{reference.Name}' has no matching FROM/JOIN table.");
                    AddJoinColumn(joinNode, joinLink, column, output);
                }
                else
                {
                    query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
                    output.Add(new PgResultColumn(column.DbName, column.DataType));
                }
            }
        }

        /// <summary>
        /// Adds a joined column to the link's projection and to the pg output under a
        /// table-qualified key (<c>&lt;joinTable&gt;.&lt;col&gt;</c>). The Core intent
        /// flatten emits joined single-link columns under this exact key, so the
        /// RowDescription/DataRow projection finds them; qualification keeps them from
        /// colliding with a base column of the same name.
        /// </summary>
        private static void AddJoinColumn(GqlObjectQuery joinNode, JoinResolution joinLink, ColumnDto column, List<PgResultColumn> output)
        {
            if (joinNode.ScalarColumns.All(c => !string.Equals(c.GraphQlDbName, column.DbName, StringComparison.OrdinalIgnoreCase)))
                joinNode.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
            var key = $"{joinLink.ConnectedTable.DbName}.{column.DbName}";
            if (output.All(o => o.Name != key))
                output.Add(new PgResultColumn(key, column.DataType));
        }

        private static GqlObjectQuery BuildJoinLink(GqlObjectQuery root, JoinResolution join)
        {
            var link = new GqlObjectQuery
            {
                DbTable = join.ConnectedTable,
                SchemaName = join.ConnectedTable.TableSchema,
                TableName = join.ConnectedTable.DbName,
                GraphQlName = join.LinkFieldName,
                FieldName = join.LinkFieldName,
            };
            root.Links.Add(link);
            return link;
        }

        // ---- join resolution -------------------------------------------------

        private sealed record JoinResolution(IDbTable ConnectedTable, string LinkFieldName);

        /// <summary>
        /// Resolves a written JOIN to a forward single-column FK single-link on the
        /// FROM table. The join target must be the parent ("one") side of an existing
        /// single-link, the FK must be single-column, and the ON equality must name
        /// exactly that link's child FK and parent key columns. Anything else — a
        /// one-to-many/collection direction, a composite FK, an unrelated table, or an
        /// ON clause that doesn't match the relationship — is rejected honestly rather
        /// than guessed.
        /// </summary>
        private static JoinResolution ResolveJoin(IDbModel model, IDbTable fromTable, PgSelectStatement stmt)
        {
            var joinRef = stmt.Join!.Table;
            var target = ResolveTable(model, joinRef.Name);

            var candidates = fromTable.SingleLinks
                .Where(kv => string.Equals(kv.Value.ParentTable.DbName, target.DbName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                var isCollection = fromTable.MultiLinks.Values.Any(l =>
                    string.Equals(l.ChildTable.DbName, target.DbName, StringComparison.OrdinalIgnoreCase));
                throw RejectFeature(isCollection
                    ? $"JOIN to '{target.DbName}' is a one-to-many relationship; only forward (many-to-one) single-column FK joins are supported."
                    : $"no schema relationship connects '{fromTable.DbName}' to '{target.DbName}'.");
            }
            if (candidates.Count > 1)
                throw RejectFeature($"JOIN between '{fromTable.DbName}' and '{target.DbName}' is ambiguous (multiple relationships).");

            var (fieldName, link) = (candidates[0].Key, candidates[0].Value);
            if (link.IsComposite)
                throw RejectFeature($"JOIN to '{target.DbName}' uses a composite foreign key, which is not supported.");

            ValidateOnClause(stmt, fromTable, target, link);
            return new JoinResolution(target, fieldName);
        }

        /// <summary>Confirms the ON equality names the link's FK (child) and key (parent) columns.</summary>
        private static void ValidateOnClause(PgSelectStatement stmt, IDbTable fromTable, IDbTable target, TableLinkDto link)
        {
            var (childRef, parentRef) = MatchSides(stmt, fromTable, target);
            if (!ColumnMatches(childRef, link.ChildId) || !ColumnMatches(parentRef, link.ParentId))
                throw RejectFeature(
                    $"JOIN ON does not match the '{fromTable.DbName}'→'{target.DbName}' relationship " +
                    $"({link.ChildId.ColumnName} = {link.ParentId.ColumnName}).");
        }

        private static (PgColumnRef Child, PgColumnRef Parent) MatchSides(PgSelectStatement stmt, IDbTable fromTable, IDbTable target)
        {
            var join = stmt.Join!;
            // Either side of `=` may name the FROM or the JOIN table, by table name or alias.
            bool LeftIsFrom() => IsQualifierFor(join.Left.Qualifier, stmt.From, fromTable);
            bool RightIsTarget() => IsQualifierFor(join.Right.Qualifier, join.Table, target);
            bool RightIsFrom() => IsQualifierFor(join.Right.Qualifier, stmt.From, fromTable);
            bool LeftIsTarget() => IsQualifierFor(join.Left.Qualifier, join.Table, target);

            if (LeftIsFrom() && RightIsTarget()) return (join.Left, join.Right);
            if (RightIsFrom() && LeftIsTarget()) return (join.Right, join.Left);
            throw RejectFeature("JOIN ON must relate the FROM table to the joined table by their key columns.");
        }

        private static bool IsQualifierFor(string? qualifier, PgTableRef reference, IDbTable table) =>
            qualifier is null
            || string.Equals(qualifier, reference.Alias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(qualifier, table.DbName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(qualifier, table.GraphQlName, StringComparison.OrdinalIgnoreCase);

        private static bool ColumnMatches(PgColumnRef reference, ColumnDto column) =>
            string.Equals(reference.Name, column.DbName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reference.Name, column.GraphQlName, StringComparison.OrdinalIgnoreCase);

        // ---- WHERE → filter dictionary --------------------------------------

        private static Dictionary<string, object?> BuildFilter(PgBoolExpr expr, QualifierScope scope)
        {
            switch (expr)
            {
                case PgBoolCombine combine:
                    var key = combine.IsAnd ? "and" : "or";
                    return new Dictionary<string, object?>
                    {
                        [key] = combine.Terms.Select(t => (object?)BuildFilter(t, scope)).ToList(),
                    };
                case PgPredicate predicate:
                    return BuildLeaf(predicate, scope);
                default:
                    throw Reject("unsupported WHERE expression.");
            }
        }

        private static Dictionary<string, object?> BuildLeaf(PgPredicate predicate, QualifierScope scope)
        {
            var column = ResolveRootColumn(predicate.Column, scope, "WHERE");
            var opMap = predicate.Op switch
            {
                PgCompareOp.Eq => (FilterOperators.Eq, predicate.Value),
                PgCompareOp.Neq => (FilterOperators.Neq, predicate.Value),
                PgCompareOp.Lt => (FilterOperators.Lt, predicate.Value),
                PgCompareOp.Lte => (FilterOperators.Lte, predicate.Value),
                PgCompareOp.Gt => (FilterOperators.Gt, predicate.Value),
                PgCompareOp.Gte => (FilterOperators.Gte, predicate.Value),
                PgCompareOp.Like => (FilterOperators.Like, predicate.Value),
                PgCompareOp.In => (FilterOperators.In, (object?)predicate.Values!.ToList()),
                PgCompareOp.Between => (FilterOperators.Between, (object?)predicate.Values!.ToList()),
                PgCompareOp.IsNull => (FilterOperators.Null, (object?)true),
                PgCompareOp.IsNotNull => (FilterOperators.Null, (object?)false),
                _ => throw Reject("unsupported comparison operator."),
            };
            return new Dictionary<string, object?>
            {
                [column.GraphQlName] = new Dictionary<string, object?> { [opMap.Item1] = opMap.Item2 },
            };
        }

        // ---- shared resolution ----------------------------------------------

        private static ColumnDto ResolveRootColumn(PgColumnRef reference, QualifierScope scope, string clause)
        {
            var (_, column, isJoin) = scope.Resolve(reference, clause);
            if (isJoin)
                throw RejectFeature($"{clause} on joined-table columns is not supported; reference only the FROM table.");
            return column;
        }

        private static IDbTable ResolveTable(IDbModel model, string name)
        {
            var table = model.Tables.FirstOrDefault(t =>
                string.Equals(t.DbName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.GraphQlName, name, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                throw Reject($"relation \"{name}\" does not exist.");
            return table;
        }

        private static PgQueryTranslationException Reject(string detail) =>
            new($"pgwire: {detail}");

        private static PgQueryTranslationException RejectFeature(string detail) =>
            new($"pgwire: {detail}", PgWireProtocol.SqlStateFeatureNotSupported);

        /// <summary>
        /// Binds column qualifiers (table name or alias) to the FROM and optional
        /// JOIN tables and resolves a <see cref="PgColumnRef"/> to a concrete
        /// <see cref="ColumnDto"/>, flagging whether it belongs to the joined table.
        /// </summary>
        private sealed class QualifierScope
        {
            private readonly PgTableRef _fromRef;
            private readonly PgTableRef? _joinRef;
            private readonly IDbTable? _joinTable;

            public IDbTable FromTable { get; }

            public QualifierScope(PgTableRef fromRef, IDbTable fromTable, PgJoinClause? join, IDbTable? joinTable)
            {
                _fromRef = fromRef;
                FromTable = fromTable;
                _joinRef = join?.Table;
                _joinTable = joinTable;
            }

            public (IDbTable Table, ColumnDto Column, bool IsJoin) Resolve(PgColumnRef reference, string clause)
            {
                var q = reference.Qualifier;
                if (q is null)
                {
                    if (TryColumn(FromTable, reference.Name, out var fromCol))
                        return (FromTable, fromCol, false);
                    if (_joinTable is not null && TryColumn(_joinTable, reference.Name, out var joinCol))
                        return (_joinTable, joinCol, true);
                    throw Reject($"column \"{reference.Name}\" does not exist ({clause}).");
                }

                if (Matches(q, _fromRef, FromTable))
                    return (FromTable, RequireColumn(FromTable, reference.Name, clause), false);
                if (_joinTable is not null && _joinRef is not null && Matches(q, _joinRef, _joinTable))
                    return (_joinTable, RequireColumn(_joinTable, reference.Name, clause), true);

                throw Reject($"unknown table qualifier \"{q}\" ({clause}).");
            }

            private static bool Matches(string qualifier, PgTableRef reference, IDbTable table) =>
                string.Equals(qualifier, reference.Alias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(qualifier, table.DbName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(qualifier, table.GraphQlName, StringComparison.OrdinalIgnoreCase);

            private static ColumnDto RequireColumn(IDbTable table, string name, string clause)
            {
                if (TryColumn(table, name, out var col)) return col;
                throw Reject($"column \"{name}\" does not exist on relation \"{table.DbName}\" ({clause}).");
            }

            private static bool TryColumn(IDbTable table, string name, out ColumnDto column)
            {
                column = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.DbName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.GraphQlName, name, StringComparison.OrdinalIgnoreCase))!;
                return column is not null;
            }
        }
    }
}

using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// Public, validating fluent builder for <see cref="TableFilter"/>. The renderer
    /// (<see cref="TableFilter.ToSqlParameterized"/>) already supports comparison, IN,
    /// NULL, BETWEEN (range), OR, and single-link relationship predicates — but
    /// <c>TableFilter</c>'s constructor is <c>internal</c> and the only public factory
    /// (<see cref="Modules.TableFilterFactory"/>) builds just eq/in/null, so consumer
    /// modules could not construct the richer shapes the engine already renders. This
    /// builder closes that gap.
    ///
    /// Every predicate is validated at BUILD time against the target table: an unknown
    /// column, unknown single-link relationship, or unknown filter operator throws an
    /// actionable <see cref="ArgumentException"/> when the predicate is added — never a
    /// silent no-op and never a raw render-time <see cref="Resolvers.BifrostExecutionError"/>.
    /// Values are always carried as parameters; the builder never composes SQL text, so
    /// there is no literal-injection surface.
    /// </summary>
    public sealed class TableFilterBuilder
    {
        // The whole vocabulary FilterOperators exposes EXCEPT the table-scoped _search
        // (which is not a per-column operator and is surfaced separately). Used to reject
        // an unknown operator at build time.
        private static readonly IReadOnlySet<string> ColumnOperators = new HashSet<string>(StringComparer.Ordinal)
        {
            FilterOperators.Eq, FilterOperators.Neq,
            FilterOperators.Lt, FilterOperators.Lte, FilterOperators.Gt, FilterOperators.Gte,
            FilterOperators.Contains, FilterOperators.NContains,
            FilterOperators.StartsWith, FilterOperators.NStartsWith,
            FilterOperators.EndsWith, FilterOperators.NEndsWith,
            FilterOperators.Like, FilterOperators.NLike,
            FilterOperators.In, FilterOperators.NIn,
            FilterOperators.Between, FilterOperators.NBetween,
            FilterOperators.Null,
        };

        private readonly IDbTable _table;
        private readonly List<TableFilter> _predicates = new();

        private TableFilterBuilder(IDbTable table) => _table = table;

        /// <summary>Starts a builder whose predicates are validated against <paramref name="table"/>.</summary>
        public static TableFilterBuilder For(IDbTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return new TableFilterBuilder(table);
        }

        /// <summary>Adds a column comparison predicate (<c>_eq</c>, <c>_neq</c>, <c>_lt</c>,
        /// <c>_gt</c>, <c>_contains</c>, <c>_like</c>, …). Rejects an unknown column or operator.</summary>
        public TableFilterBuilder Compare(string column, string op, object? value)
        {
            ValidateColumn(_table, column);
            ValidateOperator(op);
            _predicates.Add(Leaf(column, op, value));
            return this;
        }

        /// <summary>Adds a <c>column _eq value</c> predicate (or <c>IS NULL</c> when <paramref name="value"/> is null).</summary>
        public TableFilterBuilder Equal(string column, object? value) => Compare(column, FilterOperators.Eq, value);

        /// <summary>Adds a <c>column IN (…)</c> predicate.</summary>
        public TableFilterBuilder In(string column, IEnumerable<object?> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            ValidateColumn(_table, column);
            _predicates.Add(Leaf(column, FilterOperators.In, values));
            return this;
        }

        /// <summary>Adds a <c>column IS NULL</c> (or <c>IS NOT NULL</c>) predicate.</summary>
        public TableFilterBuilder IsNull(string column, bool isNull = true)
        {
            ValidateColumn(_table, column);
            _predicates.Add(Leaf(column, isNull ? FilterOperators.Eq : FilterOperators.Neq, null));
            return this;
        }

        /// <summary>Adds a <c>column BETWEEN lower AND upper</c> range predicate.</summary>
        public TableFilterBuilder Between(string column, object? lower, object? upper)
        {
            ValidateColumn(_table, column);
            _predicates.Add(Leaf(column, FilterOperators.Between, new List<object?> { lower, upper }));
            return this;
        }

        /// <summary>
        /// Adds a single-link relationship predicate: rows are kept when the related row
        /// (reached via <paramref name="relationship"/>) satisfies
        /// <c>nestedColumn op value</c>. Rejects an unknown relationship, an unknown column
        /// on the related table, or an unknown operator.
        /// </summary>
        public TableFilterBuilder Related(string relationship, string nestedColumn, string op, object? value)
        {
            if (!_table.SingleLinks.TryGetValue(relationship, out var link))
                throw new ArgumentException(
                    $"Filter references unknown single-link relationship '{relationship}' on table '{_table.DbName}'.",
                    nameof(relationship));
            ValidateColumn(link.ParentTable, nestedColumn);
            ValidateOperator(op);
            _predicates.Add(new TableFilter
            {
                TableName = _table.DbName,
                ColumnName = relationship,
                FilterType = FilterType.Join,
                Next = new TableFilter
                {
                    ColumnName = nestedColumn,
                    FilterType = FilterType.Join,
                    Next = new TableFilter { RelationName = op, Value = value, FilterType = FilterType.Relation },
                },
            });
            return this;
        }

        /// <summary>
        /// Adds an OR group: the built filter matches when ANY of the <paramref name="branches"/>
        /// matches. Each branch receives a fresh sub-builder scoped to the same table and must
        /// contribute at least one predicate.
        /// </summary>
        public TableFilterBuilder Or(params Action<TableFilterBuilder>[] branches)
        {
            ArgumentNullException.ThrowIfNull(branches);
            if (branches.Length < 2)
                throw new ArgumentException("An OR group requires at least two branches.", nameof(branches));

            var orFilters = new List<TableFilter>();
            foreach (var branch in branches)
            {
                var sub = new TableFilterBuilder(_table);
                branch(sub);
                orFilters.Add(sub.BuildTree("An OR branch must contribute at least one predicate."));
            }
            _predicates.Add(new TableFilter { Or = orFilters, FilterType = FilterType.Or });
            return this;
        }

        /// <summary>Builds the composed <see cref="TableFilter"/>. Multiple predicates form an
        /// implicit AND. Throws when no predicate was added.</summary>
        public TableFilter Build() => BuildTree("A filter must contain at least one predicate.");

        private TableFilter BuildTree(string emptyMessage)
        {
            if (_predicates.Count == 0)
                throw new ArgumentException(emptyMessage);
            return _predicates.Count == 1
                ? _predicates[0]
                : new TableFilter { And = _predicates.ToList(), FilterType = FilterType.And };
        }

        private TableFilter Leaf(string column, string op, object? value) => new()
        {
            TableName = _table.DbName,
            ColumnName = column,
            FilterType = FilterType.Join,
            Next = new TableFilter { RelationName = op, Value = value, FilterType = FilterType.Relation },
        };

        private static void ValidateColumn(IDbTable table, string column)
        {
            if (string.IsNullOrWhiteSpace(column))
                throw new ArgumentException("Filter column name must be non-empty.", nameof(column));
            if (!table.GraphQlLookup.ContainsKey(column) && !table.ColumnLookup.ContainsKey(column))
                throw new ArgumentException(
                    $"Filter references unknown column '{column}' on table '{table.DbName}'.", nameof(column));
        }

        private static void ValidateOperator(string op)
        {
            if (!ColumnOperators.Contains(op))
                throw new ArgumentException(
                    $"Unknown filter operator '{op}'. Valid operators: {string.Join(", ", ColumnOperators)}.",
                    nameof(op));
        }
    }
}

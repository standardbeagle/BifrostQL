using BifrostQL.Core.Model;

namespace BifrostQL.Core.Auth;

/// <summary>
/// A table the identity may READ, carrying the columns it may read. The single shape every
/// schema-introspection surface (pgwire catalog, gRPC descriptors, OData <c>$metadata</c>, the
/// MCP schema tools, the app-metadata overlay) projects to before rendering its own wire format.
/// </summary>
public sealed record VisibleTable
{
    private readonly HashSet<string> _columnNames;
    private IReadOnlyList<ColumnDto>? _keyColumns;

    public VisibleTable(IDbTable table, IReadOnlyList<ColumnDto> columns)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));

        // Both names are indexed because callers address columns by either: the catalog/proto
        // surfaces use the DB name, the overlay and GraphQL-shaped surfaces the GraphQL name.
        // A column is in this set only if the evaluator ALLOWED it, so indexing both aliases
        // never widens visibility — it only stops a surface from missing a denial by naming
        // the column in its other spelling.
        _columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            _columnNames.Add(column.DbName);
            _columnNames.Add(column.GraphQlName);
        }
    }

    /// <summary>The underlying table. Its own column list is UNFILTERED — read <see cref="Columns"/>.</summary>
    public IDbTable Table { get; }

    /// <summary>The columns of <see cref="Table"/> this identity may read, in table order.</summary>
    public IReadOnlyList<ColumnDto> Columns { get; }

    /// <summary>
    /// True when <paramref name="name"/> names a readable column of this table, matched
    /// case-insensitively against either its DB name or its GraphQL name. This is the check a
    /// surface must apply at EVERY place a column name appears — display fields, grid columns,
    /// filters, sort keys, key lists, FK edge participants — not just where the column list
    /// itself is emitted, or the removed column is re-disclosed by the reference that names it.
    /// </summary>
    public bool HasColumn(string? name) => name is not null && _columnNames.Contains(name);

    /// <summary>True when every column in <paramref name="columns"/> is readable here.</summary>
    public bool HasColumns(IEnumerable<ColumnDto> columns) =>
        columns.All(c => HasColumn(c.DbName));

    /// <summary>
    /// The table's primary-key columns the caller may read. A key column hidden by a column
    /// read-deny is omitted rather than named, so the key shape never leaks a column the data
    /// path would refuse. Composite keys are returned in full — never a first-column guess.
    /// </summary>
    public IReadOnlyList<ColumnDto> KeyColumns =>
        _keyColumns ??= Table.KeyColumns.Where(c => HasColumn(c.DbName)).ToList();
}

/// <summary>
/// The single projection of an <see cref="IDbModel"/> down to what a caller may READ, shared by
/// every schema-introspection surface (.claude/rules/protocol-adapter-security.md invariant 4:
/// an introspection surface must be filtered by the SAME authorization as its data path,
/// fail-closed).
///
/// <para><b>It decides nothing itself.</b> It calls <see cref="PolicyEvaluator.CanAct"/> with
/// <see cref="PolicyAction.Read"/> over <see cref="PolicyConfigCollector.FromTable"/> for tables,
/// and <see cref="PolicyEvaluator.IsColumnAllowed"/> with <see cref="PolicyDirection.Read"/> for
/// columns, under the identity reconstructed by the shared
/// <see cref="PolicyIdentity.FromUserContext"/> — the same evaluator, over the same policy, that
/// the query path enforces. It is a single funnel, never a second, weaker "it's just metadata"
/// rule. Adapters render the result into their own catalog/proto/EDMX/tool-document/overlay
/// shape; no wire formatting lives here.</para>
///
/// <para><b>Fail closed.</b> A table whose policy cannot be parsed or evaluated is EXCLUDED —
/// even for an admin, since <see cref="PolicyConfigCollector.FromTable"/> throws before the
/// evaluator's admin bypass can run. A column whose evaluation faults is likewise excluded.</para>
///
/// <para><b>Visibility governs what is LISTED or DESCRIBED, not whether a named table
/// RESOLVES.</b> An adapter must still route a policy-denied table's data request through the
/// transformer pipeline so the caller receives the authoritative server-side rejection every
/// adapter raises for that condition (invariant 10). Using this projection to resolve a data
/// request would substitute a different, adapter-local condition for it.</para>
/// </summary>
public static class SchemaReadVisibility
{
    private static readonly PolicyEvaluator Evaluator = new();

    /// <summary>
    /// Every table of <paramref name="model"/>, with its readable columns, that the identity
    /// behind <paramref name="userContext"/> may read. A denied or unevaluable table is omitted
    /// entirely.
    /// </summary>
    public static IReadOnlyList<VisibleTable> Project(
        IDbModel model, IDictionary<string, object?> userContext)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));

        var identity = PolicyIdentity.FromUserContext(userContext);
        var result = new List<VisibleTable>();
        foreach (var table in model.Tables)
        {
            if (ProjectTable(model, table, identity) is { } visible)
                result.Add(visible);
        }
        return result;
    }

    /// <summary>
    /// The projection of a SINGLE table, or null when the caller may not read it — the same
    /// authoritative check <see cref="Project"/> applies, without walking the whole model.
    /// </summary>
    public static VisibleTable? ProjectTable(
        IDbTable table, IDictionary<string, object?> userContext)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));

        return ProjectTable(table, PolicyIdentity.FromUserContext(userContext));
    }

    /// <summary>
    /// Resolves a CLIENT-SUPPLIED table name within <paramref name="visible"/> by the rule
    /// <see cref="IDbModel.TryGetTableFromClientName"/> applies to the full model: a
    /// schema-qualified <c>schema.name</c> resolves exactly (split on the FIRST dot, so a dotted
    /// DB name stays reachable as <c>schema.dot.ted</c>), and a bare name resolves only when
    /// exactly one visible table carries it. An ambiguous bare name returns null, never an
    /// arbitrary pick. A policy-denied table returns null exactly like one that does not exist,
    /// so a caller's single "unknown table" answer covers unknown, ambiguous and denied alike
    /// (invariant 9) and the filter never becomes an existence ORACLE.
    ///
    /// <para>Ambiguity is decided over the VISIBLE set, not the model: a bare name shared with a
    /// table the caller may not read still resolves to the one it can, so the answer never
    /// depends on whether a hidden same-named table exists.</para>
    /// </summary>
    public static VisibleTable? FindByClientName(IReadOnlyCollection<VisibleTable> visible, string clientName)
    {
        if (visible is null) throw new ArgumentNullException(nameof(visible));
        if (clientName is null) throw new ArgumentNullException(nameof(clientName));

        var dot = clientName.IndexOf('.');
        if (dot > 0 && dot < clientName.Length - 1)
        {
            var schema = clientName[..dot];
            var dbName = clientName[(dot + 1)..];
            var qualified = visible.FirstOrDefault(v =>
                string.Equals(v.Table.TableSchema, schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.Table.DbName, dbName, StringComparison.OrdinalIgnoreCase));
            if (qualified is not null)
                return qualified;
        }

        VisibleTable? found = null;
        foreach (var candidate in visible)
        {
            if (!string.Equals(candidate.Table.DbName, clientName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found is not null)
                return null; // ambiguous bare name: a positive resolve is impossible
            found = candidate;
        }
        return found;
    }

    /// <summary>
    /// The name a client addresses <paramref name="table"/> by within <paramref name="visible"/>:
    /// its bare DB name when no other visible table shares it, else <c>schema.name</c>. Every
    /// name a surface LISTS for the caller to send back (resource URIs, did-you-mean lists) must
    /// round-trip through <see cref="FindByClientName"/>, and a bare name two visible tables
    /// share never does.
    /// </summary>
    public static string ClientName(IReadOnlyCollection<VisibleTable> visible, IDbTable table)
    {
        if (visible is null) throw new ArgumentNullException(nameof(visible));
        if (table is null) throw new ArgumentNullException(nameof(table));

        var shared = visible.Count(v =>
            string.Equals(v.Table.DbName, table.DbName, StringComparison.OrdinalIgnoreCase)) > 1;
        return shared ? $"{table.TableSchema}.{table.DbName}" : table.DbName;
    }

    /// <summary>
    /// The projection of <paramref name="table"/> within <paramref name="visible"/>, matched by
    /// schema-qualified name, or null when that table is not visible.
    /// </summary>
    public static VisibleTable? Find(IEnumerable<VisibleTable> visible, IDbTable table)
    {
        if (visible is null) throw new ArgumentNullException(nameof(visible));
        if (table is null) throw new ArgumentNullException(nameof(table));
        return visible.FirstOrDefault(v =>
            string.Equals(v.Table.DbName, table.DbName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Table.TableSchema, table.TableSchema, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when a foreign-key edge may be published: BOTH end tables are visible AND every
    /// participating column at both ends is visible. An edge is a two-ended fact — publishing it
    /// for a hidden parent would disclose the hidden table's name and key columns through the
    /// child's metadata, exactly the leak the projection removed on the direct path — so an edge
    /// naming anything hidden is dropped rather than trimmed.
    /// </summary>
    public static bool IsLinkVisible(TableLinkDto link, IEnumerable<VisibleTable> visible)
    {
        if (link is null) throw new ArgumentNullException(nameof(link));
        if (visible is null) throw new ArgumentNullException(nameof(visible));

        var candidates = visible as IReadOnlyCollection<VisibleTable> ?? visible.ToList();
        return Find(candidates, link.ChildTable) is { } child
            && Find(candidates, link.ParentTable) is { } parent
            && child.HasColumns(link.ChildIds)
            && parent.HasColumns(link.ParentIds);
    }

    /// <summary>
    /// True when a many-to-many bridge may be published: its source, junction and target
    /// tables are visible and so are the four columns it names. The same two-ended rule as
    /// <see cref="IsLinkVisible"/>: a bridge naming a hidden column is dropped, not trimmed.
    /// </summary>
    public static bool IsManyToManyVisible(ManyToManyLink link, IEnumerable<VisibleTable> visible)
    {
        if (link is null) throw new ArgumentNullException(nameof(link));
        if (visible is null) throw new ArgumentNullException(nameof(visible));

        var candidates = visible as IReadOnlyCollection<VisibleTable> ?? visible.ToList();
        return Find(candidates, link.SourceTable) is { } source
            && Find(candidates, link.JunctionTable) is { } junction
            && Find(candidates, link.TargetTable) is { } target
            && source.HasColumn(link.SourceColumn.DbName)
            && junction.HasColumn(link.JunctionSourceColumn.DbName)
            && junction.HasColumn(link.JunctionTargetColumn.DbName)
            && target.HasColumn(link.TargetColumn.DbName);
    }

    private static VisibleTable? ProjectTable(IDbTable table, AppIdentity identity)
        => ProjectTable(null, table, identity);

    private static VisibleTable? ProjectTable(IDbModel? model, IDbTable table, AppIdentity identity)
    {
        TablePolicy policy;
        try
        {
            policy = PolicyConfigCollector.FromTable(table);
            if (!Evaluator.CanAct(policy, PolicyAction.Read, identity).Allowed)
                return null;
        }
        catch (Exception ex)
        {
            // Fail closed: a table whose policy cannot be parsed or evaluated is hidden, never
            // included on a benefit-of-the-doubt basis — and never for admin either, because
            // FromTable throws before the evaluator's admin bypass can run. Model validation
            // rejects an unparseable policy at load, so reaching here is a bug: log it.
            Resolvers.BifrostErrorSink.Handled(nameof(SchemaReadVisibility),
                $"Hid table {table.TableSchema}.{table.DbName} from the schema: its read policy failed to evaluate", ex);
            return null;
        }

        return new VisibleTable(table, VisibleColumns(table, policy, identity));
    }


    private static IReadOnlyList<ColumnDto> VisibleColumns(
        IDbTable table, TablePolicy policy, AppIdentity identity)
    {
        // Blind-index shadow columns are server-derived and unreadable
        // (BlindIndexColumnGuard); listing them in a catalog would advertise a
        // column every SELECT of which is denied — and name the token column an
        // equality-correlation probe would target.
        var blindIndexTargets = BlindIndexColumns.TargetsOf(table);

        var result = new List<ColumnDto>();
        foreach (var column in table.Columns)
        {
            if (blindIndexTargets.Contains(column.DbName))
                continue;
            bool allowed;
            try
            {
                allowed = Evaluator.GetReadDisposition(policy, column.DbName, identity)
                    != ReadColumnDisposition.Refuse;
            }
            catch (Exception ex)
            {
                allowed = false; // fail closed on any column-evaluation fault
                Resolvers.BifrostErrorSink.Handled(nameof(SchemaReadVisibility),
                    $"Hid column {table.TableSchema}.{table.DbName}.{column.DbName} from the schema: its read policy failed to evaluate", ex);
            }

            if (allowed)
                result.Add(column);
        }

        return result;
    }
}

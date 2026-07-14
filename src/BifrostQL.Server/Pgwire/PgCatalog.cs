using BifrostQL.Core.Model;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// One PostgreSQL type as it appears in <c>pg_type</c> and drives the
    /// <c>data_type</c> string in <c>information_schema.columns</c>: the catalog OID
    /// (shared with <see cref="PgTypeMap"/>), the <c>pg_type.typname</c>, the SQL
    /// standard type name <c>information_schema</c> reports, and the on-wire length
    /// (<c>-1</c> for variable-length).
    /// </summary>
    internal readonly record struct PgCatalogType(int Oid, string TypName, string SqlName, short TypLen);

    /// <summary>
    /// A visible table plus the caller-visible subset of its columns, already
    /// filtered by <see cref="PgCatalogVisibility"/>. Carries the stable catalog
    /// OIDs the emulated relations cross-reference (a <c>pg_class</c> row's oid, its
    /// <c>relnamespace</c>) so <c>pg_attribute.attrelid</c> and
    /// <c>pg_namespace.oid</c> line up within one session.
    /// </summary>
    internal sealed record PgCatalogTable(
        IDbTable Table,
        IReadOnlyList<ColumnDto> Columns,
        int ClassOid,
        int NamespaceOid);

    /// <summary>
    /// A materialized emulated catalog relation: its ordered wire columns and the
    /// rows (column-name → value) derived from the DbModel. The rows are already
    /// identity-filtered; <see cref="PgCatalogResponder"/> applies any WHERE /
    /// projection / ORDER BY on top.
    /// </summary>
    internal sealed record PgCatalogRelation(
        IReadOnlyList<PgResultColumn> Columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

    /// <summary>
    /// The catalog relations the pgwire front door emulates for <c>psql \d</c> and
    /// basic BI-tool introspection. Synthesizes <c>pg_namespace</c>, <c>pg_class</c>,
    /// <c>pg_attribute</c>, <c>pg_type</c>, <c>information_schema.tables</c> and
    /// <c>information_schema.columns</c> purely from the endpoint's <see cref="IDbModel"/>
    /// — there is no physical catalog to read. Rows are DERIVED, never fabricated
    /// with placeholder data: every table/column/type row corresponds to a real
    /// model element the caller may see. Type OIDs reuse <see cref="PgTypeMap"/>, so
    /// the type a column advertises in a normal SELECT matches the type the catalog
    /// reports for it.
    /// </summary>
    internal static class PgCatalog
    {
        /// <summary>Which emulated relation a <c>schema.name</c> reference names, if any.</summary>
        internal enum RelationKind { PgNamespace, PgClass, PgAttribute, PgType, InfoTables, InfoColumns }

        // Constant, deployment-neutral catalog scalars. A single synthetic owner /
        // database name is honest for an emulated catalog that has no real roles.
        private const string CatalogName = "bifrost";
        private const int SyntheticOwner = 10;         // pg's bootstrap superuser oid
        private const int PgCatalogNamespaceOid = 11;  // pg's fixed pg_catalog namespace oid

        // ---- type table -----------------------------------------------------

        /// <summary>
        /// The fixed pg types the query path can advertise (OIDs from
        /// <see cref="PgTypeMap"/>). This is the whole of the emulated <c>pg_type</c>.
        /// </summary>
        private static readonly IReadOnlyList<PgCatalogType> Types = new[]
        {
            new PgCatalogType(PgTypeMap.OidBool, "bool", "boolean", 1),
            new PgCatalogType(PgTypeMap.OidInt8, "int8", "bigint", 8),
            new PgCatalogType(PgTypeMap.OidInt2, "int2", "smallint", 2),
            new PgCatalogType(PgTypeMap.OidInt4, "int4", "integer", 4),
            new PgCatalogType(PgTypeMap.OidText, "text", "text", -1),
            new PgCatalogType(PgTypeMap.OidFloat4, "float4", "real", 4),
            new PgCatalogType(PgTypeMap.OidFloat8, "float8", "double precision", 8),
            new PgCatalogType(PgTypeMap.OidVarchar, "varchar", "character varying", -1),
            new PgCatalogType(PgTypeMap.OidDate, "date", "date", 4),
            new PgCatalogType(PgTypeMap.OidTimestamp, "timestamp", "timestamp without time zone", 8),
            new PgCatalogType(PgTypeMap.OidNumeric, "numeric", "numeric", -1),
            new PgCatalogType(PgTypeMap.OidUuid, "uuid", "uuid", 16),
        };

        private static readonly IReadOnlyDictionary<int, PgCatalogType> TypesByOid =
            Types.ToDictionary(t => t.Oid);

        /// <summary>Resolves a Bifrost/SQL data-type string to its emulated pg type.</summary>
        private static PgCatalogType ForDataType(string? dataType)
        {
            var oid = PgTypeMap.Map(dataType).Oid;
            // Every OID PgTypeMap can return is in the table above (text is the fallback).
            return TypesByOid.TryGetValue(oid, out var type) ? type : TypesByOid[PgTypeMap.OidText];
        }

        // ---- OID + relation resolution --------------------------------------

        /// <summary>
        /// A stable, deterministic positive OID for a catalog key (FNV-1a). Determinism
        /// matters so a class oid emitted in <c>pg_class</c> equals the <c>attrelid</c>
        /// emitted in <c>pg_attribute</c> for the same table, letting a client correlate
        /// the two relations across separate queries in a session.
        /// </summary>
        internal static int StableOid(string key)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var c in key)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                // Fold to a positive int, avoiding the low reserved range so a synthetic
                // oid never collides with pg's bootstrap oids (<10000).
                return (int)(hash & 0x7FFFFFFF | 0x40000000);
            }
        }

        /// <summary>
        /// Resolves a <c>schema.name</c> table reference to the emulated relation it
        /// names, or null when it is not a catalog relation this adapter emulates.
        /// A reference in the <c>pg_catalog</c>/<c>information_schema</c> namespace (or a
        /// bare <c>pg_*</c> relation) that is NOT emulated resolves to null too; the
        /// responder turns that into a clean "not emulated" error rather than guessing.
        /// </summary>
        internal static RelationKind? ResolveRelation(string? schema, string name)
        {
            var s = schema?.ToLowerInvariant();
            var n = name.ToLowerInvariant();

            if (s == "information_schema")
                return n switch
                {
                    "tables" => RelationKind.InfoTables,
                    "columns" => RelationKind.InfoColumns,
                    _ => null,
                };

            // pg_catalog-qualified or bare pg_* relation.
            if (s is null or "pg_catalog")
                return n switch
                {
                    "pg_namespace" => RelationKind.PgNamespace,
                    "pg_class" => RelationKind.PgClass,
                    "pg_attribute" => RelationKind.PgAttribute,
                    "pg_type" => RelationKind.PgType,
                    _ => null,
                };

            return null;
        }

        // ---- relation builders ----------------------------------------------

        /// <summary>Builds the requested emulated relation from the visible table set.</summary>
        internal static PgCatalogRelation Build(RelationKind kind, IReadOnlyList<PgCatalogTable> visible) => kind switch
        {
            RelationKind.PgNamespace => BuildPgNamespace(visible),
            RelationKind.PgClass => BuildPgClass(visible),
            RelationKind.PgAttribute => BuildPgAttribute(visible),
            RelationKind.PgType => BuildPgType(),
            RelationKind.InfoTables => BuildInfoTables(visible),
            RelationKind.InfoColumns => BuildInfoColumns(visible),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        private static PgCatalogRelation BuildPgNamespace(IReadOnlyList<PgCatalogTable> visible)
        {
            var columns = new[]
            {
                new PgResultColumn("oid", "int"),
                new PgResultColumn("nspname", "varchar"),
                new PgResultColumn("nspowner", "int"),
            };

            // One namespace row per distinct schema among the visible tables.
            var rows = visible
                .GroupBy(t => t.NamespaceOid)
                .Select(g => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["oid"] = g.Key,
                    ["nspname"] = g.First().Table.TableSchema,
                    ["nspowner"] = SyntheticOwner,
                })
                .ToList();

            return new PgCatalogRelation(columns, rows);
        }

        private static PgCatalogRelation BuildPgClass(IReadOnlyList<PgCatalogTable> visible)
        {
            var columns = new[]
            {
                new PgResultColumn("oid", "int"),
                new PgResultColumn("relname", "varchar"),
                new PgResultColumn("relnamespace", "int"),
                new PgResultColumn("relkind", "char"),
                new PgResultColumn("relnatts", "smallint"),
                new PgResultColumn("relowner", "int"),
                new PgResultColumn("reltuples", "real"),
                new PgResultColumn("relhasindex", "bool"),
                new PgResultColumn("relpersistence", "char"),
            };

            var rows = visible
                .Select(t => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["oid"] = t.ClassOid,
                    ["relname"] = t.Table.DbName,
                    ["relnamespace"] = t.NamespaceOid,
                    ["relkind"] = "r",                 // ordinary table
                    ["relnatts"] = (short)t.Columns.Count,
                    ["relowner"] = SyntheticOwner,
                    ["reltuples"] = -1f,               // unknown; pg uses -1 for "never analyzed"
                    ["relhasindex"] = t.Columns.Any(c => c.IsPrimaryKey),
                    ["relpersistence"] = "p",          // permanent
                })
                .ToList();

            return new PgCatalogRelation(columns, rows);
        }

        private static PgCatalogRelation BuildPgAttribute(IReadOnlyList<PgCatalogTable> visible)
        {
            var columns = new[]
            {
                new PgResultColumn("attrelid", "int"),
                new PgResultColumn("attname", "varchar"),
                new PgResultColumn("atttypid", "int"),
                new PgResultColumn("attnum", "smallint"),
                new PgResultColumn("attnotnull", "bool"),
                new PgResultColumn("atttypmod", "int"),
                new PgResultColumn("attlen", "smallint"),
                new PgResultColumn("atthasdef", "bool"),
                new PgResultColumn("attisdropped", "bool"),
            };

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var t in visible)
            {
                foreach (var c in t.Columns.OrderBy(c => c.OrdinalPosition))
                {
                    var type = ForDataType(c.DataType);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["attrelid"] = t.ClassOid,
                        ["attname"] = c.DbName,
                        ["atttypid"] = type.Oid,
                        ["attnum"] = (short)c.OrdinalPosition,
                        ["attnotnull"] = !c.IsNullable,
                        ["atttypmod"] = -1,
                        ["attlen"] = type.TypLen,
                        ["atthasdef"] = false,
                        ["attisdropped"] = false,
                    });
                }
            }

            return new PgCatalogRelation(columns, rows);
        }

        private static PgCatalogRelation BuildPgType()
        {
            var columns = new[]
            {
                new PgResultColumn("oid", "int"),
                new PgResultColumn("typname", "varchar"),
                new PgResultColumn("typlen", "smallint"),
                new PgResultColumn("typtype", "char"),
                new PgResultColumn("typnamespace", "int"),
            };

            // pg_type is the fixed set of advertised types — not identity-dependent.
            var rows = Types
                .Select(type => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["oid"] = type.Oid,
                    ["typname"] = type.TypName,
                    ["typlen"] = type.TypLen,
                    ["typtype"] = "b",                 // base type
                    ["typnamespace"] = PgCatalogNamespaceOid,
                })
                .ToList();

            return new PgCatalogRelation(columns, rows);
        }

        private static PgCatalogRelation BuildInfoTables(IReadOnlyList<PgCatalogTable> visible)
        {
            var columns = new[]
            {
                new PgResultColumn("table_catalog", "varchar"),
                new PgResultColumn("table_schema", "varchar"),
                new PgResultColumn("table_name", "varchar"),
                new PgResultColumn("table_type", "varchar"),
            };

            var rows = visible
                .Select(t => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["table_catalog"] = CatalogName,
                    ["table_schema"] = t.Table.TableSchema,
                    ["table_name"] = t.Table.DbName,
                    ["table_type"] = "BASE TABLE",
                })
                .ToList();

            return new PgCatalogRelation(columns, rows);
        }

        private static PgCatalogRelation BuildInfoColumns(IReadOnlyList<PgCatalogTable> visible)
        {
            var columns = new[]
            {
                new PgResultColumn("table_catalog", "varchar"),
                new PgResultColumn("table_schema", "varchar"),
                new PgResultColumn("table_name", "varchar"),
                new PgResultColumn("column_name", "varchar"),
                new PgResultColumn("ordinal_position", "int"),
                new PgResultColumn("is_nullable", "varchar"),
                new PgResultColumn("data_type", "varchar"),
                new PgResultColumn("udt_name", "varchar"),
            };

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var t in visible)
            {
                foreach (var c in t.Columns.OrderBy(c => c.OrdinalPosition))
                {
                    var type = ForDataType(c.DataType);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["table_catalog"] = CatalogName,
                        ["table_schema"] = t.Table.TableSchema,
                        ["table_name"] = t.Table.DbName,
                        ["column_name"] = c.DbName,
                        ["ordinal_position"] = c.OrdinalPosition,
                        ["is_nullable"] = c.IsNullable ? "YES" : "NO",
                        ["data_type"] = type.SqlName,
                        ["udt_name"] = type.TypName,
                    });
                }
            }

            return new PgCatalogRelation(columns, rows);
        }
    }
}

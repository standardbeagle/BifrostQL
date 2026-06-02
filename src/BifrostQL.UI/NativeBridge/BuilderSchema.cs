using BifrostQL.Core.Model;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>A table available to the visual query builder.</summary>
    public sealed record BuilderTableInfo(string Schema, string Name, string Qualified);

    /// <summary>A column available to the visual query builder.</summary>
    public sealed record BuilderColumnInfo(
        string Table, string Name, string Type, bool Nullable, bool IsPrimaryKey);

    /// <summary>An FK relationship between two tables. Column lists are parallel
    /// (left[i] references right[i]) so composite foreign keys carry every pair.</summary>
    public sealed record BuilderRelationshipInfo(
        string LeftTable, IReadOnlyList<string> LeftColumns,
        string RightTable, IReadOnlyList<string> RightColumns);

    /// <summary>A many-to-many bridge between two tables through a junction table.
    /// The designer uses this to offer a "join through {junction}" that wires both
    /// FK hops at once. Column lists are parallel for composite-key safety.</summary>
    public sealed record BuilderManyToManyInfo(
        string SourceTable, IReadOnlyList<string> SourceColumns,
        string JunctionTable,
        IReadOnlyList<string> JunctionSourceColumns,
        IReadOnlyList<string> JunctionTargetColumns,
        string TargetTable, IReadOnlyList<string> TargetColumns);

    /// <summary>The payload returned by the <c>get-builder-schema</c> bridge handler.</summary>
    public sealed record BuilderSchemaDto(
        IReadOnlyList<BuilderTableInfo> Tables,
        IReadOnlyList<BuilderColumnInfo> Columns,
        IReadOnlyList<BuilderRelationshipInfo> Relationships,
        IReadOnlyList<BuilderManyToManyInfo> ManyToMany);

    /// <summary>
    /// Projects an <see cref="IDbModel"/> into the flat tables/columns/relationships
    /// shape the React query designer consumes. Pure and side-effect-free so it can
    /// be unit-tested against any model. Serialized camelCase by the native bridge,
    /// matching the TypeScript <c>BuilderSchema</c> mirror.
    /// </summary>
    public static class BuilderSchemaProjection
    {
        public static BuilderSchemaDto Project(IDbModel model)
        {
            ArgumentNullException.ThrowIfNull(model);

            var tables = model.Tables
                .Select(t => new BuilderTableInfo(t.TableSchema, t.DbName, Qualified(t)))
                .ToList();

            var columns = model.Tables
                .SelectMany(t => t.Columns.Select(c => new BuilderColumnInfo(
                    Qualified(t), c.DbName, c.DataType, c.IsNullable, c.IsPrimaryKey)))
                .ToList();

            // SingleLinks are the child->parent FK edges and are composite-safe via
            // ChildIds/ParentIds. MultiLinks are the reverse of the same FKs, so
            // projecting only SingleLinks avoids emitting each relationship twice.
            var relationships = model.Tables
                .SelectMany(t => t.SingleLinks.Values.Select(link => new BuilderRelationshipInfo(
                    Qualified(link.ChildTable),
                    link.ChildIds.Select(c => c.DbName).ToList(),
                    Qualified(link.ParentTable),
                    link.ParentIds.Select(c => c.DbName).ToList())))
                .ToList();

            var manyToMany = model.Tables
                .SelectMany(t => t.ManyToManyLinks.Values.Select(m => new BuilderManyToManyInfo(
                    Qualified(m.SourceTable),
                    new[] { m.SourceColumn.DbName },
                    Qualified(m.JunctionTable),
                    new[] { m.JunctionSourceColumn.DbName },
                    new[] { m.JunctionTargetColumn.DbName },
                    Qualified(m.TargetTable),
                    new[] { m.TargetColumn.DbName })))
                .ToList();

            return new BuilderSchemaDto(tables, columns, relationships, manyToMany);
        }

        private static string Qualified(IDbTable t) =>
            string.IsNullOrEmpty(t.TableSchema) ? t.DbName : $"{t.TableSchema}.{t.DbName}";
    }
}

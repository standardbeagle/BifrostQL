namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Represents a foreign key constraint read from database metadata.
    /// Supports single-column and composite (multi-column) foreign keys.
    /// </summary>
    public sealed class DbForeignKey
    {
        /// <summary>The constraint name as defined in the database (e.g., "FK_Orders_Users").</summary>
        public string ConstraintName { get; init; } = null!;

        /// <summary>Schema of the parent (referenced) table.</summary>
        public string ParentTableSchema { get; init; } = null!;

        /// <summary>Name of the parent (referenced) table - the "one" side.</summary>
        public string ParentTableName { get; init; } = null!;

        /// <summary>Column names in the parent table, ordered to match <see cref="ChildColumnNames"/>.</summary>
        public IReadOnlyList<string> ParentColumnNames { get; init; } = null!;

        /// <summary>Schema of the child (referencing) table.</summary>
        public string ChildTableSchema { get; init; } = null!;

        /// <summary>Name of the child (referencing) table - the "many" side.</summary>
        public string ChildTableName { get; init; } = null!;

        /// <summary>Column names in the child table, ordered to match <see cref="ParentColumnNames"/>.</summary>
        public IReadOnlyList<string> ChildColumnNames { get; init; } = null!;

        /// <summary>True when <see cref="ParentColumnNames"/> has more than one entry.</summary>
        public bool IsComposite => ParentColumnNames.Count > 1;

        public override string ToString() =>
            $"FK[{ConstraintName}] {ChildTableSchema}.{ChildTableName}({string.Join(", ", ChildColumnNames)}) -> {ParentTableSchema}.{ParentTableName}({string.Join(", ", ParentColumnNames)})";
    }
}

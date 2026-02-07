using BifrostQL.Core.Model;
using BifrostQL.Model;
using Pluralize.NET.Core;

namespace BifrostQL.Core.QueryModel.TestFixtures;

/// <summary>
/// Fluent builder for creating test IDbModel instances with minimal setup.
/// Reduces test setup from 100+ lines to ~10 lines.
/// </summary>
public sealed class DbModelTestFixture
{
    private readonly Dictionary<string, DbTable> _tables = new();
    private readonly List<(string childTable, string childColumn, string parentTable, string parentColumn, string linkName)> _singleLinks = new();
    private readonly List<(string parentTable, string parentColumn, string childTable, string childColumn, string linkName)> _multiLinks = new();
    private readonly List<DbForeignKey> _foreignKeys = new();
    private readonly Dictionary<string, object?> _modelMetadata = new();

    public static DbModelTestFixture Create() => new();

    public DbModelTestFixture WithModelMetadata(string key, object? value)
    {
        _modelMetadata[key] = value;
        return this;
    }

    public DbModelTestFixture WithTable(string tableName, Action<TableBuilder> configure)
    {
        var builder = new TableBuilder(tableName);
        configure(builder);
        _tables[tableName] = builder.Build();
        return this;
    }

    public DbModelTestFixture WithSingleLink(string childTable, string childColumn, string parentTable, string parentColumn, string? linkName = null)
    {
        _singleLinks.Add((childTable, childColumn, parentTable, parentColumn, linkName ?? parentTable));
        return this;
    }

    public DbModelTestFixture WithMultiLink(string parentTable, string parentColumn, string childTable, string childColumn, string? linkName = null)
    {
        _multiLinks.Add((parentTable, parentColumn, childTable, childColumn, linkName ?? childTable));
        return this;
    }

    public DbModelTestFixture WithManyToManyLink(
        string sourceTable, string sourceColumn,
        string junctionTable, string junctionSourceColumn, string junctionTargetColumn,
        string targetTable, string targetColumn,
        string? linkName = null)
    {
        if (!_tables.TryGetValue(sourceTable, out var source))
            throw new InvalidOperationException($"Source table '{sourceTable}' not found");
        if (!_tables.TryGetValue(junctionTable, out var junction))
            throw new InvalidOperationException($"Junction table '{junctionTable}' not found");
        if (!_tables.TryGetValue(targetTable, out var target))
            throw new InvalidOperationException($"Target table '{targetTable}' not found");

        var link = new ManyToManyLink
        {
            Name = linkName ?? target.GraphQlName,
            SourceTable = source,
            JunctionTable = junction,
            TargetTable = target,
            SourceColumn = source.ColumnLookup[sourceColumn],
            JunctionSourceColumn = junction.ColumnLookup[junctionSourceColumn],
            JunctionTargetColumn = junction.ColumnLookup[junctionTargetColumn],
            TargetColumn = target.ColumnLookup[targetColumn],
        };
        source.ManyToManyLinks[linkName ?? target.GraphQlName] = link;
        return this;
    }

    public DbModelTestFixture WithForeignKey(
        string constraintName,
        string childTableSchema, string childTableName, IReadOnlyList<string> childColumnNames,
        string parentTableSchema, string parentTableName, IReadOnlyList<string> parentColumnNames)
    {
        _foreignKeys.Add(new DbForeignKey
        {
            ConstraintName = constraintName,
            ChildTableSchema = childTableSchema,
            ChildTableName = childTableName,
            ChildColumnNames = childColumnNames,
            ParentTableSchema = parentTableSchema,
            ParentTableName = parentTableName,
            ParentColumnNames = parentColumnNames,
        });
        return this;
    }

    public DbModelTestFixture WithForeignKey(
        string constraintName,
        string childTableName, string childColumnName,
        string parentTableName, string parentColumnName,
        string schema = "dbo")
    {
        return WithForeignKey(
            constraintName,
            schema, childTableName, new[] { childColumnName },
            schema, parentTableName, new[] { parentColumnName });
    }

    public IDbModel Build()
    {
        if (_foreignKeys.Count > 0)
            return BuildWithForeignKeys();

        // Connect single links (child -> parent: ManyToOne)
        foreach (var (childTable, childColumn, parentTable, parentColumn, linkName) in _singleLinks)
        {
            if (!_tables.TryGetValue(childTable, out var child))
                throw new InvalidOperationException($"Child table '{childTable}' not found");
            if (!_tables.TryGetValue(parentTable, out var parent))
                throw new InvalidOperationException($"Parent table '{parentTable}' not found");

            var link = new TableLinkDto
            {
                Name = $"{childTable}->{parentTable}",
                ChildTable = child,
                ChildId = child.ColumnLookup[childColumn],
                ParentTable = parent,
                ParentId = parent.ColumnLookup[parentColumn],
            };
            child.SingleLinks[linkName] = link;
        }

        // Connect multi links (parent -> children: OneToMany)
        foreach (var (parentTable, parentColumn, childTable, childColumn, linkName) in _multiLinks)
        {
            if (!_tables.TryGetValue(parentTable, out var parent))
                throw new InvalidOperationException($"Parent table '{parentTable}' not found");
            if (!_tables.TryGetValue(childTable, out var child))
                throw new InvalidOperationException($"Child table '{childTable}' not found");

            var link = new TableLinkDto
            {
                Name = $"{parentTable}->{childTable}",
                ParentTable = parent,
                ParentId = parent.ColumnLookup[parentColumn],
                ChildTable = child,
                ChildId = child.ColumnLookup[childColumn],
            };
            parent.MultiLinks[linkName] = link;
        }

        return new TestDbModel(_tables, _modelMetadata);
    }

    private IDbModel BuildWithForeignKeys()
    {
        var metadataLoader = new NoOpMetadataLoader(_modelMetadata);
        return DbModel.FromTables(
            _tables.Values.ToList(),
            metadataLoader,
            Array.Empty<DbStoredProcedure>(),
            _foreignKeys);
    }

    public sealed class TableBuilder
    {
        private readonly string _tableName;
        private string? _schema;
        private string? _graphQlName;
        private readonly Dictionary<string, ColumnDto> _columns = new();
        private readonly Dictionary<string, object?> _metadata = new();

        public TableBuilder(string tableName)
        {
            _tableName = tableName;
        }

        public TableBuilder WithSchema(string schema)
        {
            _schema = schema;
            return this;
        }

        public TableBuilder WithGraphQlName(string name)
        {
            _graphQlName = name;
            return this;
        }

        public TableBuilder WithMetadata(string key, object? value)
        {
            _metadata[key] = value;
            return this;
        }

        public TableBuilder WithColumn(string name, string dataType = "nvarchar", bool isPrimaryKey = false, bool isNullable = false, string? graphQlName = null)
        {
            _columns[name] = new ColumnDto
            {
                ColumnName = name,
                GraphQlName = graphQlName ?? name,
                NormalizedName = NormalizeColumn(name),
                DataType = dataType,
                IsPrimaryKey = isPrimaryKey,
                IsNullable = isNullable,
            };
            return this;
        }

        private static string NormalizeColumn(string column)
        {
            if (string.Equals("id", column, StringComparison.InvariantCultureIgnoreCase))
                return "id";
            if (column.EndsWith("id", StringComparison.InvariantCultureIgnoreCase))
            {
                var tableName = column.Substring(0, column.Length - 2);
                return new Pluralizer().Singularize(tableName);
            }
            return column;
        }

        public TableBuilder WithColumnMetadata(string columnName, string key, object? value)
        {
            if (!_columns.TryGetValue(columnName, out var column))
                throw new InvalidOperationException($"Column '{columnName}' not found. Call WithColumn first.");
            column.Metadata[key] = value;
            return this;
        }

        public TableBuilder WithPrimaryKey(string name, string dataType = "int")
        {
            return WithColumn(name, dataType, isPrimaryKey: true);
        }

        public DbTable Build()
        {
            var table = new DbTable
            {
                DbName = _tableName,
                GraphQlName = _graphQlName ?? _tableName,
                NormalizedName = new Pluralizer().Singularize(_tableName),
                TableSchema = _schema ?? string.Empty,
                ColumnLookup = _columns,
                GraphQlLookup = _columns.Values.ToDictionary(c => c.GraphQlName, c => c),
            };
            foreach (var (key, value) in _metadata)
            {
                table.Metadata[key] = value;
            }
            return table;
        }
    }

    private sealed class TestDbModel : IDbModel
    {
        private readonly Dictionary<string, DbTable> _tables;
        private readonly Dictionary<string, DbTable> _tablesByGraphQlName;

        public TestDbModel(Dictionary<string, DbTable> tables, Dictionary<string, object?>? modelMetadata = null)
        {
            _tables = tables;
            _tablesByGraphQlName = tables.Values.ToDictionary(t => t.GraphQlName, t => t);
            Tables = tables.Values.Cast<IDbTable>().ToList();
            Metadata = modelMetadata != null ? new Dictionary<string, object?>(modelMetadata) : new Dictionary<string, object?>();
        }

        public IReadOnlyCollection<IDbTable> Tables { get; }
        public IReadOnlyCollection<DbStoredProcedure> StoredProcedures { get; } = Array.Empty<DbStoredProcedure>();
        public IDictionary<string, object?> Metadata { get; init; }

        public string? GetMetadataValue(string property) =>
            Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;

        public bool GetMetadataBool(string property, bool defaultValue) =>
            (Metadata.TryGetValue(property, out var v) && v?.ToString() == null) ? defaultValue : v?.ToString() == "true";

        public IDbTable GetTableByFullGraphQlName(string graphQlName)
        {
            if (_tablesByGraphQlName.TryGetValue(graphQlName, out var table))
                return table;
            throw new KeyNotFoundException($"Table with GraphQL name '{graphQlName}' not found");
        }

        public IDbTable GetTableFromDbName(string dbName)
        {
            if (_tables.TryGetValue(dbName, out var table))
                return table;
            throw new KeyNotFoundException($"Table with DB name '{dbName}' not found");
        }
    }

    private sealed class NoOpMetadataLoader : IMetadataLoader
    {
        private readonly Dictionary<string, object?> _dbMetadata;

        public NoOpMetadataLoader(Dictionary<string, object?> dbMetadata)
        {
            _dbMetadata = dbMetadata;
        }

        public void ApplyDatabaseMetadata(IDictionary<string, object?> metadata, string rootName = ":root")
        {
            foreach (var kv in _dbMetadata)
                metadata[kv.Key] = kv.Value;
        }

        public void ApplySchemaMetadata(IDbSchema schema, IDictionary<string, object?> metadata) { }
        public void ApplyTableMetadata(IDbTable table, IDictionary<string, object?> metadata) { }
        public void ApplyColumnMetadata(IDbTable table, ColumnDto column, IDictionary<string, object?> metadata) { }
    }
}

/// <summary>
/// Predefined test fixtures for common scenarios
/// </summary>
public static class StandardTestFixtures
{
    /// <summary>
    /// Simple fixture with Users table
    /// </summary>
    public static IDbModel SimpleUsers() => DbModelTestFixture.Create()
        .WithTable("Users", t => t
            .WithPrimaryKey("Id")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Email", "nvarchar")
            .WithColumn("CreatedAt", "datetime2"))
        .Build();

    /// <summary>
    /// Users with Orders (OneToMany relationship)
    /// </summary>
    public static IDbModel UsersWithOrders() => DbModelTestFixture.Create()
        .WithTable("Users", t => t
            .WithPrimaryKey("Id")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Email", "nvarchar"))
        .WithTable("Orders", t => t
            .WithPrimaryKey("Id")
            .WithColumn("UserId", "int")
            .WithColumn("Total", "decimal")
            .WithColumn("Status", "nvarchar"))
        .WithSingleLink("Orders", "UserId", "Users", "Id", "user")
        .WithMultiLink("Users", "Id", "Orders", "UserId", "orders")
        .Build();

    /// <summary>
    /// Three-level hierarchy: Companies -> Departments -> Employees
    /// </summary>
    public static IDbModel CompanyHierarchy() => DbModelTestFixture.Create()
        .WithTable("Companies", t => t
            .WithPrimaryKey("Id")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Industry", "nvarchar"))
        .WithTable("Departments", t => t
            .WithPrimaryKey("Id")
            .WithColumn("CompanyId", "int")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Budget", "decimal"))
        .WithTable("Employees", t => t
            .WithPrimaryKey("Id")
            .WithColumn("DepartmentId", "int")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Salary", "decimal")
            .WithColumn("HireDate", "datetime2"))
        .WithSingleLink("Departments", "CompanyId", "Companies", "Id", "company")
        .WithSingleLink("Employees", "DepartmentId", "Departments", "Id", "department")
        .WithMultiLink("Companies", "Id", "Departments", "CompanyId", "departments")
        .WithMultiLink("Departments", "Id", "Employees", "DepartmentId", "employees")
        .Build();

    /// <summary>
    /// E-commerce model: Products, Categories, Orders, OrderItems
    /// </summary>
    public static IDbModel ECommerce() => DbModelTestFixture.Create()
        .WithTable("Categories", t => t
            .WithPrimaryKey("Id")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Description", "nvarchar"))
        .WithTable("Products", t => t
            .WithPrimaryKey("Id")
            .WithColumn("CategoryId", "int")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Price", "decimal")
            .WithColumn("Stock", "int"))
        .WithTable("Orders", t => t
            .WithPrimaryKey("Id")
            .WithColumn("CustomerId", "int")
            .WithColumn("OrderDate", "datetime2")
            .WithColumn("Total", "decimal")
            .WithColumn("Status", "nvarchar"))
        .WithTable("OrderItems", t => t
            .WithPrimaryKey("Id")
            .WithColumn("OrderId", "int")
            .WithColumn("ProductId", "int")
            .WithColumn("Quantity", "int")
            .WithColumn("UnitPrice", "decimal"))
        .WithSingleLink("Products", "CategoryId", "Categories", "Id", "category")
        .WithSingleLink("OrderItems", "OrderId", "Orders", "Id", "order")
        .WithSingleLink("OrderItems", "ProductId", "Products", "Id", "product")
        .WithMultiLink("Categories", "Id", "Products", "CategoryId", "products")
        .WithMultiLink("Orders", "Id", "OrderItems", "OrderId", "items")
        .Build();
}

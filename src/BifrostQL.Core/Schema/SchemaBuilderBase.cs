using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Base class for schema builders that provides common functionality for generating GraphQL schemas.
/// </summary>
public abstract class SchemaBuilderBase
{
    protected readonly StringBuilder Builder = new();
    protected readonly IDbModel Model;
    protected readonly ITypeMapper TypeMapper;

    protected SchemaBuilderBase(IDbModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        TypeMapper = model.TypeMapper ?? SqlServerTypeMapper.Instance;
    }

    /// <summary>
    /// Builds the complete schema and returns it as a string.
    /// </summary>
    public abstract string Build();

    /// <summary>
    /// Appends a GraphQL type definition with proper indentation.
    /// </summary>
    protected void AppendTypeDefinition(string typeName, Action contentBuilder)
    {
        Builder.AppendLine($"type {typeName} {{");
        contentBuilder();
        Builder.AppendLine("}");
    }

    /// <summary>
    /// Appends a GraphQL input type definition with proper indentation.
    /// </summary>
    protected void AppendInputTypeDefinition(string typeName, Action contentBuilder)
    {
        Builder.AppendLine($"input {typeName} {{");
        contentBuilder();
        Builder.AppendLine("}");
    }

    /// <summary>
    /// Appends a field definition with proper indentation.
    /// </summary>
    protected void AppendField(string name, string type, string? arguments = null)
    {
        var fieldDef = arguments != null
            ? $"\t{name}({arguments}) : {type}"
            : $"\t{name} : {type}";
        Builder.AppendLine(fieldDef);
    }

    /// <summary>
    /// Gets the GraphQL type name for a database column type.
    /// </summary>
    protected string GetGraphQlTypeName(string dataType, bool isNullable = false)
    {
        return TypeMapper.GetGraphQlTypeName(dataType, isNullable);
    }

    /// <summary>
    /// Gets the base GraphQL type (without nullability) for a database column type.
    /// </summary>
    protected string GetGraphQlType(string dataType)
    {
        return TypeMapper.GetGraphQlType(dataType);
    }

    /// <summary>
    /// Checks if raw SQL queries are enabled in the model metadata.
    /// </summary>
    protected bool IsRawSqlEnabled()
    {
        return Model.Metadata.TryGetValue("enable-raw-sql", out var val) && val?.ToString() == "true";
    }

    /// <summary>
    /// Checks if generic table queries are enabled in the model metadata.
    /// </summary>
    protected bool IsGenericTableEnabled()
    {
        return Model.Metadata.TryGetValue("enable-generic-table", out var val) && val?.ToString() == "true";
    }

    /// <summary>
    /// Checks if soft-delete is enabled for the given table.
    /// </summary>
    protected bool HasSoftDelete(IDbTable table)
    {
        return table.Metadata.TryGetValue("soft-delete", out var val) && val != null;
    }

    /// <summary>
    /// Gets the filter type name for a GraphQL type.
    /// </summary>
    protected static string GetFilterTypeName(string gqlType)
    {
        return $"FilterType{gqlType}Input";
    }

    /// <summary>
    /// Appends the standard filter type definition for a given GraphQL type.
    /// </summary>
    protected void AppendFilterTypeDefinition(string gqlType)
    {
        var name = GetFilterTypeName(gqlType);
        AppendInputTypeDefinition(name, () =>
        {
            Builder.AppendLine($"\t_eq: {gqlType}");
            Builder.AppendLine($"\t_neq: {gqlType}");
            Builder.AppendLine($"\t_lt: {gqlType}");
            Builder.AppendLine($"\t_lte: {gqlType}");
            Builder.AppendLine($"\t_gt: {gqlType}");
            Builder.AppendLine($"\t_gte: {gqlType}");
            Builder.AppendLine($"\t_contains: {gqlType}");
            Builder.AppendLine($"\t_ncontains: {gqlType}");
            Builder.AppendLine($"\t_starts_with: {gqlType}");
            Builder.AppendLine($"\t_nstarts_with: {gqlType}");
            Builder.AppendLine($"\t_ends_with: {gqlType}");
            Builder.AppendLine($"\t_nends_with: {gqlType}");
            Builder.AppendLine($"\t_in: [{gqlType}!]");
            Builder.AppendLine($"\t_nin: [{gqlType}!]");
            Builder.AppendLine($"\t_between: [{gqlType}!]");
            Builder.AppendLine($"\t_nbetween: [{gqlType}!]");
            Builder.AppendLine($"\t_null: Boolean");
        });
    }
}

/// <summary>
/// Builder for generating table-related schema definitions.
/// </summary>
public class TableSchemaBuilder : SchemaBuilderBase
{
    private readonly IDbTable _table;

    public TableSchemaBuilder(IDbTable table, IDbModel model) : base(model)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
    }

    /// <summary>
    /// Builds the schema for this table only.
    /// </summary>
    public override string Build()
    {
        throw new InvalidOperationException("Use specific methods like GetTableFieldDefinition() instead.");
    }

    /// <summary>
    /// Gets the field definition for the table in the root query type.
    /// </summary>
    public string GetTableFieldDefinition()
    {
        var hasSoftDelete = HasSoftDelete(_table);
        var includeDeletedArg = hasSoftDelete ? " _includeDeleted: Boolean" : "";
        return $"{_table.GraphQlName}(limit: Int, offset: Int, sort: [{_table.TableColumnSortEnumName}!] filter: {_table.TableFilterTypeName} _primaryKey: [String]{includeDeletedArg}): {_table.GraphQlName}_paged";
    }

    /// <summary>
    /// Gets the paged result type definition for the table.
    /// </summary>
    public string GetPagedTableTypeDefinition()
    {
        return $"type {_table.GraphQlName}_paged {{ data: [{_table.GraphQlName}!]! total: Int offset: Int limit: Int }}";
    }
}

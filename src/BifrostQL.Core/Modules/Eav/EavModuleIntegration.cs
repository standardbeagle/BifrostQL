using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using GraphQL.Utilities;

namespace BifrostQL.Core.Modules.Eav;

/// <summary>
/// Integrates the EAV flattening module with the BifrostQL schema and resolver pipeline.
/// This class wires up the EAV functionality into the existing infrastructure.
/// </summary>
public sealed class EavModuleIntegration
{
    private readonly EavModule _module;
    private readonly IDbModel _model;
    private readonly ISqlDialect _dialect;

    public EavModuleIntegration(IDbModel model, ISqlDialect dialect, EavSchemaCache? cache = null)
    {
        _model = model;
        _dialect = dialect;
        _module = new EavModule(model, dialect, cache: cache);
    }

    /// <summary>
    /// Gets the EAV module instance.
    /// </summary>
    public EavModule Module => _module;

    /// <summary>
    /// Generates additional GraphQL schema definitions for flattened EAV tables.
    /// </summary>
    public string GenerateSchemaExtensions()
    {
        var builder = new StringBuilder();
        var transformer = new EavSchemaTransformer(_model, _dialect);

        foreach (var table in _module.FlattenedTables)
        {
            // Add flattened type definition (placeholder - columns will be resolved at runtime)
            builder.AppendLine(GenerateDynamicFlattenedType(table));

            // Add field to parent table
            var fieldName = EavSchemaTransformer.GetFlattenedFieldName(table.MetaTable);
            builder.AppendLine($"extend type {table.ParentTable.GraphQlName} {{");
            builder.AppendLine($"\t{fieldName}: {EavSchemaTransformer.GetFlattenedTypeName(table.ParentTable)}");
            builder.AppendLine("}");

            // Add root query field
            var queryFieldName = EavSchemaTransformer.GetFlattenedQueryFieldName(table.ParentTable, table.MetaTable);
            builder.AppendLine($"extend type database {{");
            builder.AppendLine($"\t{queryFieldName}(limit: Int, offset: Int, filter: JSON): {EavSchemaTransformer.GetFlattenedTypeName(table.ParentTable)}_paged");
            builder.AppendLine("}");

            // Add paged type
            builder.AppendLine(transformer.GeneratePagedTypeDefinition(table.ParentTable));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Generates a dynamic flattened type with JSON scalar for flexible schema.
    /// This avoids needing to know all columns at schema generation time.
    /// </summary>
    private string GenerateDynamicFlattenedType(EavFlattenedTable table)
    {
        var typeName = EavSchemaTransformer.GetFlattenedTypeName(table.ParentTable);

        var builder = new StringBuilder();
        builder.AppendLine($"type {typeName} {{");

        // Add parent table's primary key columns
        foreach (var pk in table.ParentTable.KeyColumns)
        {
            var gqlType = SchemaGenerator.GetGraphQlTypeName(pk.EffectiveDataType, false, _model.TypeMapper);
            builder.AppendLine($"\t{pk.GraphQlName}: {gqlType}!");
        }

        // Add dynamic meta fields as JSON for flexibility
        builder.AppendLine($"\t_meta: JSON");

        builder.AppendLine("}");

        return builder.ToString();
    }

    /// <summary>
    /// Wires EAV resolvers into the schema builder.
    /// </summary>
    public void WireResolvers(SchemaBuilder builder)
    {
        // Wire up _flattened_{meta} fields on parent tables
        foreach (var table in _module.FlattenedTables)
        {
            var fieldName = EavSchemaTransformer.GetFlattenedFieldName(table.MetaTable);
            var parentType = builder.Types.For(table.ParentTable.GraphQlName);

            // Add the flattened field resolver
            parentType.FieldFor(fieldName).Resolver = new EavSingleResolver(_module, table);

            // Wire up root query field
            var queryType = builder.Types.For("database");
            var queryFieldName = EavSchemaTransformer.GetFlattenedQueryFieldName(table.ParentTable, table.MetaTable);
            queryType.FieldFor(queryFieldName).Resolver = new EavResolver(_module, table);
        }
    }

    /// <summary>
    /// Builds a resolver map for EAV fields.
    /// </summary>
    public Dictionary<(string typeName, string fieldName), IBifrostResolver> BuildResolverMap()
    {
        var map = new Dictionary<(string, string), IBifrostResolver>();

        foreach (var table in _module.FlattenedTables)
        {
            var fieldName = EavSchemaTransformer.GetFlattenedFieldName(table.MetaTable);
            var queryFieldName = EavSchemaTransformer.GetFlattenedQueryFieldName(table.ParentTable, table.MetaTable);

            // Resolver for field on parent type
            map[(table.ParentTable.GraphQlName, fieldName)] = new EavSingleResolver(_module, table);

            // Resolver for root query field
            map[("database", queryFieldName)] = new EavResolver(_module, table);
        }

        return map;
    }
}

/// <summary>
/// Filter transformer that integrates EAV filtering with the query pipeline.
/// </summary>
public sealed class EavFilterTransformerIntegration : IFilterTransformer
{
    private readonly EavModule _module;

    public int Priority => 150;

    public EavFilterTransformerIntegration(EavModule module)
    {
        _module = module;
    }

    public bool AppliesTo(IDbTable table, QueryTransformContext context)
    {
        return _module.GetFlattenedTable(table.DbName) != null ||
               _module.FlattenedTables.Any(t =>
                   string.Equals(t.ParentTable.DbName, table.DbName, StringComparison.OrdinalIgnoreCase));
    }

    public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
    {
        // EAV filtering is handled by the EAV query transformer
        return null;
    }
}

/// <summary>
/// Extension methods for registering the EAV module with BifrostQL.
/// </summary>
public static class EavModuleRegistrationExtensions
{
    /// <summary>
    /// Adds EAV flattening support to a BifrostQL schema.
    /// </summary>
    public static void AddEavFlattening(
        this SchemaBuilder builder,
        IDbModel model,
        ISqlDialect dialect,
        EavSchemaCache? cache = null)
    {
        var integration = new EavModuleIntegration(model, dialect, cache);

        // Wire resolvers for EAV fields
        integration.WireResolvers(builder);
    }

    /// <summary>
    /// Creates an EAV module integration for the given model and dialect.
    /// </summary>
    public static EavModuleIntegration CreateEavModule(
        this IDbModel model,
        ISqlDialect dialect,
        EavSchemaCache? cache = null)
    {
        return new EavModuleIntegration(model, dialect, cache);
    }
}

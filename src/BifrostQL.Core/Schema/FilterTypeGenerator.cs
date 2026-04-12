using System.Text;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Generates GraphQL filter type definitions for database column types.
/// </summary>
public static class FilterTypeGenerator
{
    /// <summary>
    /// Generates a filter input type definition for a given GraphQL type.
    /// </summary>
    public static string Generate(string gqlType)
    {
        var builder = new StringBuilder();
        var name = GetFilterTypeName(gqlType);
        
        builder.AppendLine($"input {name} {{");
        
        // Common filters for all types
        AppendCommonFilters(builder, gqlType);
        
        // String-specific filters
        if (IsStringType(gqlType))
        {
            AppendStringFilters(builder, gqlType);
        }
        
        // Null check (for all types)
        builder.AppendLine("\t_null: Boolean");
        
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Gets the filter type name for a GraphQL type.
    /// </summary>
    public static string GetFilterTypeName(string gqlType)
    {
        return $"FilterType{gqlType}Input";
    }

    private static void AppendCommonFilters(StringBuilder builder, string gqlType)
    {
        var commonFilters = new[]
        {
            ("_eq", gqlType),
            ("_neq", gqlType),
            ("_gt", gqlType),
            ("_gte", gqlType),
            ("_lt", gqlType),
            ("_lte", gqlType),
            ("_in", $"[{gqlType}!]"),
            ("_nin", $"[{gqlType}!]"),
            ("_between", $"[{gqlType}!]"),
            ("_nbetween", $"[{gqlType}!]"),
        };

        foreach (var (fieldName, type) in commonFilters)
        {
            builder.AppendLine($"\t{fieldName}: {type}");
        }
    }

    private static void AppendStringFilters(StringBuilder builder, string gqlType)
    {
        var stringFilters = new[]
        {
            ("_contains", gqlType),
            ("_ncontains", gqlType),
            ("_starts_with", gqlType),
            ("_nstarts_with", gqlType),
            ("_ends_with", gqlType),
            ("_nends_with", gqlType),
            ("_like", gqlType),
            ("_nlike", gqlType),
        };

        foreach (var (fieldName, type) in stringFilters)
        {
            builder.AppendLine($"\t{fieldName}: {type}");
        }
    }

    private static bool IsStringType(string gqlType)
    {
        return gqlType.Equals("String", StringComparison.OrdinalIgnoreCase);
    }
}

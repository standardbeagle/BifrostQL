using System.Text;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Generates GraphQL enum type definitions from pre-loaded lookup table values.
/// The actual database loading is performed by the server layer; this class
/// only handles schema text generation from already-loaded data.
/// </summary>
public sealed class EnumTableSchemaGenerator
{
    private readonly EnumTableConfig _config;
    private readonly IReadOnlyList<EnumValueEntry> _values;

    public EnumTableSchemaGenerator(EnumTableConfig config, IReadOnlyList<EnumValueEntry> values)
    {
        _config = config;
        _values = values;
    }

    /// <summary>The GraphQL enum type name.</summary>
    public string EnumTypeName => _config.GraphQlEnumName;

    /// <summary>
    /// Generates the GraphQL enum type definition text.
    /// Returns an empty string if no valid values exist.
    /// </summary>
    public string GetEnumTypeDefinition()
    {
        if (_values.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"enum {_config.GraphQlEnumName} {{");
        foreach (var entry in _values)
        {
            sb.AppendLine($"    {entry.GraphQlName}");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Gets the GraphQL filter input type name for this enum, suitable for
    /// use in filter arguments on FK columns that reference this enum table.
    /// </summary>
    public string GetFilterInputTypeName()
    {
        return $"FilterType{_config.GraphQlEnumName}Input";
    }

    /// <summary>
    /// Generates the GraphQL filter input type definition for this enum.
    /// Provides _eq, _neq, _in, and _nin filter operations.
    /// </summary>
    public string GetFilterTypeDefinition()
    {
        if (_values.Count == 0)
            return string.Empty;

        var enumName = _config.GraphQlEnumName;
        var sb = new StringBuilder();
        sb.AppendLine($"input {GetFilterInputTypeName()} {{");
        sb.AppendLine($"    _eq: {enumName}");
        sb.AppendLine($"    _neq: {enumName}");
        sb.AppendLine($"    _in: [{enumName}]");
        sb.AppendLine($"    _nin: [{enumName}]");
        sb.AppendLine("}");
        return sb.ToString();
    }
}

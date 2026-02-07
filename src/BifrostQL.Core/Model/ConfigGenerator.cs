namespace BifrostQL.Core.Model;

/// <summary>
/// Generates metadata configuration rule strings from detected patterns.
/// Produces rules in the format consumed by <see cref="MetadataLoader"/>:
///   "dbo.table_name { key: value; key2: value2 }"
/// </summary>
public sealed class ConfigGenerator
{
    /// <summary>
    /// Generates metadata rules from a set of table pattern results.
    /// </summary>
    public IReadOnlyList<string> Generate(IReadOnlyList<TablePatternResult> results)
    {
        ArgumentNullException.ThrowIfNull(results, nameof(results));

        var rules = new List<string>();
        foreach (var result in results)
        {
            rules.AddRange(GenerateTableRules(result));
        }
        return rules;
    }

    /// <summary>
    /// Generates metadata rules for a single table's detected patterns.
    /// </summary>
    public IReadOnlyList<string> GenerateTableRules(TablePatternResult result)
    {
        ArgumentNullException.ThrowIfNull(result, nameof(result));

        var rules = new List<string>();
        var tableSelector = result.QualifiedName;

        var tableProperties = new List<string>();

        if (result.SoftDelete != null)
            tableProperties.Add($"soft-delete: {result.SoftDelete.ColumnName}");

        if (result.Tenant != null)
            tableProperties.Add($"tenant-filter: {result.Tenant.ColumnName}");

        if (tableProperties.Count > 0)
            rules.Add($"{tableSelector} {{ {string.Join("; ", tableProperties)} }}");

        foreach (var audit in result.AuditColumns)
        {
            var populateValue = GetPopulateValue(audit.Role);
            rules.Add($"{tableSelector}.{audit.ColumnName} {{ populate: {populateValue} }}");
        }

        return rules;
    }

    private static string GetPopulateValue(AuditRole role)
    {
        return role switch
        {
            AuditRole.CreatedOn => "created-on",
            AuditRole.CreatedBy => "created-by",
            AuditRole.UpdatedOn => "updated-on",
            AuditRole.UpdatedBy => "updated-by",
            AuditRole.DeletedOn => "deleted-on",
            AuditRole.DeletedBy => "deleted-by",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown audit role"),
        };
    }
}

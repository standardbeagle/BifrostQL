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
        if (results.Any(r => r.AuditColumns.Any(a => StampsActor(a.Role))))
            rules.Add(UserAuditKeyRule);
        foreach (var result in results)
        {
            rules.AddRange(GenerateTableRules(result));
        }
        return rules;
    }

    /// <summary>
    /// The model-level rule naming the claim actor columns are stamped from. Model load refuses a
    /// created-by/updated-by/deleted-by populator without one, so <see cref="Generate"/> emits it
    /// whenever it emits such a populator. The value is the key the identity mapper writes the
    /// caller's id under by default; a host with its own claim mapping must confirm it.
    /// </summary>
    public static readonly string UserAuditKeyRule =
        $":root {{ {MetadataKeys.Audit.UserKey}: {MetadataKeys.Auth.DefaultUserAuditKey} }}";

    private static bool StampsActor(AuditRole role)
        => role is AuditRole.CreatedBy or AuditRole.UpdatedBy or AuditRole.DeletedBy;

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

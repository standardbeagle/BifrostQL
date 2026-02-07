using System.Text.RegularExpressions;

namespace BifrostQL.Core.Model;

/// <summary>
/// Validates metadata configuration rules against an IDbModel,
/// verifying that referenced tables and columns exist and that
/// metadata keys are recognized.
/// </summary>
public sealed class ConfigValidator
{
    private static readonly Regex RuleRegex = new(
        @"^(?<selector>.+?)\s*\{\s*(?<properties>.*)\s*\}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates a list of metadata rule strings against the model.
    /// Returns a list of validation issues (empty if all rules are valid).
    /// </summary>
    public IReadOnlyList<ConfigValidationIssue> Validate(IReadOnlyList<string> rules, IDbModel model)
    {
        ArgumentNullException.ThrowIfNull(rules, nameof(rules));
        ArgumentNullException.ThrowIfNull(model, nameof(model));

        var issues = new List<ConfigValidationIssue>();
        foreach (var rule in rules)
        {
            issues.AddRange(ValidateRule(rule, model));
        }
        return issues;
    }

    /// <summary>
    /// Validates a single metadata rule string against the model.
    /// </summary>
    public IReadOnlyList<ConfigValidationIssue> ValidateRule(string rule, IDbModel model)
    {
        ArgumentNullException.ThrowIfNull(rule, nameof(rule));
        ArgumentNullException.ThrowIfNull(model, nameof(model));

        var issues = new List<ConfigValidationIssue>();

        var match = RuleRegex.Match(rule);
        if (!match.Success)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                "Rule does not match expected format: \"selector { key: value }\""));
            return issues;
        }

        var selector = match.Groups["selector"].Value.Trim();
        var propertiesStr = match.Groups["properties"].Value.Trim();

        if (string.IsNullOrWhiteSpace(propertiesStr))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                "Rule has no properties defined"));
            return issues;
        }

        var properties = ParseProperties(propertiesStr);
        if (properties.Count == 0)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                "Rule has no valid properties"));
            return issues;
        }

        // Determine selector type: :root, schema.table.column, or schema.table
        if (string.Equals(selector, ":root", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDatabaseProperties(rule, properties, issues);
            return issues;
        }

        var parts = selector.Split('.');
        if (parts.Length == 3)
        {
            ValidateColumnSelector(rule, parts[0], parts[1], parts[2], properties, model, issues);
        }
        else if (parts.Length == 2)
        {
            ValidateTableSelector(rule, parts[0], parts[1], properties, model, issues);
        }
        else
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Warning,
                rule,
                $"Selector '{selector}' does not match expected format (schema.table or schema.table.column)"));
        }

        return issues;
    }

    private void ValidateTableSelector(
        string rule, string schema, string tableName,
        Dictionary<string, string> properties, IDbModel model,
        List<ConfigValidationIssue> issues)
    {
        // Check if table uses wildcards - skip table existence check
        if (tableName.Contains('*'))
        {
            ValidateTableProperties(rule, properties, issues);
            return;
        }

        var table = FindTable(schema, tableName, model);
        if (table == null)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                $"Table '{schema}.{tableName}' not found in the model"));
            return;
        }

        // Validate column references within properties
        ValidateTablePropertyColumnReferences(rule, table, properties, issues);
        ValidateTableProperties(rule, properties, issues);
    }

    private void ValidateColumnSelector(
        string rule, string schema, string tableName, string columnName,
        Dictionary<string, string> properties, IDbModel model,
        List<ConfigValidationIssue> issues)
    {
        // Check if table uses wildcards - skip existence check
        if (tableName.Contains('*'))
        {
            ValidateColumnProperties(rule, properties, issues);
            return;
        }

        var table = FindTable(schema, tableName, model);
        if (table == null)
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                $"Table '{schema}.{tableName}' not found in the model"));
            return;
        }

        // Check if column uses wildcards - skip column existence check
        if (!columnName.Contains('*') && !table.ColumnLookup.ContainsKey(columnName))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                $"Column '{columnName}' not found in table '{schema}.{tableName}'"));
            return;
        }

        ValidateColumnProperties(rule, properties, issues);
    }

    private static void ValidateTablePropertyColumnReferences(
        string rule, IDbTable table,
        Dictionary<string, string> properties,
        List<ConfigValidationIssue> issues)
    {
        // soft-delete references a column
        if (properties.TryGetValue("soft-delete", out var sdColumn) &&
            !table.ColumnLookup.ContainsKey(sdColumn))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                $"Soft-delete column '{sdColumn}' not found in table '{table.TableSchema}.{table.DbName}'"));
        }

        // soft-delete-by references a column
        if (properties.TryGetValue("soft-delete-by", out var sdByColumn) &&
            !table.ColumnLookup.ContainsKey(sdByColumn))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                $"Soft-delete-by column '{sdByColumn}' not found in table '{table.TableSchema}.{table.DbName}'"));
        }

        // tenant-filter references a column
        if (properties.TryGetValue("tenant-filter", out var tfColumn) &&
            !table.ColumnLookup.ContainsKey(tfColumn))
        {
            issues.Add(new ConfigValidationIssue(
                ConfigIssueSeverity.Error,
                rule,
                $"Tenant-filter column '{tfColumn}' not found in table '{table.TableSchema}.{table.DbName}'"));
        }
    }

    private static void ValidateTableProperties(
        string rule, Dictionary<string, string> properties,
        List<ConfigValidationIssue> issues)
    {
        var warnings = MetadataValidator.ValidateTableMetadata(rule, properties.ToDictionary(p => p.Key, p => (object?)p.Value));
        foreach (var warning in warnings)
        {
            issues.Add(new ConfigValidationIssue(ConfigIssueSeverity.Warning, rule, warning));
        }
    }

    private static void ValidateColumnProperties(
        string rule, Dictionary<string, string> properties,
        List<ConfigValidationIssue> issues)
    {
        foreach (var key in properties.Keys)
        {
            var columnMetadata = new Dictionary<string, object?> { [key] = properties[key] };
            var warnings = MetadataValidator.ValidateColumnMetadata("", "", columnMetadata);
            foreach (var warning in warnings)
            {
                issues.Add(new ConfigValidationIssue(ConfigIssueSeverity.Warning, rule, warning));
            }
        }
    }

    private static void ValidateDatabaseProperties(
        string rule, Dictionary<string, string> properties,
        List<ConfigValidationIssue> issues)
    {
        var metadata = properties.ToDictionary(p => p.Key, p => (object?)p.Value);
        var warnings = MetadataValidator.ValidateDatabaseMetadata(metadata);
        foreach (var warning in warnings)
        {
            issues.Add(new ConfigValidationIssue(ConfigIssueSeverity.Warning, rule, warning));
        }
    }

    private static IDbTable? FindTable(string schema, string tableName, IDbModel model)
    {
        return model.Tables.FirstOrDefault(t =>
            string.Equals(t.TableSchema, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.DbName, tableName, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> ParseProperties(string propertiesStr)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = propertiesStr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var colonIdx = part.IndexOf(':');
            if (colonIdx <= 0)
                continue;
            var key = part.Substring(0, colonIdx).Trim();
            var value = part.Substring(colonIdx + 1).Trim();
            result[key] = value;
        }
        return result;
    }
}

/// <summary>
/// Severity of a configuration validation issue.
/// </summary>
public enum ConfigIssueSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A single validation issue found when checking a configuration rule.
/// </summary>
public sealed record ConfigValidationIssue(
    ConfigIssueSeverity Severity,
    string Rule,
    string Message);

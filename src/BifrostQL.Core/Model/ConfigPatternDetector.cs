using System.Text.RegularExpressions;

namespace BifrostQL.Core.Model;

/// <summary>
/// Analyzes an IDbModel and detects common configuration patterns
/// (audit columns, soft-delete, tenant isolation) from column naming conventions.
/// Patterns are configurable via <see cref="DetectionPatterns"/>.
/// </summary>
public sealed class ConfigPatternDetector
{
    private readonly DetectionPatterns _patterns;

    public ConfigPatternDetector()
        : this(DetectionPatterns.Default)
    {
    }

    public ConfigPatternDetector(DetectionPatterns patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns, nameof(patterns));
        _patterns = patterns;
    }

    /// <summary>
    /// Analyzes the entire model and returns detected patterns for each table.
    /// Only tables with at least one detected pattern are included.
    /// </summary>
    public IReadOnlyList<TablePatternResult> Detect(IDbModel model)
    {
        ArgumentNullException.ThrowIfNull(model, nameof(model));

        var results = new List<TablePatternResult>();
        foreach (var table in model.Tables)
        {
            var result = DetectTable(table);
            if (result.HasPatterns)
                results.Add(result);
        }
        return results;
    }

    /// <summary>
    /// Analyzes a single table and returns any detected patterns.
    /// </summary>
    public TablePatternResult DetectTable(IDbTable table)
    {
        ArgumentNullException.ThrowIfNull(table, nameof(table));

        var softDelete = DetectSoftDelete(table);
        var tenant = DetectTenant(table);
        var auditColumns = DetectAuditColumns(table);

        return new TablePatternResult(
            table.TableSchema,
            table.DbName,
            softDelete,
            tenant,
            auditColumns);
    }

    private SoftDeletePattern? DetectSoftDelete(IDbTable table)
    {
        foreach (var column in table.Columns)
        {
            if (MatchesAny(column.ColumnName, _patterns.SoftDeleteColumns))
                return new SoftDeletePattern(column.ColumnName);
        }
        return null;
    }

    private TenantPattern? DetectTenant(IDbTable table)
    {
        foreach (var column in table.Columns)
        {
            if (MatchesAny(column.ColumnName, _patterns.TenantColumns))
                return new TenantPattern(column.ColumnName);
        }
        return null;
    }

    private IReadOnlyList<AuditColumnPattern> DetectAuditColumns(IDbTable table)
    {
        var results = new List<AuditColumnPattern>();
        foreach (var column in table.Columns)
        {
            if (MatchesAny(column.ColumnName, _patterns.CreatedOnColumns))
                results.Add(new AuditColumnPattern(column.ColumnName, AuditRole.CreatedOn));
            else if (MatchesAny(column.ColumnName, _patterns.CreatedByColumns))
                results.Add(new AuditColumnPattern(column.ColumnName, AuditRole.CreatedBy));
            else if (MatchesAny(column.ColumnName, _patterns.UpdatedOnColumns))
                results.Add(new AuditColumnPattern(column.ColumnName, AuditRole.UpdatedOn));
            else if (MatchesAny(column.ColumnName, _patterns.UpdatedByColumns))
                results.Add(new AuditColumnPattern(column.ColumnName, AuditRole.UpdatedBy));
            else if (MatchesAny(column.ColumnName, _patterns.DeletedOnColumns))
                results.Add(new AuditColumnPattern(column.ColumnName, AuditRole.DeletedOn));
            else if (MatchesAny(column.ColumnName, _patterns.DeletedByColumns))
                results.Add(new AuditColumnPattern(column.ColumnName, AuditRole.DeletedBy));
        }
        return results;
    }

    /// <summary>
    /// Detects lookup/reference tables in the model based on structural heuristics.
    /// </summary>
    public IReadOnlyList<LookupTablePattern> DetectLookupTables(IDbModel model)
    {
        ArgumentNullException.ThrowIfNull(model, nameof(model));

        var results = new List<LookupTablePattern>();
        foreach (var table in model.Tables)
        {
            if (LookupTableDetector.IsLookupTable(table))
            {
                var roles = LookupTableDetector.DetectColumnRoles(table);
                results.Add(new LookupTablePattern(table.TableSchema, table.DbName, roles));
            }
        }
        return results;
    }

    private static bool MatchesAny(string columnName, IReadOnlyList<Regex> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(columnName))
                return true;
        }
        return false;
    }
}

/// <summary>
/// The role of an audit column in the metadata configuration system.
/// </summary>
public enum AuditRole
{
    CreatedOn,
    CreatedBy,
    UpdatedOn,
    UpdatedBy,
    DeletedOn,
    DeletedBy,
}

/// <summary>
/// Detected soft-delete pattern on a table.
/// </summary>
public sealed record SoftDeletePattern(string ColumnName);

/// <summary>
/// Detected tenant isolation pattern on a table.
/// </summary>
public sealed record TenantPattern(string ColumnName);

/// <summary>
/// Detected audit column pattern on a table.
/// </summary>
public sealed record AuditColumnPattern(string ColumnName, AuditRole Role);

/// <summary>
/// Detected lookup table pattern with column roles.
/// </summary>
public sealed record LookupTablePattern(string Schema, string TableName, LookupColumnRoles Roles);

/// <summary>
/// Aggregated detection results for a single table.
/// </summary>
public sealed class TablePatternResult
{
    public string Schema { get; }
    public string TableName { get; }
    public SoftDeletePattern? SoftDelete { get; }
    public TenantPattern? Tenant { get; }
    public IReadOnlyList<AuditColumnPattern> AuditColumns { get; }

    public TablePatternResult(
        string schema,
        string tableName,
        SoftDeletePattern? softDelete,
        TenantPattern? tenant,
        IReadOnlyList<AuditColumnPattern> auditColumns)
    {
        Schema = schema;
        TableName = tableName;
        SoftDelete = softDelete;
        Tenant = tenant;
        AuditColumns = auditColumns;
    }

    public bool HasPatterns =>
        SoftDelete != null || Tenant != null || AuditColumns.Count > 0;

    public string QualifiedName =>
        string.IsNullOrWhiteSpace(Schema) ? TableName : $"{Schema}.{TableName}";
}

/// <summary>
/// Configurable naming patterns used by <see cref="ConfigPatternDetector"/>.
/// Each pattern list is matched case-insensitively against column names.
/// </summary>
public sealed class DetectionPatterns
{
    public IReadOnlyList<Regex> SoftDeleteColumns { get; init; } = Array.Empty<Regex>();
    public IReadOnlyList<Regex> TenantColumns { get; init; } = Array.Empty<Regex>();
    public IReadOnlyList<Regex> CreatedOnColumns { get; init; } = Array.Empty<Regex>();
    public IReadOnlyList<Regex> CreatedByColumns { get; init; } = Array.Empty<Regex>();
    public IReadOnlyList<Regex> UpdatedOnColumns { get; init; } = Array.Empty<Regex>();
    public IReadOnlyList<Regex> UpdatedByColumns { get; init; } = Array.Empty<Regex>();
    public IReadOnlyList<Regex> DeletedOnColumns { get; init; } = Array.Empty<Regex>();
    public IReadOnlyList<Regex> DeletedByColumns { get; init; } = Array.Empty<Regex>();

    /// <summary>
    /// Creates a case-insensitive anchored regex from a glob-like pattern.
    /// Supports * as wildcard.
    /// </summary>
    public static Regex Pattern(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Default patterns that cover common naming conventions.
    /// </summary>
    public static DetectionPatterns Default { get; } = new()
    {
        SoftDeleteColumns = new[]
        {
            Pattern("deleted_at"),
            Pattern("deleted_on"),
            Pattern("is_deleted"),
            Pattern("is_active"),
        },
        TenantColumns = new[]
        {
            Pattern("tenant_id"),
            Pattern("organization_id"),
            Pattern("org_id"),
            Pattern("company_id"),
        },
        CreatedOnColumns = new[]
        {
            Pattern("created_at"),
            Pattern("created_on"),
            Pattern("created_date"),
        },
        CreatedByColumns = new[]
        {
            Pattern("created_by"),
            Pattern("created_by_*"),
        },
        UpdatedOnColumns = new[]
        {
            Pattern("updated_at"),
            Pattern("updated_on"),
            Pattern("modified_at"),
            Pattern("modified_on"),
        },
        UpdatedByColumns = new[]
        {
            Pattern("updated_by"),
            Pattern("updated_by_*"),
            Pattern("modified_by"),
            Pattern("modified_by_*"),
        },
        DeletedOnColumns = new[]
        {
            Pattern("deleted_at"),
            Pattern("deleted_on"),
        },
        DeletedByColumns = new[]
        {
            Pattern("deleted_by"),
            Pattern("deleted_by_*"),
        },
    };
}

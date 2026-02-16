using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Injects tenant isolation filters based on table metadata.
///
/// Configuration via metadata:
///   "dbo.orders { tenant-filter: tenant_id }"
///
/// Requires user context to contain the tenant identifier:
///   UserContext["tenant_id"] = 123
///
/// The key used to look up the tenant in UserContext can be configured:
///   "BifrostQL:Metadata { tenant-context-key: org_id }"
/// </summary>
public sealed class TenantFilterTransformer : IFilterTransformer
{
    public const string MetadataKey = "tenant-filter";
    public const string TenantContextKeyMetadata = "tenant-context-key";
    public const string DefaultTenantContextKey = "tenant_id";

    // Security: Tenant filtering runs first (innermost)
    public int Priority => 0;

    public bool AppliesTo(IDbTable table, QueryTransformContext context)
    {
        return table.Metadata.TryGetValue(MetadataKey, out var val) && val != null;
    }

    public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
    {
        var columnName = table.Metadata[MetadataKey]?.ToString();
        if (string.IsNullOrWhiteSpace(columnName))
            return null;

        // Determine which key to look up in UserContext
        var tenantContextKey = GetTenantContextKey(context.Model);

        var fullTableName = $"{table.TableSchema}.{table.DbName}";

        // Get tenant ID from user context - this is required for security
        if (!context.UserContext.TryGetValue(tenantContextKey, out var tenantId))
        {
            throw new BifrostExecutionError(
                $"Tenant context required but not found. " +
                $"Expected '{tenantContextKey}' in user context for table '{fullTableName}'.");
        }

        if (tenantId == null)
        {
            throw new BifrostExecutionError(
                $"Tenant ID cannot be null for table '{fullTableName}'.");
        }

        // Verify the column exists
        if (!table.ColumnLookup.ContainsKey(columnName))
        {
            throw new BifrostExecutionError(
                $"Tenant filter column '{columnName}' not found in table '{fullTableName}'.");
        }

        return TableFilterFactory.Equals(table.DbName, columnName, tenantId);
    }

    private static string GetTenantContextKey(IDbModel model)
    {
        if (model.Metadata.TryGetValue(TenantContextKeyMetadata, out var key) && key is string keyStr)
        {
            return keyStr;
        }
        return DefaultTenantContextKey;
    }
}

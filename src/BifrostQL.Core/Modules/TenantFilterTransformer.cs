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
public sealed class TenantFilterTransformer : ContextValueFilterTransformerBase
{
    public const string MetadataKey = "tenant-filter";
    public const string TenantContextKeyMetadata = "tenant-context-key";
    public const string DefaultTenantContextKey = "tenant_id";

    public TenantFilterTransformer() : base(MetadataKey, DefaultTenantContextKey, priority: 0)
    {
    }

    public override string ModuleName => "tenant-filter";

    protected override TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context)
    {
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

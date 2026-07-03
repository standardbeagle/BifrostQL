using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Enforces tenant isolation on the write path — the mutation counterpart to
/// <see cref="TenantFilterTransformer"/>, which only guards reads. Without this,
/// the <c>tenant-filter</c> metadata is one-directional: any caller could
/// UPDATE or DELETE another tenant's rows by primary key, or INSERT a row with
/// an arbitrary <c>tenant_id</c>. Tenant isolation is presented in the docs as a
/// security guarantee, so both directions must be enforced.
///
/// Configuration via metadata (shared with the read-side transformer):
///   "dbo.orders { tenant-filter: tenant_id }"
///
/// Requires the tenant identifier in user context (fail-closed — a missing or
/// null tenant aborts the mutation rather than silently skipping the guard):
///   UserContext["tenant_id"] = 123
///
/// The context key can be overridden model-wide:
///   "BifrostQL:Metadata { tenant-context-key: org_id }"
///
/// Behavior:
///   INSERT — forces the tenant column to the caller's tenant, overriding any
///            client-supplied value.
///   UPDATE — ANDs "tenant_column = &lt;caller tenant&gt;" onto the WHERE clause and
///            pins the tenant column in the SET so a row cannot be reassigned to
///            another tenant.
///   DELETE — ANDs "tenant_column = &lt;caller tenant&gt;" onto the WHERE clause so a
///            caller can only delete their own rows.
/// </summary>
public sealed class TenantMutationTransformer : MetadataMutationTransformerBase
{
    public const string MetadataKey = MetadataKeys.Security.TenantFilter;
    public const string TenantContextKeyMetadata = MetadataKeys.Security.TenantContextKey;
    public const string DefaultTenantContextKey = "tenant_id";

    public TenantMutationTransformer() : base(MetadataKey, priority: 0)
    {
    }

    public override string ModuleName => MetadataKeys.Security.TenantFilter;

    protected override MutationTransformResult TransformCore(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context,
        string columnName)
    {
        var tenantId = ResolveTenantId(table, context);

        switch (mutationType)
        {
            case MutationType.Insert:
            {
                // Pin the tenant column to the caller's tenant, overriding any
                // client-supplied value so a caller cannot plant a row in
                // another tenant.
                var pinned = new Dictionary<string, object?>(data) { [columnName] = tenantId };
                return new MutationTransformResult
                {
                    MutationType = MutationType.Insert,
                    Data = pinned,
                };
            }

            case MutationType.Update:
            {
                // Pin the tenant column in the SET (block reassignment) and
                // scope the WHERE to the caller's tenant.
                var pinned = new Dictionary<string, object?>(data) { [columnName] = tenantId };
                return new MutationTransformResult
                {
                    MutationType = MutationType.Update,
                    Data = pinned,
                    AdditionalFilter = TableFilterFactory.Equals(table.DbName, columnName, tenantId),
                };
            }

            default: // Delete
                return new MutationTransformResult
                {
                    MutationType = mutationType,
                    Data = data,
                    AdditionalFilter = TableFilterFactory.Equals(table.DbName, columnName, tenantId),
                };
        }
    }

    private static object ResolveTenantId(IDbTable table, MutationTransformContext context)
    {
        var tenantContextKey = GetTenantContextKey(context.Model);
        var fullTableName = $"{table.TableSchema}.{table.DbName}";

        if (!context.UserContext.TryGetValue(tenantContextKey, out var tenantId))
            throw new BifrostExecutionError(
                $"Tenant context required but not found. " +
                $"Expected '{tenantContextKey}' in user context for table '{fullTableName}'.");

        if (tenantId == null)
            throw new BifrostExecutionError(
                $"Tenant ID cannot be null for table '{fullTableName}'.");

        return tenantId;
    }

    private static string GetTenantContextKey(IDbModel model)
    {
        if (model.Metadata.TryGetValue(TenantContextKeyMetadata, out var key) && key is string keyStr)
            return keyStr;
        return DefaultTenantContextKey;
    }
}

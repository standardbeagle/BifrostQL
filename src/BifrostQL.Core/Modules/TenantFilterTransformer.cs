using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Auth;

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
    public const string MetadataKey = MetadataKeys.Security.TenantFilter;
    public const string TenantContextKeyMetadata = MetadataKeys.Security.TenantContextKey;
    public const string DefaultTenantContextKey = "tenant_id";

    public TenantFilterTransformer() : base(MetadataKey, DefaultTenantContextKey, priority: 0)
    {
    }

    public override string ModuleName => MetadataKeys.Security.TenantFilter;

    protected override TableFilter BuildFilter(IDbTable table, string columnName, QueryTransformContext context)
    {
        // Determine which key to look up in UserContext
        var tenantContextKey = ResolveTenantContextKey(context.Model);

        var fullTableName = $"{table.TableSchema}.{table.DbName}";

        // Get tenant ID from user context - this is required for security
        if (!context.UserContext.TryGetValue(tenantContextKey, out var tenantId))
        {
            throw new BifrostExecutionError(
                $"Tenant context required but not found. " +
                $"Expected '{tenantContextKey}' in user context for table '{fullTableName}'.")
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };
        }

        if (tenantId == null)
        {
            throw new BifrostExecutionError(
                $"Tenant ID cannot be null for table '{fullTableName}'.")
            { ErrorCode = BifrostExecutionError.AccessDeniedCode };
        }

        return TableFilterFactory.Equals(table, columnName,
            ContextValueCoercer.Coerce(context.Model.TypeMapper, table, columnName, tenantId));
    }

    /// <summary>
    /// The user-context key the tenant claim is read from: the model-level
    /// <c>tenant-context-key</c> metadata, else <see cref="DefaultTenantContextKey"/>.
    /// Public because the history trail read authorizes by the SAME claim source —
    /// one resolution rule, so the trail can never scope by a different claim than
    /// the base table does.
    /// </summary>
    public static string ResolveTenantContextKey(IDbModel model)
    {
        // Absent metadata → the default claim. But metadata that IS present yet is not a
        // usable (non-empty) string is a misconfiguration of a SECURITY key: silently
        // falling back to the default would scope by the wrong claim. Fail fast instead,
        // consistent with the repo's fail-fast posture for security-relevant config.
        if (!model.Metadata.TryGetValue(TenantContextKeyMetadata, out var key))
            return DefaultTenantContextKey;
        if (key is string keyStr && !string.IsNullOrWhiteSpace(keyStr))
            return keyStr;
        throw new BifrostExecutionError(
            $"Metadata '{TenantContextKeyMetadata}' must be a non-empty string naming the tenant context key; " +
            "remove it to use the default, or set a valid key.");
    }

    /// <summary>
    /// The refusal for a tenant claim that holds several values (or none) where one tenant is
    /// required. Constant text: it never echoes the claim's contents.
    /// </summary>
    public const string NonScalarTenantMessage = "Tenant context value is not a single value.";

    /// <summary>
    /// The caller's tenant under the model's <see cref="ResolveTenantContextKey"/>, as the one
    /// string every tenant-stamping store (change sets, pending changes, outbox, audit) records
    /// and compares. See <see cref="ReadTenantValue(IDictionary{string, object?}, string)"/>.
    /// </summary>
    public static string? ReadTenantValue(IDbModel model, IDictionary<string, object?> userContext)
        => ReadTenantValue(userContext, ResolveTenantContextKey(model));

    /// <summary>
    /// The tenant under <paramref name="tenantContextKey"/> as a scalar string, or null when the
    /// context has none. A raw principal claim reaches the user context as a one-element
    /// <c>string[]</c> under its claim type; that is read as its single value, the way the
    /// tenant filter's coercion reads it. A sequence of any other length is refused with
    /// <see cref="NonScalarTenantMessage"/>: <c>ToString()</c> on it gave
    /// <c>"System.String[]"</c> for every caller, which merged every tenant into one.
    /// Numbers format with the invariant culture.
    /// </summary>
    public static string? ReadTenantValue(IDictionary<string, object?> userContext, string tenantContextKey)
    {
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));
        return userContext.TryGetValue(tenantContextKey, out var value) ? TenantScalar(value) : null;
    }

    private static string? TenantScalar(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case System.Text.Json.JsonElement element:
                return element.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Null => null,
                    System.Text.Json.JsonValueKind.String => element.GetString(),
                    System.Text.Json.JsonValueKind.Number => element.GetRawText(),
                    System.Text.Json.JsonValueKind.Array when element.GetArrayLength() == 1 => TenantScalar(element[0]),
                    _ => throw NonScalarTenant(),
                };
            case System.Collections.IEnumerable sequence:
            {
                var items = sequence.Cast<object?>().Take(2).ToList();
                if (items.Count != 1 || items[0] is System.Collections.IEnumerable and not string)
                    throw NonScalarTenant();
                return TenantScalar(items[0]);
            }
            case IFormattable formattable:
                return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            case IConvertible convertible:
                return convertible.ToString(System.Globalization.CultureInfo.InvariantCulture);
            default:
                // An opaque object (a principal, a profile) has no tenant text: its ToString() is
                // its type name, one value shared by every caller.
                throw NonScalarTenant();
        }
    }

    private static BifrostExecutionError NonScalarTenant()
        => new(NonScalarTenantMessage) { ErrorCode = BifrostExecutionError.AccessDeniedCode };
}

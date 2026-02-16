using System.Collections;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Injects automatic filters based on arbitrary user claim-to-column mappings.
///
/// Configuration via table metadata (multiple filters supported):
///   "dbo.orders { auto-filter: org_id:organization_id }"
///   "dbo.orders { auto-filter: org_id:organization_id,region_id:user_region }"
///
/// Each mapping is "column:claim" where:
///   - column: the database column to filter on
///   - claim: the key to look up in UserContext
///
/// Admin bypass via model metadata:
///   "BifrostQL:Metadata { auto-filter-bypass-role: admin }"
///
/// When configured, the bypass role is checked against UserContext["roles"].
/// If the user has the bypass role, auto-filters are skipped.
///
/// Array claim values produce IN filters instead of equality filters.
/// </summary>
public sealed class AutoFilterTransformer : IFilterTransformer
{
    public const string MetadataKey = "auto-filter";
    public const string BypassRoleMetadataKey = "auto-filter-bypass-role";
    public const string RolesContextKey = "roles";

    // Security: runs at priority 1, right after tenant filter (priority 0)
    public int Priority => 1;

    public bool AppliesTo(IDbTable table, QueryTransformContext context)
    {
        if (!table.Metadata.TryGetValue(MetadataKey, out var val) || val == null)
            return false;

        var mappingStr = val.ToString();
        if (string.IsNullOrWhiteSpace(mappingStr))
            return false;

        if (HasBypassRole(context))
            return false;

        return true;
    }

    public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
    {
        var mappingStr = table.Metadata[MetadataKey]?.ToString();
        if (string.IsNullOrWhiteSpace(mappingStr))
            return null;

        var fullTableName = $"{table.TableSchema}.{table.DbName}";
        var mappings = ParseMappings(mappingStr, fullTableName);

        TableFilter? combined = null;
        foreach (var mapping in mappings)
        {
            var filter = CreateFilterForMapping(table, context, mapping, fullTableName);
            combined = combined == null
                ? filter
                : new TableFilter
                {
                    And = new List<TableFilter> { combined, filter },
                    FilterType = FilterType.And,
                };
        }

        return combined;
    }

    private static TableFilter CreateFilterForMapping(
        IDbTable table,
        QueryTransformContext context,
        AutoFilterMapping mapping,
        string fullTableName)
    {
        // Verify the column exists
        if (!table.ColumnLookup.ContainsKey(mapping.Column))
        {
            throw new BifrostExecutionError(
                $"Auto-filter column '{mapping.Column}' not found in table '{fullTableName}'.");
        }

        // Get claim value from user context
        if (!context.UserContext.TryGetValue(mapping.Claim, out var claimValue))
        {
            throw new BifrostExecutionError(
                $"Auto-filter claim '{mapping.Claim}' required but not found in user context " +
                $"for column '{mapping.Column}' on table '{fullTableName}'.");
        }

        if (claimValue == null)
        {
            throw new BifrostExecutionError(
                $"Auto-filter claim '{mapping.Claim}' cannot be null " +
                $"for column '{mapping.Column}' on table '{fullTableName}'.");
        }

        // Array claims produce IN filters
        if (IsArrayClaim(claimValue))
        {
            var values = ToObjectList(claimValue);
            if (values.Count == 0)
            {
                throw new BifrostExecutionError(
                    $"Auto-filter claim '{mapping.Claim}' cannot be empty " +
                    $"for column '{mapping.Column}' on table '{fullTableName}'.");
            }
            return TableFilterFactory.In(table.DbName, mapping.Column, values);
        }

        return TableFilterFactory.Equals(table.DbName, mapping.Column, claimValue);
    }

    private bool HasBypassRole(QueryTransformContext context)
    {
        if (!context.Model.Metadata.TryGetValue(BypassRoleMetadataKey, out var bypassRole)
            || bypassRole is not string role
            || string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        if (!context.UserContext.TryGetValue(RolesContextKey, out var rolesValue) || rolesValue == null)
            return false;

        if (rolesValue is string singleRole)
            return string.Equals(singleRole, role, StringComparison.OrdinalIgnoreCase);

        if (rolesValue is IEnumerable<string> roleList)
            return roleList.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

        if (IsArrayClaim(rolesValue))
        {
            var items = ToObjectList(rolesValue);
            return items.Any(r => string.Equals(r?.ToString(), role, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    internal static List<AutoFilterMapping> ParseMappings(string mappingStr, string fullTableName)
    {
        var results = new List<AutoFilterMapping>();
        var parts = mappingStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var colonIndex = part.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= part.Length - 1)
            {
                throw new BifrostExecutionError(
                    $"Invalid auto-filter mapping '{part}' on table '{fullTableName}'. " +
                    $"Expected format 'column:claim'.");
            }

            var column = part[..colonIndex].Trim();
            var claim = part[(colonIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(column) || string.IsNullOrWhiteSpace(claim))
            {
                throw new BifrostExecutionError(
                    $"Invalid auto-filter mapping '{part}' on table '{fullTableName}'. " +
                    $"Column and claim must not be empty.");
            }

            results.Add(new AutoFilterMapping(column, claim));
        }

        if (results.Count == 0)
        {
            throw new BifrostExecutionError(
                $"No valid auto-filter mappings found on table '{fullTableName}'.");
        }

        return results;
    }

    private static bool IsArrayClaim(object value)
    {
        return value is IEnumerable and not string;
    }

    private static List<object?> ToObjectList(object value)
    {
        if (value is IEnumerable<object?> objEnumerable)
            return objEnumerable.ToList();

        var result = new List<object?>();
        foreach (var item in (IEnumerable)value)
            result.Add(item);
        return result;
    }
}

internal readonly record struct AutoFilterMapping(string Column, string Claim);

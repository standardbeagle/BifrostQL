using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Refuses an INSERT or UPDATE whose foreign key points at a row of another tenant.
///
/// <para><see cref="TenantMutationTransformer"/> pins the written row's OWN tenant column
/// and scopes its WHERE, but nothing checked the values a row REFERENCES: a tenant-1
/// caller could insert a contact whose <c>account_id</c> names tenant 2's account, and
/// every read that joins through that link then pairs tenant-1 rows with tenant-2 data
/// (and the FK constraint, which knows nothing of tenants, happily accepts it). This
/// transformer closes that half: for every database foreign key
/// (<see cref="TableLinkRelationshipKind.ForeignKey"/> — name-based and polymorphic links
/// are conventions, not constraints, and are not checked) whose PARENT table declares
/// <c>tenant-filter</c>, a write that supplies every child column of the link with a
/// non-null value must reference a parent row that exists and whose tenant column equals
/// the caller's tenant.</para>
///
/// <para>The probe runs on the write's own connection and transaction
/// (<see cref="MutationTransformContext.LoadRowForUpdateAsync"/>), takes the dialect's
/// update lock so the parent's tenancy cannot change before the write commits, and sees
/// parents inserted earlier in the same transaction (a tree-sync child of a freshly
/// inserted parent). A parent without <c>tenant-filter</c> (shared lookup data) is not
/// checked. There is no admin bypass — <see cref="TenantMutationTransformer"/> has none
/// either, and a missing or null tenant claim fails closed through the same resolution
/// it uses.</para>
///
/// <para>A missing parent and another tenant's parent answer with ONE constant message
/// carrying <see cref="BifrostExecutionError.AccessDeniedCode"/> and no table, column or
/// value (protocol-adapter-security.md invariants 3 and 9): a different answer would let
/// a caller probe which keys exist in other tenants. A NULL in any child column means
/// the row references nothing and is not checked; an UPDATE that supplies only some
/// columns of a composite link is refused, because the reference it produces mixes
/// supplied and stored values this check cannot see without reading the target row.</para>
///
/// <para>Priority 155: after <see cref="EnumValueMutationTransformer"/> (150), so a
/// lookup-enum foreign key is checked by the stored key it maps to, not the enum name the
/// client sent. Below the profile floor, so no profile can switch it off.</para>
/// </summary>
public sealed class TenantForeignKeyMutationTransformer : IMutationTransformer, IModuleNamed
{
    /// <summary>The one wire text for a refused reference — missing and foreign alike.</summary>
    public const string RefusalMessage = "A referenced row is not available.";

    public string ModuleName => "tenant-foreign-key";

    public int Priority => 155;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
        => mutationType is MutationType.Insert or MutationType.Update && RequiresWriteTransaction(table);

    /// <summary>The same predicate the body checks: the table has a tenant-scoped FK.</summary>
    public bool RequiresWriteTransaction(IDbTable table)
        => table.SingleLinks.Values.Any(IsTenantScopedForeignKey);

    public async ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        foreach (var link in table.SingleLinks.Values)
        {
            if (!IsTenantScopedForeignKey(link))
                continue;
            if (!await ReferenceAllowedAsync(link, mutationType, data, context))
                return Refused(mutationType, data);
        }
        return new MutationTransformResult { MutationType = mutationType, Data = data };
    }

    private static bool IsTenantScopedForeignKey(TableLinkDto link)
        => link.RelationshipKind == TableLinkRelationshipKind.ForeignKey
           && !string.IsNullOrWhiteSpace(link.ParentTable.GetMetadataValue(MetadataKeys.Security.TenantFilter));

    private static async ValueTask<bool> ReferenceAllowedAsync(
        TableLinkDto link, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
    {
        var childColumns = link.ChildIds;
        var parentColumns = link.ParentIds;
        // A link whose column lists do not pair up cannot be checked; refusing beats
        // guessing which parent column a child column means.
        if (childColumns.Count == 0 || childColumns.Count != parentColumns.Count)
            return false;

        var parentKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var supplied = 0;
        var anyNull = false;
        for (var i = 0; i < childColumns.Count; i++)
        {
            if (!data.TryGetValue(childColumns[i].ColumnName, out var value))
                continue;
            supplied++;
            if (value is null || value == DBNull.Value)
                anyNull = true;
            parentKey[parentColumns[i].ColumnName] = value;
        }

        // The write does not touch this link, or sets a NULL into it: no reference to check.
        if (supplied == 0 || anyNull)
            return true;
        if (supplied < childColumns.Count)
            // Insert: the unsupplied columns are NULL, so the row references nothing.
            // Update: they keep their stored values, which this check does not read.
            return mutationType == MutationType.Insert;

        var parent = link.ParentTable;
        var tenantColumn = parent.GetMetadataValue(MetadataKeys.Security.TenantFilter)!;
        var tenantId = TenantMutationTransformer.ResolveTenantId(parent, context);
        var row = await context.LoadRowForUpdateAsync(parent, new[] { tenantColumn }, parentKey);
        return row is not null
               && row.TryGetValue(tenantColumn, out var storedTenant)
               && TenantMutationTransformer.TenantValueMatches(storedTenant, tenantId);
    }

    private static MutationTransformResult Refused(MutationType mutationType, Dictionary<string, object?> data) =>
        new()
        {
            MutationType = mutationType,
            Data = data,
            Errors = new[] { RefusalMessage },
            ErrorCode = BifrostExecutionError.AccessDeniedCode,
        };
}

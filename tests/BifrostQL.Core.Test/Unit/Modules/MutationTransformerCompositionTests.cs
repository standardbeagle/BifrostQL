using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Characterization + composition tests for the mutation transformer pipeline
/// (<see cref="MutationTransformersWrap"/>). These lock the security-relevant
/// invariants the individual-transformer tests never assert together:
///   1. Transformers run in ascending Priority order.
///   2. AppliesTo is re-evaluated against the CURRENT (possibly rewritten)
///      mutation type each iteration — so soft-delete's DELETE→UPDATE rewrite at
///      priority 100 makes any delete-gated transformer above 100 silently skip.
///      This is a real footgun; the test documents it so a future reorder that
///      breaks a security transformer is caught here.
///   3. Multiple transformers' AdditionalFilters AND-combine (tenant row-scope +
///      soft-delete IS NULL) rather than one clobbering the other.
///   4. The documented priority bands (0-99 security, 100-199 data filtering,
///      200+ app) hold for the built-in transformers.
/// </summary>
public sealed class MutationTransformerCompositionTests
{
    // --- fixtures -----------------------------------------------------------

    private static IDbModel TenantSoftDeleteModel()
        => DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id", "int")
                .WithColumn("tenant_id", "int")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata(MetadataKeys.Security.TenantFilter, "tenant_id")
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at"))
            .Build();

    private static MutationTransformContext Context(IDbModel model, object? tenantId = null)
        => new()
        {
            Model = model,
            UserContext = tenantId == null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["tenant_id"] = tenantId },
        };

    /// <summary>
    /// Recording transformer that applies only to a chosen mutation type, so a
    /// test can prove whether it fired given where it sits relative to a rewrite.
    /// </summary>
    private sealed class RecordingTransformer : IMutationTransformer
    {
        private readonly MutationType _appliesToType;
        private readonly List<string> _log;
        public string Label { get; }
        public bool Ran { get; private set; }

        public RecordingTransformer(string label, int priority, MutationType appliesToType, List<string> log)
        {
            Label = label;
            Priority = priority;
            _appliesToType = appliesToType;
            _log = log;
        }

        public int Priority { get; }

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == _appliesToType;

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            Ran = true;
            _log.Add(Label);
            return ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
            });
        }
    }

    private static IReadOnlyList<string> CollectLeafColumns(TableFilter? filter)
    {
        var cols = new List<string>();
        void Walk(TableFilter? f)
        {
            if (f == null) return;
            if (f.And.Count > 0) { foreach (var c in f.And) Walk(c); return; }
            if (f.Or.Count > 0) { foreach (var c in f.Or) Walk(c); return; }
            if (!string.IsNullOrEmpty(f.ColumnName)) cols.Add(f.ColumnName);
        }
        Walk(filter);
        return cols;
    }

    // --- ordering -----------------------------------------------------------

    [Fact]
    public async Task Pipeline_RunsTransformersInAscendingPriorityOrder()
    {
        var model = TenantSoftDeleteModel();
        var table = model.GetTableFromDbName("Orders");
        var log = new List<string>();
        // Registered out of priority order on purpose.
        var wrap = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new RecordingTransformer("high", 200, MutationType.Insert, log),
                new RecordingTransformer("low", 1, MutationType.Insert, log),
                new RecordingTransformer("mid", 50, MutationType.Insert, log),
            },
        };

        await wrap.TransformAsync(table, MutationType.Insert,
            new Dictionary<string, object?>(), Context(model));

        log.Should().Equal("low", "mid", "high");
    }

    // --- the DELETE→UPDATE gating hazard ------------------------------------

    [Fact]
    public async Task Pipeline_DeleteGatedTransformerAbovePriority100_SkipsAfterSoftDeleteRewrite()
    {
        // A delete-gated transformer sitting ABOVE soft-delete (priority 150 > 100)
        // never fires on a soft-deleted row: soft-delete rewrote DELETE→UPDATE, and
        // AppliesTo is re-checked against the new (Update) type. This is the footgun
        // — a security transformer that must see deletes MUST sit below priority 100.
        var model = TenantSoftDeleteModel();
        var table = model.GetTableFromDbName("Orders");
        var log = new List<string>();
        var deleteGatedAbove = new RecordingTransformer("above", 150, MutationType.Delete, log);
        var wrap = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer(),
                deleteGatedAbove,
            },
        };

        var result = await wrap.TransformAsync(table, MutationType.Delete,
            new Dictionary<string, object?>(), Context(model, tenantId: 7));

        result.MutationType.Should().Be(MutationType.Update, "soft-delete rewrote the DELETE");
        deleteGatedAbove.Ran.Should().BeFalse(
            "a delete-gated transformer above priority 100 is skipped once soft-delete has rewritten the type");
    }

    [Fact]
    public async Task Pipeline_DeleteGatedTransformerBelowPriority100_FiresBeforeSoftDeleteRewrite()
    {
        // The correct placement: below 100, the delete-gated transformer sees the
        // original DELETE before soft-delete rewrites it. (Audit@50 relies on this.)
        var model = TenantSoftDeleteModel();
        var table = model.GetTableFromDbName("Orders");
        var log = new List<string>();
        var deleteGatedBelow = new RecordingTransformer("below", 50, MutationType.Delete, log);
        var wrap = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer(),
                deleteGatedBelow,
            },
        };

        var result = await wrap.TransformAsync(table, MutationType.Delete,
            new Dictionary<string, object?>(), Context(model, tenantId: 7));

        deleteGatedBelow.Ran.Should().BeTrue(
            "a delete-gated transformer below priority 100 sees the DELETE before the rewrite");
        result.MutationType.Should().Be(MutationType.Update);
    }

    // --- AdditionalFilter composition (tenant row-scope + soft-delete) ------

    [Fact]
    public async Task Pipeline_TenantAndSoftDeleteOnDelete_CombinesBothFilters()
    {
        // Tenant (priority 0) scopes the WHERE to the caller's tenant; soft-delete
        // (100) rewrites DELETE→UPDATE and adds deleted_at IS NULL. The two
        // AdditionalFilters must AND-combine, not clobber each other — otherwise a
        // caller could soft-delete another tenant's row, or re-delete an already
        // soft-deleted one.
        var model = TenantSoftDeleteModel();
        var table = model.GetTableFromDbName("Orders");
        var wrap = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new TenantMutationTransformer(),
                new SoftDeleteMutationTransformer(),
            },
        };

        var result = await wrap.TransformAsync(table, MutationType.Delete,
            new Dictionary<string, object?>(), Context(model, tenantId: 7));

        result.MutationType.Should().Be(MutationType.Update);
        result.AdditionalFilter.Should().NotBeNull();
        result.AdditionalFilter!.FilterType.Should().Be(FilterType.And);
        var columns = CollectLeafColumns(result.AdditionalFilter);
        columns.Should().Contain("tenant_id", "tenant row-scope must survive composition");
        columns.Should().Contain("deleted_at", "soft-delete IS NULL guard must survive composition");
    }

    // --- documented priority bands ------------------------------------------

    [Theory]
    [InlineData(typeof(TenantFilterTransformer), 0)]
    [InlineData(typeof(TenantMutationTransformer), 0)]
    [InlineData(typeof(AuditMutationTransformer), 50)]
    [InlineData(typeof(SoftDeleteMutationTransformer), 100)]
    [InlineData(typeof(SoftDeleteFilterTransformer), 100)]
    public void BuiltInTransformers_HoldTheirDocumentedPriority(System.Type transformerType, int expectedPriority)
    {
        // Locks the priority contract the bands and the DELETE→UPDATE ordering
        // depend on: security 0-99, data filtering 100-199. Audit MUST stay below
        // SoftDelete (see the gating tests above).
        dynamic transformer = System.Activator.CreateInstance(transformerType)!;
        ((int)transformer.Priority).Should().Be(expectedPriority);
    }

    [Fact]
    public void Audit_RunsBeforeSoftDelete_ToStampDeletesBeforeTheRewrite()
    {
        new AuditMutationTransformer().Priority
            .Should().BeLessThan(new SoftDeleteMutationTransformer().Priority,
                "audit must stamp deleted_* on the original DELETE before soft-delete rewrites it to UPDATE");
    }
}

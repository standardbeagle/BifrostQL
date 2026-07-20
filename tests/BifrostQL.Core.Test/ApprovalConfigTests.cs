using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Approval;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for Approval slice 1 — the metadata contract parsed from <c>approval</c> /
/// <c>approver-role</c> / <c>self-approve</c> into a typed <see cref="ApprovalConfig"/>, the
/// allow-list registration that makes a miscased key a HARD error, and the pending_changes
/// lifecycle pinned through the existing state-machine module. The write-interception slice
/// consumes this config, so its parse and fail-fast behavior are pinned here.
///
/// The load-bearing fail direction under test: a half-resolved gate FAILS CLOSED (does not
/// load), never leaves the table ungated. A silently-ignored <c>approval</c> key would apply
/// writes UNGATED (fail-open) — the exact bug the allow-list catches.
/// </summary>
public class ApprovalConfigTests
{
    private static IDbTable TableWithMetadata(params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in metadata)
            dict[key] = value;

        table.DbName.Returns("orders");
        table.TableSchema.Returns("dbo");
        table.Metadata.Returns(dict);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);

        return table;
    }

    // --- Criterion 2: None path + fully-resolved config, no partial config. ---

    [Fact]
    public void FromTable_NoApprovalKey_ReturnsNone()
    {
        var table = TableWithMetadata();

        var config = ApprovalConfig.FromTable(table);

        config.RequiresApproval.Should().BeFalse();
        config.ApproverRole.Should().BeNull();
        config.Should().BeSameAs(ApprovalConfig.None);
    }

    [Fact]
    public void FromTable_ApprovalWithApproverRole_ResolvesFully()
    {
        var table = TableWithMetadata(
            (MetadataKeys.Approval.Marker, MetadataKeys.Approval.Enabled),
            (MetadataKeys.Approval.ApproverRole, "manager"));

        var config = ApprovalConfig.FromTable(table);

        config.RequiresApproval.Should().BeTrue();
        config.ApproverRole.Should().Be("manager");
        // self-approve defaults TRUE unless declared false.
        config.SelfApprove.Should().BeTrue();
    }

    [Fact]
    public void FromTable_SelfApproveFalse_IsHonored()
    {
        var table = TableWithMetadata(
            (MetadataKeys.Approval.Marker, MetadataKeys.Approval.Enabled),
            (MetadataKeys.Approval.ApproverRole, "manager"),
            (MetadataKeys.Approval.SelfApprove, "false"));

        var config = ApprovalConfig.FromTable(table);

        config.SelfApprove.Should().BeFalse();
    }

    // --- Criterion 4: missing approver-role is a config error (fail-open otherwise). ---

    [Fact]
    public void FromTable_ApprovalWithoutApproverRole_Throws()
    {
        // A gate nobody can approve is fail-open — it must not load.
        var table = TableWithMetadata(
            (MetadataKeys.Approval.Marker, MetadataKeys.Approval.Enabled));

        var act = () => ApprovalConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Approval.ApproverRole}*");
    }

    [Fact]
    public void FromTable_ApprovalNotEnabled_Throws()
    {
        var table = TableWithMetadata(
            (MetadataKeys.Approval.Marker, "true"),
            (MetadataKeys.Approval.ApproverRole, "manager"));

        var act = () => ApprovalConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Approval.Enabled}*");
    }

    [Fact]
    public void FromTable_SelfApproveUnrecognized_Throws()
    {
        var table = TableWithMetadata(
            (MetadataKeys.Approval.Marker, MetadataKeys.Approval.Enabled),
            (MetadataKeys.Approval.ApproverRole, "manager"),
            (MetadataKeys.Approval.SelfApprove, "maybe"));

        var act = () => ApprovalConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Approval.SelfApprove}*");
    }

    // --- Criterion 1: a miscased Approval key is a HARD ModelConfigValidator error
    // (never a silently-ungated table). ---

    [Fact]
    public void Validate_MiscasedApprovalKey_IsHardError()
    {
        // "Approval" (capital A) matches the allow-list case-insensitively but the metadata
        // dictionary is case-sensitive, so every module that reads 'approval' would miss it —
        // leaving writes UNGATED. The casing gate must reject it at model load.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata("Approval", MetadataKeys.Approval.Enabled)
                .WithMetadata(MetadataKeys.Approval.ApproverRole, "manager"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Approval").And.Contain("casing");
    }

    [Fact]
    public void Validate_ApprovalWithoutApproverRole_IsHardError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Approval.Marker, MetadataKeys.Approval.Enabled))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.Orders")
            .And.Contain(MetadataKeys.Approval.ApproverRole);
    }

    // --- Criterion 3: the pending_changes lifecycle is expressed via the state-machine
    // module's metadata and enforced by StateMachineMutationTransformer; NO second enum. ---

    [Fact]
    public void PendingChangeStateModel_IsParsedByStateMachineCollector()
    {
        // The store's lifecycle metadata feeds the EXISTING collector — proving it is a real
        // state-machine declaration, not a hand-rolled enum.
        var table = StoreTable();

        var definition = StateMachineConfigCollector.FromTable(table);

        definition.Should().NotBeNull();
        definition!.StateColumn.Should().Be(PendingChangeStore.ColState);
        definition.InitialState.Should().Be(PendingChangeStore.StatePending);
        definition.States.Should().BeEquivalentTo(new[]
        {
            PendingChangeStore.StatePending,
            PendingChangeStore.StateApproved,
            PendingChangeStore.StateRejected,
            PendingChangeStore.StateExpired,
        });
    }

    [Fact]
    public async Task PendingChangeStateModel_IllegalTransition_RejectedByStateMachineTransformer()
    {
        // approved -> pending is not a declared transition (terminal states have no outgoing
        // edges), so the EXISTING transformer must deny it — the lifecycle is enforced by
        // machinery that is already tested, not by new hand-rolled checks.
        var table = StoreTable();
        var transformer = new StateMachineMutationTransformer();
        var context = new MutationTransformContext
        {
            Model = Substitute.For<IDbModel>(),
            UserContext = new Dictionary<string, object?>
            {
                [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
                [MetadataKeys.Auth.DefaultRolesContextKey] = Array.Empty<string>(),
            },
            CurrentRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [PendingChangeStore.ColState] = PendingChangeStore.StateApproved,
            },
        };
        var data = new Dictionary<string, object?>
        {
            [PendingChangeStore.ColState] = PendingChangeStore.StatePending,
        };

        var result = await transformer.TransformAsync(table, MutationType.Update, data, context);

        result.Errors.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PendingChangeStateModel_LegalTransition_AllowedByStateMachineTransformer()
    {
        // pending -> approved is declared, so the transformer permits it.
        var table = StoreTable();
        var transformer = new StateMachineMutationTransformer();
        var context = new MutationTransformContext
        {
            Model = Substitute.For<IDbModel>(),
            UserContext = new Dictionary<string, object?>
            {
                [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
                [MetadataKeys.Auth.DefaultRolesContextKey] = Array.Empty<string>(),
            },
            CurrentRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [PendingChangeStore.ColState] = PendingChangeStore.StatePending,
            },
        };
        var data = new Dictionary<string, object?>
        {
            [PendingChangeStore.ColState] = PendingChangeStore.StateApproved,
        };

        var result = await transformer.TransformAsync(table, MutationType.Update, data, context);

        result.Errors.Should().BeNullOrEmpty();
    }

    // A substitute store table carrying the pinned state-machine metadata + its key column.
    private static IDbTable StoreTable()
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in PendingChangeStore.StateMachineMetadata())
            dict[key] = value;

        table.DbName.Returns("pending_changes");
        table.TableSchema.Returns("dbo");
        table.Metadata.Returns(dict);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);
        table.KeyColumns.Returns(new[]
        {
            new ColumnDto
            {
                ColumnName = PendingChangeStore.ColId,
                GraphQlName = PendingChangeStore.ColId,
                NormalizedName = ColumnDto.NormalizeColumn(PendingChangeStore.ColId),
                DataType = "int",
            },
        });
        return table;
    }
}

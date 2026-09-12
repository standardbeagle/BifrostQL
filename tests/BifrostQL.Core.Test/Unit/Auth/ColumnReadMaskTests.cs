using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Auth;

/// <summary>
/// RED/GREEN TDD coverage for S4b read-side column grants with masking: the
/// column-selector <c>read-requires</c> key, the <c>deny-mode: null|refuse</c>
/// key, and their enforcement through <see cref="PolicyEvaluator.GetReadDisposition"/>
/// and <see cref="PolicyFilterTransformer"/>.
///
/// Masking is the DEFAULT for <c>read-requires</c> (denied → null in the
/// selection, still refused as a filter/sort/aggregate input). Existing
/// <c>policy-read-deny</c> keeps REFUSE semantics unless <c>deny-mode: null</c>
/// is set, so nothing shipped changes behaviour.
/// </summary>
public sealed class ColumnReadMaskTests
{
    private static QueryTransformContext Context(
        IDbModel model, IDictionary<string, object?>? userContext = null) =>
        new()
        {
            Model = model,
            UserContext = userContext ?? new Dictionary<string, object?>(),
            QueryType = QueryType.Standard,
            Path = "",
            IsNestedQuery = false,
        };

    private static IDictionary<string, object?> UserWithRoles(params string[] roles) =>
        new Dictionary<string, object?>
        {
            ["user_id"] = "user-1",
            ["roles"] = roles,
        };

    private static IDbModel ModelWithMembers(
        string? readRequires = "rates.view_cost",
        string? columnDenyMode = null,
        params (string key, string value)[] tableMetadata)
    {
        var builder = DbModelTestFixture.Create()
            .WithTable("members", t =>
            {
                t.WithSchema("public")
                    .WithPrimaryKey("id")
                    .WithColumn("cost_rate", "decimal")
                    .WithColumn("display_name")
                    .WithMetadata(MetadataKeys.Policy.Actions, "read,update");
                if (readRequires is not null)
                    t.WithColumnMetadata("cost_rate", MetadataKeys.Policy.ReadRequires, readRequires);
                if (columnDenyMode is not null)
                    t.WithColumnMetadata("cost_rate", MetadataKeys.Policy.DenyMode, columnDenyMode);
                foreach (var (key, value) in tableMetadata)
                    t.WithMetadata(key, value);
            });
        return builder.Build();
    }

    private static IDbTable Members(IDbModel model) => model.GetTableFromDbName("members");

    private static AppIdentity Identity(params string[] roles) =>
        new("user-1", "local", roles: roles);

    // ---- PolicyConfigCollector ----

    [Fact]
    public void Collector_ParsesReadRequires_FromColumnSelectorMetadata()
    {
        var model = ModelWithMembers();

        var policy = PolicyConfigCollector.FromTable(Members(model));

        policy.HasPolicy.Should().BeTrue();
        policy.ReadRequires.Should().ContainKey("cost_rate");
        policy.ReadRequires["cost_rate"].Should().BeEquivalentTo("rates.view_cost");
    }

    [Fact]
    public void Collector_ParsesDenyMode_ColumnAndTable()
    {
        var model = ModelWithMembers(
            columnDenyMode: "refuse",
            tableMetadata: (MetadataKeys.Policy.DenyMode, "null"));

        var policy = PolicyConfigCollector.FromTable(Members(model));

        policy.ColumnDenyModes.Should().ContainKey("cost_rate");
        policy.ColumnDenyModes["cost_rate"].Should().Be("refuse");
        policy.TableDenyMode.Should().Be("null");
    }

    // ---- PolicyEvaluator.GetReadDisposition ----

    [Fact]
    public void Disposition_ReadRequires_MemberWithoutGrant_Masks()
    {
        var model = ModelWithMembers();
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "cost_rate", Identity("member"))
            .Should().Be(ReadColumnDisposition.Mask);
    }

    [Fact]
    public void Disposition_ReadRequires_GrantHolder_Allows()
    {
        var model = ModelWithMembers();
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "cost_rate", Identity("member", "rates.view_cost"))
            .Should().Be(ReadColumnDisposition.Allow);
    }

    [Fact]
    public void Disposition_ReadRequires_AnyListedGrantPasses()
    {
        var model = ModelWithMembers(readRequires: "rates.view_cost, team.manage");
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "cost_rate", Identity("team.manage"))
            .Should().Be(ReadColumnDisposition.Allow);
    }

    [Fact]
    public void Disposition_ReadRequires_DenyModeRefuse_Refuses()
    {
        var model = ModelWithMembers(columnDenyMode: "refuse");
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "cost_rate", Identity("member"))
            .Should().Be(ReadColumnDisposition.Refuse);
    }

    [Fact]
    public void Disposition_UngatedColumn_Allows()
    {
        var model = ModelWithMembers();
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "display_name", Identity("member"))
            .Should().Be(ReadColumnDisposition.Allow);
    }

    [Fact]
    public void Disposition_Admin_Allows()
    {
        var model = ModelWithMembers();
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "cost_rate", Identity("admin"))
            .Should().Be(ReadColumnDisposition.Allow);
    }

    [Fact]
    public void Disposition_PolicyReadDeny_DefaultStaysRefuse()
    {
        // Shipped behaviour: policy-read-deny throws unless deny-mode: null is set.
        var model = ModelWithMembers(
            readRequires: null,
            tableMetadata: (MetadataKeys.Policy.ReadDeny, "cost_rate"));
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "cost_rate", Identity("member"))
            .Should().Be(ReadColumnDisposition.Refuse);
    }

    [Fact]
    public void Disposition_PolicyReadDeny_DenyModeNull_Masks()
    {
        var model = ModelWithMembers(
            readRequires: null,
            columnDenyMode: "null",
            tableMetadata: (MetadataKeys.Policy.ReadDeny, "cost_rate"));
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "cost_rate", Identity("member"))
            .Should().Be(ReadColumnDisposition.Mask);
    }

    [Fact]
    public void Disposition_PolicyReadDeny_TableDenyModeNull_Masks()
    {
        var model = ModelWithMembers(
            readRequires: null,
            tableMetadata: new[]
            {
                (MetadataKeys.Policy.ReadDeny, "cost_rate"),
                (MetadataKeys.Policy.DenyMode, "null"),
            });
        var policy = PolicyConfigCollector.FromTable(Members(model));

        new PolicyEvaluator().GetReadDisposition(policy, "cost_rate", Identity("member"))
            .Should().Be(ReadColumnDisposition.Mask);
    }

    // ---- PolicyFilterTransformer ----

    [Fact]
    public void Transformer_MaskedColumns_ReturnsDeniedMaskedSet()
    {
        var model = ModelWithMembers();
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("member"));

        var masked = transformer.MaskedColumns(
            Members(model), new[] { "id", "cost_rate", "display_name" }, context);

        masked.Should().BeEquivalentTo("cost_rate");
    }

    [Fact]
    public void Transformer_AssertColumnsReadable_MaskedColumnSelected_DoesNotThrow()
    {
        // Masking is for selection: selecting a masked column is a 200 with null,
        // not an error. The throw survives only for refuse-mode denies.
        var model = ModelWithMembers();
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("member"));

        var act = () => transformer.AssertColumnsReadable(
            Members(model), new[] { "id", "cost_rate" }, context);

        act.Should().NotThrow();
    }

    [Fact]
    public void Transformer_AssertColumnsReadable_RefuseMode_StillThrowsNonLeaking()
    {
        var model = ModelWithMembers(columnDenyMode: "refuse");
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("member"));

        var ex = Assert.Throws<BifrostExecutionError>(() =>
            transformer.AssertColumnsReadable(Members(model), new[] { "cost_rate" }, context));

        ex.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        ex.Message.Should().NotContain("cost_rate");
        ex.Message.Should().NotContain("members");
    }

    [Fact]
    public void Transformer_MaskedColumns_GrantHolder_EmptySet()
    {
        var model = ModelWithMembers();
        var transformer = new PolicyFilterTransformer();
        var context = Context(model, UserWithRoles("rates.view_cost"));

        transformer.MaskedColumns(Members(model), new[] { "cost_rate" }, context)
            .Should().BeEmpty();
    }
}

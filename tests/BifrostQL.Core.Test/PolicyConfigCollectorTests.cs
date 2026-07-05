using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for sub-task 1/4 of the Membership Manager policy
/// work: the table-level role→permission grant matrix expressed as
/// <c>policy-actions</c> metadata.
///
/// These tests pin the documented matrix (the same matrix written into the
/// header comments of membership-manager-seed-sample.sql) and assert that
/// <see cref="PolicyConfigCollector.FromTable"/> turns each MM table's
/// <c>policy-actions</c> value into a usable <see cref="TablePolicy"/>.
///
/// Scope guard: this is table-level grants only. Field-level deny lists and
/// row-scope expressions are sub-tasks 2 and 3 — they are intentionally not
/// asserted here.
/// </summary>
public class PolicyConfigCollectorTests
{
    /// <summary>
    /// The canonical Membership Manager table-level <c>policy-actions</c> grant
    /// matrix. This is the single source of truth the seed-sample SQL header
    /// comments document. The policy engine consumes the per-table allowed
    /// action set; the 6-role catalog (admin / officer / event_manager /
    /// finance_manager / member / read_only) and its per-table grants live as
    /// <c>role_permissions</c> seed rows — they are exercised by the seed, not
    /// the collector.
    ///
    /// admin bypasses policy entirely (MetadataKeys.Policy.DefaultAdminRole) so
    /// it needs no per-table entry. read_only is covered by every table
    /// permitting <c>read</c>. The values below are the union of actions any
    /// non-admin role may perform on the table through generated CRUD.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MembershipManagerPolicyActions =
        new Dictionary<string, string>
        {
            // Membership domain — officers manage the full member lifecycle.
            ["members"] = "read,create,update,delete",
            ["households"] = "read,create,update,delete",
            ["household_members"] = "read,create,update,delete",
            ["membership_plans"] = "read,create,update,delete",
            ["member_memberships"] = "read,create,update,delete",
            // Finance domain — finance_manager creates and edits, no destructive delete.
            ["dues_invoices"] = "read,create,update",
            ["dues_payments"] = "read,create,update",
            // Events domain — event_manager runs events end to end.
            ["events"] = "read,create,update,delete",
            ["event_rsvps"] = "read,create,update,delete",
            ["event_attendance"] = "read,create,update,delete",
            // Audit log — append-only via server workflows; read-only through CRUD.
            ["audit_log"] = "read",
            // Org-model foundation — read-only reference data through generated CRUD.
            ["roles"] = "read",
            ["role_permissions"] = "read",
            ["organization_memberships"] = "read,update",
            ["tenants"] = "read",
        };

    private static IDbTable TableWithMetadata(string dbName, params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in metadata)
            dict[key] = value;
        table.DbName.Returns(dbName);
        table.TableSchema.Returns("main");
        table.Metadata.Returns(dict);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);
        return table;
    }

    [Fact]
    public void EveryMembershipManagerTable_HasADocumentedGrant()
    {
        // The matrix must cover every table named in the task scope. If a table
        // is added to the schema, it must get an explicit grant decision here.
        var expected = new[]
        {
            "members", "households", "household_members", "membership_plans",
            "member_memberships", "dues_invoices", "dues_payments", "events",
            "event_rsvps", "event_attendance", "audit_log", "roles",
            "role_permissions", "organization_memberships", "tenants",
        };

        MembershipManagerPolicyActions.Keys.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Collector_ProducesATablePolicy_ForEveryMembershipManagerTable()
    {
        foreach (var (tableName, policyActions) in MembershipManagerPolicyActions)
        {
            var table = TableWithMetadata(tableName,
                (MetadataKeys.Policy.Actions, policyActions));

            var policy = PolicyConfigCollector.FromTable(table);

            policy.HasPolicy.Should().BeTrue(
                "table '{0}' carries a policy-actions grant", tableName);
            policy.AllowedActions.Should().NotBeEmpty(
                "table '{0}' grants at least one action", tableName);
        }
    }

    [Fact]
    public void Collector_GrantsReadToEveryTable_SoReadOnlyRoleCanSeeEverything()
    {
        // read_only is the floor role: every MM table must permit Read so the
        // read_only grant set is satisfiable table-by-table.
        foreach (var (tableName, policyActions) in MembershipManagerPolicyActions)
        {
            var table = TableWithMetadata(tableName,
                (MetadataKeys.Policy.Actions, policyActions));

            var policy = PolicyConfigCollector.FromTable(table);

            policy.AllowedActions.Should().Contain(PolicyAction.Read,
                "read_only must be able to read '{0}'", tableName);
        }
    }

    [Fact]
    public void Collector_ParsesFinanceTables_AsCreateUpdateNoDelete()
    {
        // Finance tables intentionally withhold delete at the table level —
        // dues records are corrected, never destroyed.
        foreach (var tableName in new[] { "dues_invoices", "dues_payments" })
        {
            var table = TableWithMetadata(tableName,
                (MetadataKeys.Policy.Actions, MembershipManagerPolicyActions[tableName]));

            var policy = PolicyConfigCollector.FromTable(table);

            policy.AllowedActions.Should().BeEquivalentTo(new[]
            {
                PolicyAction.Read, PolicyAction.Create, PolicyAction.Update,
            }, "finance table '{0}' allows correction but not deletion", tableName);
        }
    }

    [Fact]
    public void Collector_ParsesAuditLog_AsReadOnly()
    {
        var table = TableWithMetadata("audit_log",
            (MetadataKeys.Policy.Actions, MembershipManagerPolicyActions["audit_log"]));

        var policy = PolicyConfigCollector.FromTable(table);

        policy.AllowedActions.Should().BeEquivalentTo(new[] { PolicyAction.Read });
    }

    [Fact]
    public void Collector_ParsesGlobalLookupTables_AsReadOnly()
    {
        // roles / role_permissions are global reference data — never mutated
        // through generated CRUD.
        foreach (var tableName in new[] { "roles", "role_permissions" })
        {
            var table = TableWithMetadata(tableName,
                (MetadataKeys.Policy.Actions, MembershipManagerPolicyActions[tableName]));

            var policy = PolicyConfigCollector.FromTable(table);

            policy.AllowedActions.Should().BeEquivalentTo(new[] { PolicyAction.Read },
                "global lookup table '{0}' is read-only through CRUD", tableName);
        }
    }

    [Fact]
    public void Collector_UnknownActionToken_Throws()
    {
        // A typo'd action token (e.g. SQL-style "select,insert") must fail rather
        // than be silently dropped. If it were the only policy metadata on a table,
        // an empty allow-list collapses HasPolicy to false, which the evaluator
        // treats as "no policy = unrestricted" — a silent fail-OPEN.
        var table = TableWithMetadata("orders",
            (MetadataKeys.Policy.Actions, "select,insert"));

        var act = () => PolicyConfigCollector.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown policy action*");
    }

    [Fact]
    public void Collector_ValidActionAmongTypos_StillThrows()
    {
        // Even a partially-valid list must fail on the first bad token so the
        // operator learns the grant is not what they wrote.
        var table = TableWithMetadata("orders",
            (MetadataKeys.Policy.Actions, "read,destroy"));

        var act = () => PolicyConfigCollector.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*destroy*");
    }
}

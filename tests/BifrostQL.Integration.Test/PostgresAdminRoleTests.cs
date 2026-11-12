using System.Text.Json;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// The policy bypass belongs to the role a model names in <c>:root { policy-admin-role }</c>, and
/// to no role when it names none. A role claim spelled <c>admin</c> is an ordinary grant: an
/// identity provider issues that word for its own administrators, and it must not open every
/// table here. Each fact drives the read, the write and the <c>_dbSchema</c> listing in one go,
/// so the seams are shown to agree on who the administrator is.
/// </summary>
public sealed class PostgresAdminRoleTests
{
    private const string Ddl =
        """
        CREATE TABLE secrets (id SERIAL PRIMARY KEY, body TEXT NOT NULL);
        INSERT INTO secrets (body) VALUES ('seed');
        """;

    private const string Policy = "public.secrets { policy-actions: read[staff],create[staff] }";

    [SkippableFact]
    public async Task AnAdminClaim_IsAnOrdinaryGrant_WhenTheModelNamesNoAdminRole()
    {
        Skip.If(PostgresGraphQlHost.Master is null, PostgresGraphQlHost.SkipReason);
        await using var host = await PostgresGraphQlHost.StartAsync("bifrost_admin_role", Ddl, new[] { Policy });

        (await AccessAsync(host, "admin")).Should().Be(Access.None, "a claim named admin grants nothing the model did not grant");
        (await AccessAsync(host, "staff")).Should().Be(Access.All, "control: the granted role reads, writes and lists the table");
        (await host.CountAsync("SELECT count(*) FROM secrets")).Should().Be(2, "only the staff insert landed");
    }

    [SkippableFact]
    public async Task TheModelsAdminRole_BypassesEverySeam_AndAnAdminClaimStillDoesNot()
    {
        Skip.If(PostgresGraphQlHost.Master is null, PostgresGraphQlHost.SkipReason);
        await using var host = await PostgresGraphQlHost.StartAsync(
            "bifrost_admin_role", Ddl, new[] { ":root { policy-admin-role: ops }", Policy });

        (await AccessAsync(host, "ops")).Should().Be(Access.All, "the named admin role bypasses the grant on every seam");
        (await AccessAsync(host, "admin")).Should().Be(Access.None, "a claim named admin is not the named role");
        (await host.CountAsync("SELECT count(*) FROM secrets")).Should().Be(2, "only the ops insert landed");
    }

    private enum Access { None, All, Mixed }

    /// <summary>Reads, inserts into and lists <c>secrets</c> as a caller holding <paramref name="role"/>.</summary>
    private static async Task<Access> AccessAsync(PostgresGraphQlHost host, string role)
    {
        var read = Succeeded(await host.PostAsync("{ secrets { data { id body } } }", "user-1", role));
        var write = Succeeded(await host.PostAsync("""mutation { secrets(insert: { body: "written" }) }""", "user-1", role));
        var listed = Listed(await host.PostAsync("{ _dbSchema { dbName } }", "user-1", role));

        return (read, write, listed) switch
        {
            (true, true, true) => Access.All,
            (false, false, false) => Access.None,
            _ => Access.Mixed,
        };
    }

    private static bool Succeeded(JsonDocument reply) => !reply.RootElement.TryGetProperty("errors", out _);

    private static bool Listed(JsonDocument reply)
    {
        PostgresGraphQlHost.AssertNoErrors(reply);
        return reply.RootElement.GetProperty("data").GetProperty("_dbSchema").EnumerateArray()
            .Any(table => table.GetProperty("dbName").GetString() == "secrets");
    }
}

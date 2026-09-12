using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Loads a two-table SQLite model through the production <see cref="DbModelLoader"/>
/// so policy facts are proven on a model that went through
/// <see cref="MetadataLoader"/>, <see cref="DbModel.FromTables"/> and
/// <c>ModelConfigValidator</c> — the path a real deployment takes. Unit fixtures
/// hand-build tables and hand-stamp metadata, which cannot show whether
/// <c>:root { policy-default: deny }</c> ever reaches a table at all.
/// </summary>
public sealed class PolicyDefaultDenyModel : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;

    private PolicyDefaultDenyModel(SqliteConnection keepAlive, IDbModel model)
    {
        _keepAlive = keepAlive;
        Model = model;
    }

    public IDbModel Model { get; }

    /// <summary>Table carrying an <c>id</c> column, so <c>has(id)</c> selectors match it.</summary>
    public const string DeclaredTable = "secret";

    public static async Task<PolicyDefaultDenyModel> LoadAsync(params string[] metadata)
    {
        var connectionString =
            $"Data Source=bifrost_policy_default_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        try
        {
            await using (var conn = new SqliteConnection(connectionString))
            {
                await conn.OpenAsync();
                await using var ddl = new SqliteCommand(
                    @"CREATE TABLE secret (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        body TEXT NULL
                    );", conn);
                await ddl.ExecuteNonQueryAsync();
            }

            var loader = new DbModelLoader(
                new SqliteDbConnFactory(connectionString), new MetadataLoader(metadata));
            return new PolicyDefaultDenyModel(keepAlive, await loader.LoadAsync());
        }
        catch
        {
            await keepAlive.DisposeAsync();
            throw;
        }
    }

    public IDbTable Table() => Model.GetTableFromDbName(DeclaredTable);

    public TablePolicy Policy() => PolicyConfigCollector.FromTable(Table());

    public ValueTask DisposeAsync() => _keepAlive.DisposeAsync();
}

/// <summary>
/// Loader-path proof that <c>:root { policy-default: deny }</c> — the documented
/// rule-string form, which arrives through <c>MetadataLoader.ApplyDatabaseMetadata</c>
/// rather than the <c>additionalMetadata</c> dictionary — reaches every table and
/// therefore every policy consumer.
/// </summary>
public sealed class PolicyDefaultDenyLoaderPathTests
{
    private static AppIdentity NonAdmin() => new("u", "test");

    private static AppIdentity Admin() =>
        new("u", "test", roles: new[] { MetadataKeys.Policy.DefaultAdminRole });

    [Fact]
    public async Task Loader_RootPolicyDefaultDeny_DeniesUndeclaredTableForNonAdmin()
    {
        await using var loaded = await PolicyDefaultDenyModel.LoadAsync(
            ":root { policy-default: deny }");

        var policy = loaded.Policy();
        var evaluator = new PolicyEvaluator();

        policy.HasPolicy.Should().BeTrue(
            "the root deny default must be stamped onto every table by the loader");
        evaluator.CanAct(policy, PolicyAction.Read, NonAdmin())
            .Allowed.Should().BeFalse();
        evaluator.CanAct(policy, PolicyAction.Read, Admin())
            .Allowed.Should().BeTrue("the admin role still bypasses policy");
    }

    [Fact]
    public async Task Loader_MisspelledPolicyDefault_FailsLoadWithValidatorMessage()
    {
        var load = async () => await PolicyDefaultDenyModel.LoadAsync(
            ":root { policy-default: dney }");

        (await load.Should().ThrowAsync<Exception>())
            .WithMessage("*value must be 'allow' or 'deny'*");
    }

    [Fact]
    public async Task Loader_SelectorDeclaredTableUnderDeny_AllowsReadDeniesUpdate()
    {
        await using var loaded = await PolicyDefaultDenyModel.LoadAsync(
            ":root { policy-default: deny }",
            "main.*|has(id) { policy-actions: read }");

        var policy = loaded.Policy();
        var evaluator = new PolicyEvaluator();

        policy.HasPolicy.Should().BeTrue();
        evaluator.CanAct(policy, PolicyAction.Read, NonAdmin())
            .Allowed.Should().BeTrue("a selector rule counts as a declaration");
        evaluator.CanAct(policy, PolicyAction.Update, NonAdmin())
            .Allowed.Should().BeFalse("policy-actions omitted update");
    }
}

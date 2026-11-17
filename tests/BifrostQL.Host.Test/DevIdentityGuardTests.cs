using BifrostQL.Core.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BifrostQL.Host.Test;

/// <summary>
/// BifrostQL:DevIdentity stamps one fixed authenticated identity on every request. The guard
/// used to refuse only the Production environment, so Staging (or any custom name) ran with it.
/// </summary>
public sealed class DevIdentityGuardTests
{
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("qa-eu")]
    public void DevIdentity_OutsideDevelopment_FailsStartup(string environment)
    {
        using var factory = new DevIdentityFactory(environment);

        var boot = () => factory.CreateClient();

        boot.Should().Throw<InvalidOperationException>()
            .WithMessage("*DevIdentity*Development*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("ChatDemo")]
    public void DevIdentity_InDevelopmentOrTheChatDemoProfile_Starts(string environment)
    {
        // Control: the same configuration boots in Development and in the shipped ChatDemo
        // profile, so the refusal above is the environment check, not some other startup fault.
        using var factory = new DevIdentityFactory(environment);

        using var client = factory.CreateClient();

        client.Should().NotBeNull();
    }

    private sealed class DevIdentityFactory : WebApplicationFactory<Program>
    {
        private readonly string _environment;
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"host-devid-{Guid.NewGuid():N}.db");

        public DevIdentityFactory(string environment) => _environment = environment;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
                cmd.ExecuteNonQuery();
            }

            builder.UseEnvironment(_environment);
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BifrostQL:Provider"] = "sqlite",
                    ["BifrostQL:DisableAuth"] = "true",
                    ["BifrostQL:Path"] = "/graphql",
                    ["BifrostQL:DevIdentity"] = "demo-user",
                    // The ChatDemo profile turns chat on, which needs a key at startup; never called.
                    ["BifrostQL:Chat:ApiKey"] = "unused-in-this-test",
                    ["ConnectionStrings:bifrost"] = $"Data Source={_dbPath}",
                }));

            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}

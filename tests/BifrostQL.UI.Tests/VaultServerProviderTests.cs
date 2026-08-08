using BifrostQL.UI.Vault;
using FluentAssertions;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Unit tests for VaultServerProvider.BuildConnectionString — the provider-specific
/// connection-string assembly that feeds the actual DB connect. A bug here surfaces
/// as a connection failure, so each provider variant is pinned. Also covers the
/// SslMode → SqlClient Encrypt mapping and vault loading via an override path.
/// </summary>
public sealed class VaultServerProviderTests
{
    private static VaultServer Server(
        string provider, string host, int port,
        string? database = null, string? username = null, string? password = null,
        string? sslMode = null, bool trustServerCertificate = false)
        => new("s", provider, host, port, database, username, password, sslMode, null, [],
            trustServerCertificate);

    [Fact]
    public void SqlServer_WithCredentials_BuildsUserPasswordConnection()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlserver", "db", 1433, "appdb", "sa", "pw"));

        cs.Should().Contain("Server=db");
        cs.Should().NotContain("db,1433"); // default port omitted
        cs.Should().Contain("Database=appdb");
        cs.Should().Contain("User Id=sa");
        cs.Should().Contain("Password=pw");
        cs.Should().NotContain("Integrated Security");
    }

    /// <summary>
    /// Certificate validation is on unless the vault entry opts out. Emitting
    /// TrustServerCertificate=True unconditionally meant every vault-backed SQL Server
    /// connection negotiated TLS and then accepted ANY certificate, so anyone on the
    /// path could present their own and read the credentials plus all query traffic.
    /// </summary>
    [Fact]
    public void SqlServer_ByDefault_DoesNotTrustAnyCertificate()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlserver", "db", 1433, "appdb", "sa", "pw"));

        cs.Should().Contain("Encrypt=Mandatory");
        cs.Should().NotContain("TrustServerCertificate");
    }

    /// <summary>
    /// Encrypt=Strict exists precisely to validate the server certificate; SqlClient
    /// treats trusting it as a contradiction. The opt-in must not weaken a strict entry.
    /// </summary>
    /// <summary>
    /// The escape hatch for an internal/self-signed certificate the operator already
    /// trusts. It must be reachable, and it must take effect only when asked for.
    /// </summary>
    [Fact]
    public void SqlServer_OptedIn_TrustsCertificate()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlserver", "db", 1433, "appdb", "sa", "pw", trustServerCertificate: true));

        cs.Should().Contain("Encrypt=Mandatory");
        cs.Should().Contain("TrustServerCertificate=True");
    }

    [Fact]
    public void SqlServer_StrictEncrypt_NeverTrustsCertificate()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlserver", "db", 1433, sslMode: "strict", trustServerCertificate: true));

        cs.Should().Contain("Encrypt=Strict");
        cs.Should().NotContain("TrustServerCertificate");
    }

    /// <summary>
    /// With TLS explicitly off there is no certificate to trust, so the flag is noise —
    /// and leaving it in the string implies a trust posture the entry never chose.
    /// </summary>
    [Fact]
    public void SqlServer_EncryptDisabled_OmitsTrustFlag()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlserver", "db", 1433, sslMode: "disable", trustServerCertificate: true));

        cs.Should().Contain("Encrypt=False");
        cs.Should().NotContain("TrustServerCertificate");
    }

    [Fact]
    public void SqlServer_NoUsername_UsesIntegratedSecurity()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlserver", "db", 1433));

        cs.Should().Contain("Integrated Security=True");
        cs.Should().NotContain("User Id=");
    }

    [Fact]
    public void SqlServer_NonDefaultPort_EmbedsCommaPort()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlserver", "db", 14330));

        cs.Should().Contain("Server=db,14330");
    }

    [Theory]
    [InlineData(null, "Encrypt=Mandatory")]
    [InlineData("false", "Encrypt=False")]
    [InlineData("disable", "Encrypt=False")]
    [InlineData("off", "Encrypt=False")]
    [InlineData("none", "Encrypt=False")]
    // Opportunistic modes must ATTEMPT encryption (Encrypt=Mandatory), not silently
    // disable TLS — the user asked for encryption-if-available, and the frontend
    // treats these as ssl=true. Certificate validation stays on either way.
    [InlineData("optional", "Encrypt=Mandatory")]
    [InlineData("prefer", "Encrypt=Mandatory")]
    [InlineData("require", "Encrypt=Mandatory")]
    [InlineData("strict", "Encrypt=Strict")]
    public void SqlServer_MapsSslModeToEncrypt(string? sslMode, string expected)
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlserver", "db", 1433, sslMode: sslMode));

        cs.Should().Contain(expected);
    }

    [Fact]
    public void Postgres_BuildsFullConnection()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("postgres", "pg", 5432, "appdb", "u", "p"));

        cs.Should().Contain("Host=pg");
        cs.Should().Contain("Port=5432");
        cs.Should().Contain("Database=appdb");
        cs.Should().Contain("Username=u");
        cs.Should().Contain("Password=p");
        cs.Should().Contain("SSL Mode=Prefer"); // default when SslMode null
    }

    [Fact]
    public void Postgres_HonoursExplicitSslMode()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("postgres", "pg", 5432, sslMode: "Require"));

        cs.Should().Contain("SSL Mode=Require");
    }

    [Fact]
    public void MySql_UsesUidPwdAndDefaultSsl()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("mysql", "my", 3306, "appdb", "u", "p"));

        cs.Should().Contain("Server=my");
        cs.Should().Contain("Port=3306");
        cs.Should().Contain("Uid=u");
        cs.Should().Contain("Pwd=p");
        cs.Should().Contain("SslMode=Preferred");
    }

    [Fact]
    public void Sqlite_UsesHostAsDataSource()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("sqlite", "/data/app.db", 0));

        cs.Should().Be("Data Source=/data/app.db");
    }

    [Fact]
    public void UnknownProvider_Throws()
    {
        var act = () => VaultServerProvider.BuildConnectionString(
            Server("oracle", "h", 1521));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("postgres", "Password")]
    [InlineData("mysql", "Pwd")]
    [InlineData("sqlserver", "Password")]
    public void PasswordWithSpecialChars_RoundTripsAndDoesNotInjectKeywords(string provider, string pwdKey)
    {
        // A password containing ';' and '=' must not corrupt the connection string
        // or inject spurious keywords. Validate against the canonical ADO.NET parser.
        const string nastyPwd = "p;Encrypt=False;Database=evil";
        var cs = VaultServerProvider.BuildConnectionString(
            Server(provider, "h", provider == "mysql" ? 3306 : 5432, "realdb", "u", nastyPwd));

        var parsed = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = cs };

        parsed[pwdKey].Should().Be(nastyPwd, "the full password must survive as a single value");
        parsed["Database"].Should().Be("realdb", "the injected Database keyword must not override the real one");
    }

    [Fact]
    public void HostWithSpecialChars_RoundTripsViaParser()
    {
        var cs = VaultServerProvider.BuildConnectionString(
            Server("postgres", "weird;host", 5432, "d"));

        var parsed = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = cs };
        parsed["Host"].Should().Be("weird;host");
    }

    [Fact]
    public async Task LoadServers_FromVaultOverride_ReturnsVaultEntriesTaggedVault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bifrost-vsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vaultPath = Path.Combine(dir, "vault.json.enc");
        try
        {
            var name = "vsp-" + Guid.NewGuid().ToString("N")[..8];
            await VaultStore.Save(new VaultData
            {
                Servers = { new VaultServer(name, "postgres", "h", 5432, "d", "u", "p", null, null, []) },
            }, vaultPath);

            var loaded = await VaultServerProvider.LoadServers(vaultPath);

            loaded.Should().ContainSingle(e => e.Server.Name == name && e.Source == "vault");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}

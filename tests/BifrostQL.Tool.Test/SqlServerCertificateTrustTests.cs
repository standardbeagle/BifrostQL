using BifrostQL.Tool.Commands;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Tool.Test;

/// <summary>
/// The `bifrost &lt;server&gt; &lt;database&gt;` shorthand assembled its connection string with an
/// unconditional TrustServerCertificate=True. The connection negotiated TLS and then accepted
/// whatever certificate it was handed — expired, self-signed, or an attacker's. Encryption
/// without authentication is not a secure channel: anyone on the path to the database presents
/// their own certificate and reads the credentials plus every query that follows. The user had
/// no way to turn it off.
///
/// These tests state the posture the builder should hold: validation on by default, the waiver
/// an explicit opt-in, on both the SQL-auth and integrated-auth shapes.
/// </summary>
public class SqlServerCertificateTrustTests
{
    [Fact]
    public void Build_SqlAuth_DoesNotTrustTheCertificateByDefault()
    {
        // Arrange / Act
        var cs = SqlServerCertificateTrust.Build("db.corp", "appdb", "sa", "pw", trustServerCertificate: false);

        // Assert
        cs.Should().NotContain("TrustServerCertificate");
        cs.Should().Contain("Server=db.corp").And.Contain("Database=appdb").And.Contain("User Id=sa");
    }

    [Fact]
    public void Build_IntegratedAuth_DoesNotTrustTheCertificateByDefault()
    {
        // Arrange / Act
        var cs = SqlServerCertificateTrust.Build("db.corp", "appdb", user: null, password: null, trustServerCertificate: false);

        // Assert
        cs.Should().NotContain("TrustServerCertificate");
        cs.Should().Contain("Trusted_Connection=True");
    }

    [Fact]
    public void Build_SqlAuth_TrustsTheCertificateOnlyWhenExplicitlyOptedIn()
    {
        // Arrange / Act
        var cs = SqlServerCertificateTrust.Build("db.corp", "appdb", "sa", "pw", trustServerCertificate: true);

        // Assert
        cs.Should().Contain("TrustServerCertificate=True");
    }

    [Fact]
    public void Build_IntegratedAuth_TrustsTheCertificateOnlyWhenExplicitlyOptedIn()
    {
        // Arrange / Act
        var cs = SqlServerCertificateTrust.Build("db.corp", "appdb", user: null, password: null, trustServerCertificate: true);

        // Assert
        cs.Should().Contain("TrustServerCertificate=True");
    }

    /// <summary>
    /// A connect that fails on certificate validation is the failure a previously-working
    /// invocation now hits. Without recognising it, the user meets a bare "SSL Provider,
    /// error: 0" and has nothing to act on.
    /// </summary>
    [Fact]
    public void IsCertificateValidationFailure_RecognisesANestedCertificateRejection()
    {
        // Arrange
        var ex = new InvalidOperationException(
            "A connection was successfully established ... ",
            new Exception("The certificate chain was issued by an authority that is not trusted."));

        // Act / Assert
        SqlServerCertificateTrust.IsCertificateValidationFailure(ex).Should().BeTrue();
    }

    [Fact]
    public void IsCertificateValidationFailure_IgnoresAnUnrelatedFailure()
    {
        // Arrange
        var ex = new InvalidOperationException("Login failed for user 'sa'.");

        // Act / Assert
        SqlServerCertificateTrust.IsCertificateValidationFailure(ex).Should().BeFalse();
    }

    /// <summary>
    /// The remedy has to name the exact way out, or enforcing validation just strands the
    /// operator with a genuine self-signed internal server.
    /// </summary>
    [Fact]
    public void CertificateRemedy_NamesTheFlagAndTheConnectionStringKeyword()
    {
        // Act / Assert
        SqlServerCertificateTrust.CertificateRemedy.Should().Contain("--trust-server-certificate");
        SqlServerCertificateTrust.CertificateRemedy.Should().Contain("TrustServerCertificate=True");
    }
}

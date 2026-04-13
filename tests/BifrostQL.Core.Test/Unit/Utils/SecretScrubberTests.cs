using BifrostQL.Core.Utils;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Utils
{
    /// <summary>
    /// Tests for <see cref="SecretScrubber"/>. The scrubber is applied to any string
    /// that may be returned to the browser or written to logs, so the test suite
    /// exercises every credential pattern produced by the vault-connect code path.
    /// </summary>
    public class SecretScrubberTests
    {
        // ---------- Password= / Pwd= ----------

        [Theory]
        [InlineData("Server=x;Password=abc123;Database=y", "Server=x;Password=****;Database=y")]
        [InlineData("Server=x;Password=abc123", "Server=x;Password=****")]
        [InlineData("Server=x;password=abc123;", "Server=x;password=****;")]
        [InlineData("Server=x;PASSWORD=abc123;", "Server=x;PASSWORD=****;")]
        [InlineData("Password=p@$$w0rd!;Other=1", "Password=****;Other=1")]
        [InlineData("Password=\"embedded;value\";x=1", "Password=****;x=1")]
        public void Scrub_PasswordKey_IsReplaced(string input, string expected)
        {
            SecretScrubber.Scrub(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("Server=x;Pwd=abc123;Database=y", "Server=x;Pwd=****;Database=y")]
        [InlineData("Server=x;pwd=xyz789;", "Server=x;pwd=****;")]
        [InlineData("Server=x;PWD=Secret!;", "Server=x;PWD=****;")]
        [InlineData("Pwd=end-of-string", "Pwd=****")]
        public void Scrub_PwdKey_IsReplaced(string input, string expected)
        {
            SecretScrubber.Scrub(input).Should().Be(expected);
        }

        // ---------- User Id= / Uid= (PII) ----------

        [Theory]
        [InlineData("Server=x;User Id=andy;Password=p", "Server=x;User Id=****;Password=****")]
        [InlineData("Server=x;user id=andy;", "Server=x;user id=****;")]
        [InlineData("Server=x;USER ID=andy;", "Server=x;USER ID=****;")]
        [InlineData("Server=x;Uid=admin;", "Server=x;Uid=****;")]
        [InlineData("Server=x;UID=admin", "Server=x;UID=****")]
        public void Scrub_UserIdKey_IsReplaced(string input, string expected)
        {
            SecretScrubber.Scrub(input).Should().Be(expected);
        }

        // ---------- URL-style credentials ----------

        [Theory]
        [InlineData(
            "postgresql://user:secret@host:5432/db",
            "postgresql://user:****@host:5432/db")]
        [InlineData(
            "mysql://root:hunter2@127.0.0.1:3306/app",
            "mysql://root:****@127.0.0.1:3306/app")]
        [InlineData(
            "mongodb+srv://me:p%40ss@cluster.example.com/mydb",
            "mongodb+srv://me:****@cluster.example.com/mydb")]
        [InlineData(
            "Connecting to postgres://u:p@h/d failed",
            "Connecting to postgres://u:****@h/d failed")]
        public void Scrub_UrlCreds_IsReplaced(string input, string expected)
        {
            SecretScrubber.Scrub(input).Should().Be(expected);
        }

        // ---------- BIFROST_SERVERS env-var dumps ----------

        [Theory]
        // Single-line input: the whole value past "=" is treated as sensitive,
        // including any trailing annotation on the same line. This is an
        // intentional defense-in-depth choice — anything downstream of the
        // env-var on that line is very likely part of the JSON dump.
        [InlineData(
            "BIFROST_SERVERS=[{\"Password\":\"p\"}] more text",
            "BIFROST_SERVERS=****")]
        [InlineData(
            "env: BIFROST_SERVERS=anything goes here",
            "env: BIFROST_SERVERS=****")]
        // Multi-line input: only the BIFROST_SERVERS line is scrubbed, the next
        // line is preserved verbatim so that stack traces remain readable.
        [InlineData(
            "BIFROST_SERVERS=[{\"Password\":\"p\"}]\nnext line kept",
            "BIFROST_SERVERS=****\nnext line kept")]
        public void Scrub_BifrostServersEnv_IsReplaced(string input, string expected)
        {
            SecretScrubber.Scrub(input).Should().Be(expected);
        }

        // ---------- Passthrough / no credentials ----------

        [Theory]
        [InlineData("Server=localhost;Database=test")]
        [InlineData("A generic error message with no secrets")]
        [InlineData("Login failed for user on 'localhost'.")]
        [InlineData("")]
        public void Scrub_NoCredentials_Passthrough(string input)
        {
            SecretScrubber.Scrub(input).Should().Be(input);
        }

        // ---------- Null handling (documented: null in → null out) ----------

        [Fact]
        public void Scrub_Null_ReturnsNull()
        {
            SecretScrubber.Scrub(null).Should().BeNull();
        }

        // ---------- Multiple creds in one string ----------

        [Fact]
        public void Scrub_MultipleCreds_AllReplaced()
        {
            const string input =
                "Failed on Server=db1;User Id=u1;Password=p1;Database=d1 " +
                "and postgresql://u2:p2@db2/d2";
            var result = SecretScrubber.Scrub(input);

            result.Should().NotContain("p1");
            result.Should().NotContain("p2");
            result.Should().NotContain("u1");
            // URL user component is intentionally preserved (only the password is scrubbed).
            result.Should().Contain("postgresql://u2:****@db2/d2");
            result.Should().Contain("Password=****");
            result.Should().Contain("User Id=****");
        }

        // ---------- Case-insensitive sanity check (single call, mixed case) ----------

        [Fact]
        public void Scrub_MixedCase_AllVariantsReplaced()
        {
            const string input = "PaSsWoRd=s1;pWd=s2;uSeR Id=bob;UiD=alice";
            var result = SecretScrubber.Scrub(input);

            result.Should().NotContain("s1");
            result.Should().NotContain("s2");
            result.Should().NotContain("bob");
            result.Should().NotContain("alice");
            // Keys remain in their original casing; only values are masked.
            result.Should().Contain("PaSsWoRd=****");
            result.Should().Contain("pWd=****");
            result.Should().Contain("uSeR Id=****");
            result.Should().Contain("UiD=****");
        }

        // ---------- Adversarial: URL without credentials must pass through ----------

        [Theory]
        [InlineData("Connecting to http://example.com:8080/api/path")]
        [InlineData("See https://docs.example.com/guide for details")]
        [InlineData("postgres://localhost:5432/mydb")] // no user:pass segment
        public void Scrub_UrlWithoutCredentials_Passthrough(string input)
        {
            SecretScrubber.Scrub(input).Should().Be(input);
        }

        // ---------- Adversarial: empty password value still gets masked ----------

        [Fact]
        public void Scrub_EmptyPasswordValue_StillMasked()
        {
            SecretScrubber.Scrub("Server=x;Password=;Other=1")
                .Should().Be("Server=x;Password=****;Other=1");
        }

        // ---------- Acceptance criterion: full vault-connect style payload ----------

        [Fact]
        public void Scrub_FullErrorPayload_NoPasswordLiteralRemains()
        {
            const string input =
                "System.Exception: Login failed. " +
                "Connection: Server=tcp:db.example.com,1433;User Id=svc;Password=hunter2;Database=app\n" +
                "   at Microsoft.Data.SqlClient.SqlConnection.Open()\n" +
                "Inner: postgres://svc:hunter2@10.0.0.1:5432/app";

            var result = SecretScrubber.Scrub(input)!;

            result.Should().NotContain("hunter2");
            result.Should().NotContain("Password=hunter2");
            result.Should().NotContain("User Id=svc");
            result.Should().Contain("Password=****");
            result.Should().Contain("User Id=****");
            result.Should().Contain("postgres://svc:****@10.0.0.1:5432/app");
        }
    }
}

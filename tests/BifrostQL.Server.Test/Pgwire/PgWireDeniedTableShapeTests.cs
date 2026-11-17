using System.Security.Claims;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Invariant 9: a pre-pipeline shape check on a table the caller may not read answers exactly
    /// like the pipeline's denial. Column names resolve against the raw model, so before the fix
    /// <c>SELECT nosuch FROM secrets</c> answered <c>column "nosuch" does not exist</c> while
    /// <c>SELECT v FROM secrets</c> answered <c>permission denied.</c>: a caller with no read
    /// access could probe a denied table's column names one guess at a time. Both now answer
    /// the "relation does not exist" a missing table gets.
    /// </summary>
    public sealed class PgWireDeniedTableShapeTests : IAsyncLifetime
    {
        // secrets: a policy rule with no read action denies the whole table. notes: readable.
        private static readonly string[] MetadataRules =
        {
            "*.secrets { policy-read-deny: v }",
        };

        private static readonly string[] SeedSql =
        {
            "CREATE TABLE secrets (id INTEGER PRIMARY KEY, v TEXT NOT NULL)",
            "INSERT INTO secrets (id, v) VALUES (1, 'hidden')",
            "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)",
            "INSERT INTO notes (id, body) VALUES (1, 'visible')",
        };

        private PgWireRealDbHarness _harness = null!;

        public async Task InitializeAsync()
            => _harness = await PgWireRealDbHarness.StartAsync(nameof(PgWireDeniedTableShapeTests), MetadataRules, SeedSql);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static ClaimsPrincipal Caller() =>
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "u1") }, authenticationType: "pgwire"));

        [Theory]
        [InlineData("SELECT nosuch FROM secrets")]
        [InlineData("SELECT id FROM secrets WHERE nosuch = 1")]
        [InlineData("SELECT id FROM secrets ORDER BY nosuch")]
        public async Task Unknown_column_on_a_denied_table_answers_like_the_denial(string probe)
        {
            var denial = await _harness.QueryAsync(Caller(), "SELECT v FROM secrets");
            var missing = await _harness.QueryAsync(Caller(), "SELECT v FROM missing");
            denial.ErrorSqlState.Should().Be(missing.ErrorSqlState, "a denied table answers like a missing one");
            denial.ErrorMessage.Should().Be(missing.ErrorMessage!.Replace("missing", "secrets"));

            var shape = await _harness.QueryAsync(Caller(), probe);

            shape.ErrorSqlState.Should().Be(denial.ErrorSqlState);
            shape.ErrorMessage.Should().Be(denial.ErrorMessage);
        }

        [Fact]
        public async Task Unknown_column_on_a_readable_table_still_names_the_column()
        {
            // Control: the denial check must not swallow ordinary errors on tables the caller reads.
            var result = await _harness.QueryAsync(Caller(), "SELECT nosuch FROM notes");

            result.HasError.Should().BeTrue();
            result.ErrorSqlState.Should().NotBe("42501");
            result.ErrorMessage.Should().Contain("nosuch");
        }
    }
}

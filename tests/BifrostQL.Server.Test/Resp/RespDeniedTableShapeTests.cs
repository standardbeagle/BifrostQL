using System.Security.Claims;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// Invariant 9: a pre-pipeline key-shape check on a table the caller may not read answers
    /// exactly like the pipeline's denial. Keys parse against the raw model, so before the fix a
    /// wrong-arity GET on a denied table answered "supplies 2 value segment(s) but 1 are expected"
    /// — its primary-key arity — while a well-formed GET answered NOPERM.
    /// </summary>
    public sealed class RespDeniedTableShapeTests : IAsyncLifetime
    {
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

        private RespRealDbHarness _harness = null!;

        public async Task InitializeAsync()
            => _harness = await RespRealDbHarness.StartAsync(nameof(RespDeniedTableShapeTests), MetadataRules, SeedSql);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static ClaimsPrincipal Caller() =>
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "u1") }, authenticationType: "resp"));

        private async Task<string> GetErrorAsync(string key)
        {
            var act = () => _harness.GetAsync(Caller(), key);
            var thrown = await act.Should().ThrowAsync<RespWireException>();
            return thrown.Which.Message;
        }

        [Theory]
        [InlineData("secrets:1:2")]   // wrong arity
        [InlineData("secrets")]       // no key segments
        [InlineData("secrets:abc")]   // segment not coercible to the INTEGER key
        public async Task Malformed_key_on_a_denied_table_answers_like_the_denial(string probe)
        {
            var denial = await GetErrorAsync("secrets:1");
            denial.Should().Contain("NOPERM","the fixture must deny the table through the pipeline");

            (await GetErrorAsync(probe)).Should().Be(denial);
        }

        [Fact]
        public async Task Malformed_key_on_a_readable_table_still_explains_the_shape()
        {
            // Control: the denial check must not swallow shape errors on tables the caller reads.
            (await GetErrorAsync("notes:1:2")).Should().Contain("segment");
        }
    }
}

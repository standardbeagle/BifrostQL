using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// A simple query used to be parsed twice (catalog responder, then translator) and a prepared
    /// statement once per Describe/Bind/Execute. <see cref="PgSql"/> carries the first parse to
    /// every later consumer.
    /// </summary>
    public sealed class PgSqlTests
    {
        [Fact]
        public void Statement_is_parsed_once_and_shared()
        {
            var sql = new PgSql("SELECT id FROM users WHERE id = 1");

            var first = sql.Statement;

            sql.Statement.Should().BeSameAs(first);
            first.From.Name.Should().Be("users");
        }

        [Fact]
        public void A_text_that_does_not_parse_throws_the_same_error_every_time()
        {
            var sql = new PgSql("DELETE FROM users");

            var first = Record.Exception(() => sql.Statement);
            var second = Record.Exception(() => sql.Statement);

            first.Should().BeOfType<PgQueryTranslationException>();
            second.Should().BeSameAs(first);
        }
    }
}

using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// The RESP data commands (read, scan, write) each catch their own failures, so the
    /// condition -> wire-error mapping has to live in one place or the three drift apart.
    /// These pin the funnel's two outcomes.
    /// </summary>
    public class RespCommandErrorTests
    {
        [Fact]
        public void AuthorizationDenial_MapsToNoPerm_NotInternalError()
        {
            // An access denial reported as "internal error" tells the client the server
            // faulted and the command is worth retrying. It can never succeed on retry.
            var denied = new BifrostExecutionError("Access denied on table main.products column price")
                { ErrorCode = BifrostExecutionError.AccessDeniedCode };

            var mapped = RespCommandError.Map(denied);

            mapped.Should().StartWith("NOPERM");
            mapped.Should().Be(RespProtocol.AccessDeniedError);
        }

        [Fact]
        public void AuthorizationDenial_WireErrorCarriesNoIdentifier()
        {
            // The category crosses the wire; the denied table/column must not, or the
            // error becomes a way to enumerate a schema the caller cannot read.
            var denied = new BifrostExecutionError("Access denied on table main.products column price")
                { ErrorCode = BifrostExecutionError.AccessDeniedCode };

            var mapped = RespCommandError.Map(denied);

            mapped.Should().NotContain("products");
            mapped.Should().NotContain("price");
            mapped.Should().NotContain("main.");
        }

        [Fact]
        public void UntaggedExecutionError_StaysGenericInternalError()
        {
            // Anything not positively identified as a denial keeps the sanitized generic
            // string — fail closed, and never forward Bifrost-internal text.
            const string sensitive = "column secret_x does not exist in table users";

            var mapped = RespCommandError.Map(new BifrostExecutionError($"Database error: {sensitive}"));

            mapped.Should().Be(RespProtocol.InternalError);
            mapped.Should().NotContain("secret_x");
        }

        [Fact]
        public void UnexpectedException_StaysGenericInternalError()
        {
            var mapped = RespCommandError.Map(new InvalidOperationException("boom in the driver"));

            mapped.Should().Be(RespProtocol.InternalError);
            mapped.Should().NotContain("boom");
        }
    }
}

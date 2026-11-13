using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// The RESP funnel names every operation-id refusal by its code as the Redis error prefix (the
    /// first word of the reply, which clients parse as the error kind), never <c>ERR internal error</c>.
    /// Only <c>OPERATION_ID_REQUIRED</c> is reachable end to end (the conformance kit covers it). The
    /// thrown message names a table to prove the wire text is the per-code constant (invariant 3).
    /// </summary>
    public sealed class RespCommandErrorOperationIdTests
    {
        [Theory]
        [InlineData("OPERATION_ID_REQUIRED")]
        [InlineData("OPERATION_ID_UNSUPPORTED")]
        [InlineData("OPERATION_ID_REUSED")]
        [InlineData("OPERATION_ID_ANONYMOUS")]
        public void Operation_id_refusal_is_named_by_its_code(string code)
        {
            var refusal = new BifrostExecutionError("refused on secret_table.secret_column") { ErrorCode = code };

            var reply = RespCommandError.Map(refusal);

            reply.Should().StartWith(code + " ");
            reply.Should().NotContain("secret_table").And.NotContain("secret_column");
        }

        [Fact]
        public void Another_coded_error_is_still_an_internal_error()
        {
            var other = new BifrostExecutionError("boom on secret_table") { ErrorCode = "SOME_OTHER_CODE" };

            RespCommandError.Map(other).Should().Be(RespProtocol.InternalError);
        }
    }
}

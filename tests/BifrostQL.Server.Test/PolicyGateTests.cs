using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test;

public sealed class PolicyGateTests
{
    [Fact]
    public void UnknownTableIsDeniedAndRequireUsesGenericAccessError()
    {
        var model = new DbModel
        {
            Tables = Array.Empty<IDbTable>(),
            StoredProcedures = Array.Empty<DbStoredProcedure>(),
            Metadata = new Dictionary<string, object?>()
        };
        var gate = new PolicyGate(model, new AppIdentity("user", "test"));

        gate.CanAct("public.payments", PolicyAction.Create).Should().Be(PolicyDecision.Deny);
        var error = Assert.Throws<BifrostExecutionError>(() => gate.Require("public.payments", PolicyAction.Create));
        error.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        error.Message.Should().Be(PolicyDecision.Deny.Reason);
    }
}

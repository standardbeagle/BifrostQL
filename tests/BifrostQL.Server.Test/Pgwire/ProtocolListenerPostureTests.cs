using System.Net;
using BifrostQL.Server.Grpc;
using BifrostQL.Server.Pgwire;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// The declared exposure posture of every protocol-adapter listener, pinned as a test rather
    /// than left to a comment. Each of these listeners is a DATABASE front door; all three bound
    /// <c>ListenAnyIP</c> (0.0.0.0) with no way to narrow it, so merely registering an adapter
    /// published it to every network the host sits on — a posture decision nobody made.
    ///
    /// <para>The project rule is that an undeclared posture IS loopback, and that widening it
    /// (loopback -> LAN -> public) is an operator decision. These assertions exist so a future
    /// change back to an ambient wildcard bind fails a test instead of silently shipping. See
    /// AGENTS.md "Listener exposure posture" for the recorded posture and caps.</para>
    /// </summary>
    public sealed class ProtocolListenerPostureTests
    {
        [Fact]
        public void EveryProtocolListener_DefaultsToLoopback()
        {
            new PgWireOptions().BindAddress.Should().Be(IPAddress.Loopback);
            new RespWireOptions().BindAddress.Should().Be(IPAddress.Loopback);
            new GrpcWireOptions().BindAddress.Should().Be(IPAddress.Loopback);
        }

        [Fact]
        public void EveryProtocolListener_DeclaresAConcreteConnectionCap()
        {
            new PgWireOptions().MaxConnections.Should().Be(100);
            new RespWireOptions().MaxConnections.Should().Be(100);
            new GrpcWireOptions().MaxConcurrentConnections.Should().Be(100);
        }

        [Fact]
        public void EveryProtocolListener_DeclaresAConcretePreAuthDeadline()
        {
            // An unauthenticated peer holds an admission slot reserved at accept, so every front
            // door must be able to take it back without operator action.
            new PgWireOptions().HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(30));
            new RespWireOptions().AuthenticationTimeout.Should().Be(TimeSpan.FromSeconds(30));
        }

        [Fact]
        public void WideningTheBind_IsPossible_ButOnlyExplicitly()
        {
            // Exposure to the LAN remains available — it just has to be written down by the host.
            var options = new RespWireOptions { BindAddress = IPAddress.Any };
            options.BindAddress.Should().Be(IPAddress.Any);
        }
    }
}

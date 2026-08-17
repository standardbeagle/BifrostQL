using System.Net;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// The LDAP front door's declared exposure posture, pinned the same way every other protocol
    /// listener's is (see <c>ProtocolListenerPostureTests</c> and AGENTS.md "Listener exposure
    /// posture"). LDAP is a DATABASE front door that now authenticates callers, so the two posture
    /// rules that apply to any adapter with an authenticated state apply here: the bind defaults to
    /// loopback (widening is an operator decision, written down), and an admission slot taken at
    /// accept can be reclaimed from a peer that never authenticates.
    /// </summary>
    public sealed class LdapListenerPostureTests
    {
        [Fact]
        public void LdapListener_DefaultsToLoopback()
        {
            new LdapWireOptions().BindAddress.Should().Be(IPAddress.Loopback);
        }

        [Fact]
        public void WideningTheBind_IsPossible_ButOnlyExplicitly()
        {
            new LdapWireOptions { BindAddress = IPAddress.Any }.BindAddress.Should().Be(IPAddress.Any);
        }

        [Fact]
        public void LdapListener_DeclaresAConcretePreAuthDeadline()
        {
            // 30 seconds, matching the pgwire handshake and RESP authentication deadlines.
            new LdapWireOptions().AuthenticationTimeout.Should().Be(TimeSpan.FromSeconds(30));
        }

        [Fact]
        public void LdapListener_DeclaresTheSameConnectionCap_AsEveryOtherFrontDoor()
        {
            // 100, as pgwire and RESP declare. The cap bounds what an unauthenticated peer can make
            // the host hold, so it is a front-door property rather than a per-protocol preference.
            new LdapWireOptions().MaxConnections.Should().Be(100);
        }

        [Fact]
        public void LdapListener_DeclaresAConcreteTlsHandshakeDeadline()
        {
            // The admission slot is held while the handshake runs, so it needs its own bound.
            new LdapWireOptions().TlsHandshakeTimeout.Should().Be(TimeSpan.FromSeconds(30));
        }
    }
}

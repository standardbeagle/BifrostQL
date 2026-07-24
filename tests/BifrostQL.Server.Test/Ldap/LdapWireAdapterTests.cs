using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Startup-guard tests for the LDAP adapter. A thrown <see cref="LdapWireAdapter.StartAsync"/>
    /// aborts host startup (the <see cref="IProtocolAdapter"/> contract), so a misconfigured limit
    /// must fail fast there rather than come up with a disabled DoS guard or an illegal port.
    /// </summary>
    public sealed class LdapWireAdapterTests
    {
        [Fact]
        public async Task StartAsync_WithValidOptions_Succeeds()
        {
            var adapter = new LdapWireAdapter(new LdapWireOptions { Port = 3899 });
            await adapter.StartAsync(default); // must not throw
        }

        [Theory]
        [InlineData(0)]
        [InlineData(70000)]
        public async Task StartAsync_WithIllegalPort_AbortsStartup(int port)
        {
            var adapter = new LdapWireAdapter(new LdapWireOptions { Port = port });
            var act = async () => await adapter.StartAsync(default);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task StartAsync_WithZeroNestingDepth_AbortsStartup()
        {
            var adapter = new LdapWireAdapter(new LdapWireOptions { MaxNestingDepth = 0 });
            var act = async () => await adapter.StartAsync(default);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task StartAsync_WithNonPositiveIdleTimeout_AbortsStartup()
        {
            var adapter = new LdapWireAdapter(new LdapWireOptions { IdleTimeout = TimeSpan.Zero });
            var act = async () => await adapter.StartAsync(default);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Validate_RejectsAMessageCapBelowTheFloor()
        {
            var act = () => LdapWireAdapter.Validate(new LdapWireOptions { MaxMessageLength = 4 });
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}

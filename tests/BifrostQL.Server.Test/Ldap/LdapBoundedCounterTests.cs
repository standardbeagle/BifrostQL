using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Unit tests for the lock-free admission counter that backs both the connection cap and the
    /// per-connection outstanding-operation cap. A cap that reads as protection but never refuses is
    /// worse than none, so these pin that acquisition fails exactly at the ceiling and that a release
    /// re-opens a slot — the fail-closed guarantee the connection loop depends on.
    /// </summary>
    public sealed class LdapBoundedCounterTests
    {
        [Fact]
        public void Acquire_SucceedsUpToTheCap_ThenRefuses()
        {
            var counter = new LdapBoundedCounter(3, "MaxOutstandingOperations");

            counter.TryAcquire().Should().BeTrue();
            counter.TryAcquire().Should().BeTrue();
            counter.TryAcquire().Should().BeTrue();
            counter.Count.Should().Be(3);

            // The 4th acquisition is refused without mutating the counter.
            counter.TryAcquire().Should().BeFalse();
            counter.Count.Should().Be(3);
        }

        [Fact]
        public void Release_ReopensASlot()
        {
            var counter = new LdapBoundedCounter(1, "MaxConnections");
            counter.TryAcquire().Should().BeTrue();
            counter.TryAcquire().Should().BeFalse();

            counter.Release();

            counter.TryAcquire().Should().BeTrue("a released slot must be reusable");
        }

        [Fact]
        public void Constructor_RejectsANonPositiveCap()
        {
            var act = () => new LdapBoundedCounter(0, "MaxConnections");
            act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}

using System.Security.Claims;
using System.Text;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// An LDAP peer that sends requests and never reads the responses parks the server's flush on a
    /// full window. The loop's read budget cannot see that — it is not reading — and every response
    /// was written under the bare connection token, so the slot was held for as long as the peer
    /// liked: past AuthenticationTimeout before a bind, past IdleTimeout after one. Both facts run
    /// the production handler over the production Kestrel glue with a stalled peer, on an injected
    /// clock, and prove the connection ends and its admission slot is released.
    /// </summary>
    public sealed class LdapWriteDeadlineTests
    {
        private static readonly TimeSpan PreAuth = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task Unauthenticated_request_flood_into_a_zero_window_is_cut_at_the_pre_auth_deadline()
        {
            var clock = new FakeTimeProvider();
            var limiter = new LdapConnectionLimiter(1);
            var options = new LdapWireOptions
            {
                AuthenticationTimeout = PreAuth,
                // Both far beyond the total advance below, so only the pre-auth deadline can end the write.
                WriteTimeout = TimeSpan.FromDays(1),
                IdleTimeout = TimeSpan.FromDays(1),
            };
            var handler = new LdapConnectionHandler(options, limiter, timeProvider: clock);
            var transport = new StalledReaderTransport();

            await transport.SendAsync(Enumerable.Range(1, 200)
                .SelectMany(id => LdapWire.Message(id, LdapWire.ExtendedRequest("1.2.3.4"))).ToArray());

            var session = handler.HandleConnectionAsync(transport.ServerStream, CancellationToken.None);
            await transport.WaitUntilServerIsBlockedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            session.IsCompleted.Should().BeFalse("the session is parked on the full window before the clock moves");
            limiter.Count.Should().Be(1, "the parked session still holds its admission slot");

            await AdvanceUntilDoneAsync(clock, session, PreAuth + TimeSpan.FromSeconds(1));

            session.IsCompleted.Should().BeTrue(
                "an unauthenticated session's writes must end with its pre-auth budget, not wait on a peer that never reads");
            session.IsCompletedSuccessfully.Should().BeTrue("a cut write closes the connection cleanly, never faults the handler");
            limiter.Count.Should().Be(0, "the session's slot is released when the write is cut");
        }

        [Fact]
        public async Task Credentialed_session_writing_to_a_stalled_reader_is_cut_at_the_write_timeout()
        {
            var clock = new FakeTimeProvider();
            var limiter = new LdapConnectionLimiter(1);
            var writeTimeout = TimeSpan.FromMinutes(1);
            var options = new LdapWireOptions
            {
                AuthenticationTimeout = PreAuth,
                WriteTimeout = writeTimeout,
                // Far beyond the total advance below: only the write timeout may end this session.
                IdleTimeout = TimeSpan.FromDays(1),
                AllowInsecureSimpleBind = true, // the in-memory test transport is not TLS
            };
            var authenticator = new LdapBindAuthenticator(new Store(), new Hasher(), new Factory(), options);
            var handler = new LdapConnectionHandler(options, limiter, authenticator, timeProvider: clock);
            var transport = new StalledReaderTransport();

            var session = handler.HandleConnectionAsync(transport.ServerStream, CancellationToken.None);
            await transport.SendAsync(LdapWire.Message(1, LdapWire.BindRequest(name: "uid=alice", password: "s3cret")));
            LdapWire.ParseResponse(await transport.ReadAvailableAsync().WaitAsync(TimeSpan.FromSeconds(10)))
                .ResultCode.Should().Be(LdapResultCode.Success, "the deadline is retired by a credentialed bind");

            await transport.SendAsync(Enumerable.Range(2, 500)
                .SelectMany(id => LdapWire.Message(id, LdapWire.ExtendedRequest("1.2.3.4"))).ToArray());
            await transport.WaitUntilServerIsBlockedAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await AdvanceUntilDoneAsync(clock, session, writeTimeout + TimeSpan.FromSeconds(1));

            session.IsCompleted.Should().BeTrue(
                "a credentialed session's response that stalls on a peer that never reads must end at WriteTimeout");
            session.IsCompletedSuccessfully.Should().BeTrue("a cut write closes the connection cleanly, never faults the handler");
            limiter.Count.Should().Be(0);
        }

        // Advances the injected clock and gives the cancelled flush a moment to unwind, per poll:
        // the clock decides expiry; the real wait only lets continuations run.
        private static async Task AdvanceUntilDoneAsync(FakeTimeProvider clock, Task session, TimeSpan step)
        {
            for (var poll = 0; poll < 50 && !session.IsCompleted; poll++)
            {
                clock.Advance(step);
                await Task.WhenAny(session, Task.Delay(TimeSpan.FromMilliseconds(100)));
            }
        }

        private sealed class Hasher : ILdapPasswordHasher
        {
            public string DecoyHash => "hash:$decoy$";
            public bool Verify(ReadOnlySpan<byte> password, string passwordHash) =>
                passwordHash != DecoyHash && passwordHash == "hash:" + Encoding.UTF8.GetString(password);
        }

        private sealed class Store : ILdapCredentialStore
        {
            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct)
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, "alice") }, "ldap"));
                return Task.FromResult<LdapCredentialRecord?>(
                    string.Equals(bindDn, "uid=alice", StringComparison.OrdinalIgnoreCase)
                        ? new LdapCredentialRecord("hash:s3cret", principal, Enabled: true)
                        : null);
            }
        }

        private sealed class Factory : IBifrostAuthContextFactory
        {
            public IDictionary<string, object?> CreateUserContext(HttpContext context)
            {
                var sub = context.User.FindFirst(ClaimTypes.Name)?.Value;
                return string.IsNullOrEmpty(sub)
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { ["sub"] = sub };
            }

            public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
                => CreateUserContext(context);
        }
    }
}

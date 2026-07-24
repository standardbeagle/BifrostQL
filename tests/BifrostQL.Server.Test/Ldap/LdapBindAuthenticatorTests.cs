using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using BifrostQL.Server;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Unit tests for <see cref="LdapBindAuthenticator"/> — THE auth surface of the LDAP epic. These
    /// pin the security invariants directly against the verifier rather than only through the wire:
    /// the constant-time anti-enumeration path (invariant 2), the uniform invalidCredentials result
    /// for every failure class (criterion 2), fail-closed identity projection, the hardening controls
    /// (rate limit, bounded password, secret zeroing, structured audit — criterion 3), and the
    /// default-off anonymous-bind gate (criterion 4).
    /// </summary>
    public sealed class LdapBindAuthenticatorTests
    {
        // ---- test doubles ----

        /// <summary>
        /// A hash verifier with a recorded invocation log. Verify(pw, hash) is true iff the hash is the
        /// canonical hash of the password AND is not the decoy — so it models a real hash verify
        /// (never a plaintext compare) and lets a test assert WHETHER the verify ran (the crux of the
        /// anti-enumeration revert-proof).
        /// </summary>
        private sealed class RecordingHasher : ILdapPasswordHasher
        {
            public readonly List<string> VerifiedHashes = new();
            public string DecoyHash => "hash:$decoy$";
            public static string HashOf(string password) => "hash:" + password;

            public bool Verify(ReadOnlySpan<byte> password, string passwordHash)
            {
                VerifiedHashes.Add(passwordHash);
                if (passwordHash == DecoyHash) return false;
                return passwordHash == HashOf(Encoding.UTF8.GetString(password));
            }
        }

        private sealed class FakeStore : ILdapCredentialStore
        {
            private readonly Dictionary<string, LdapCredentialRecord> _byDn = new(StringComparer.OrdinalIgnoreCase);
            public bool Throw { get; set; }

            public FakeStore Add(string dn, string password, ClaimsPrincipal principal, bool enabled = true)
            {
                _byDn[dn] = new LdapCredentialRecord(RecordingHasher.HashOf(password), principal, enabled);
                return this;
            }

            public Task<LdapCredentialRecord?> FindAsync(string bindDn, CancellationToken ct)
            {
                if (Throw) throw new InvalidOperationException("store is down");
                return Task.FromResult(_byDn.TryGetValue(bindDn, out var r) ? r : null);
            }
        }

        /// <summary>
        /// Projects a candidate principal the way the real factory's fail-closed contract requires:
        /// a principal marked "unmapped" throws (unmapped issuer), a subject-less principal projects to
        /// an empty context, and a normal principal projects to a one-key context.
        /// </summary>
        private sealed class FakeAuthFactory : IBifrostAuthContextFactory
        {
            public IDictionary<string, object?> CreateUserContext(HttpContext context)
            {
                var user = context.User;
                if (user.HasClaim("unmapped", "true"))
                    throw new InvalidOperationException("unmapped OIDC issuer");
                var sub = user.FindFirst(ClaimTypes.Name)?.Value ?? user.FindFirst("sub")?.Value;
                if (string.IsNullOrEmpty(sub))
                    return new Dictionary<string, object?>(); // subject-less → empty
                return new Dictionary<string, object?> { ["sub"] = sub };
            }

            public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
                => CreateUserContext(context);
        }

        private sealed class RecordingObserver : ILdapBindObserver
        {
            public readonly ConcurrentQueue<LdapBindAuditRecord> Records = new();
            public void OnBind(LdapBindAuditRecord record) => Records.Enqueue(record);
        }

        private static ClaimsPrincipal Principal(string? subject, bool unmapped = false)
        {
            var claims = new List<Claim>();
            if (subject is not null) claims.Add(new Claim(ClaimTypes.Name, subject));
            if (unmapped) claims.Add(new Claim("unmapped", "true"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "ldap"));
        }

        private static byte[] Pw(string s) => Encoding.UTF8.GetBytes(s);

        private static LdapBindRequest SimpleBind(string name, byte[]? password) =>
            new(Version: 3, Name: name, AuthKind: LdapBindAuthKind.Simple, SimplePassword: password);

        private static LdapBindAuthenticator Build(
            FakeStore store,
            out RecordingHasher hasher,
            out RecordingObserver observer,
            LdapWireOptions? options = null,
            Func<DateTimeOffset>? clock = null)
        {
            hasher = new RecordingHasher();
            observer = new RecordingObserver();
            return new LdapBindAuthenticator(
                store, hasher, new FakeAuthFactory(), options ?? new LdapWireOptions(),
                services: null, rateLimiter: null, observer: observer, clock: clock);
        }

        // ---- happy path ----

        [Fact]
        public async Task ValidCredential_Succeeds_AndProjectsIdentity()
        {
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out _, out var observer);

            var result = await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("s3cret")), "10.0.0.1", default);

            result.Succeeded.Should().BeTrue();
            result.ResultCode.Should().Be(LdapResultCode.Success);
            result.UserContext.Should().ContainKey("sub").WhoseValue.Should().Be("alice");
            observer.Records.Should().ContainSingle().Which.Outcome.Should().Be(LdapBindOutcome.Success);
        }

        // ---- criterion 2: five failure classes, one uniform result ----

        [Fact]
        public async Task AllFiveFailureClasses_ReturnUniform_InvalidCredentials()
        {
            var store = new FakeStore()
                .Add("uid=alice", "s3cret", Principal("alice"))
                .Add("uid=disabled", "pw", Principal("disabled"), enabled: false)
                .Add("uid=nosub", "pw", Principal(subject: null))       // subject-less principal
                .Add("uid=badissuer", "pw", Principal("x", unmapped: true));

            var auth = Build(store, out _, out _);

            // 1 unknown DN, 2 wrong password, 3 disabled account, 4 subject-less, 5 unmapped issuer.
            var cases = new[]
            {
                SimpleBind("uid=ghost", Pw("whatever")),
                SimpleBind("uid=alice", Pw("wrong")),
                SimpleBind("uid=disabled", Pw("pw")),
                SimpleBind("uid=nosub", Pw("pw")),
                SimpleBind("uid=badissuer", Pw("pw")),
            };

            foreach (var bind in cases)
            {
                var result = await auth.AuthenticateAsync(bind, "10.0.0.1", default);
                result.Succeeded.Should().BeFalse();
                result.ResultCode.Should().Be(LdapResultCode.InvalidCredentials,
                    "every failure class must be byte-identical on the wire (anti-enumeration)");
                result.DiagnosticMessage.Should().BeEmpty("the diagnostic must leak no distinguishing detail");
            }
        }

        // ---- invariant 2: the constant-time compare runs UNCONDITIONALLY (revert-proof) ----

        [Fact]
        public async Task UnknownDn_And_WrongPassword_BothRunTheHashVerify()
        {
            // Anti-enumeration revert-proof. The hash verify must run for BOTH an unknown DN (against a
            // decoy hash) and a known DN with a wrong password. If the code were reordered to the
            // short-circuit anti-pattern `record is not null && Verify(...)`, the unknown-DN attempt
            // would SKIP Verify — and this assertion (Verify ran for the unknown DN) would go RED,
            // detecting exactly the timing/branch enumeration oracle invariant 2 forbids.
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out var hasher, out _);

            await auth.AuthenticateAsync(SimpleBind("uid=ghost", Pw("whatever")), "10.0.0.1", default);
            hasher.VerifiedHashes.Should().ContainSingle()
                .Which.Should().Be(hasher.DecoyHash, "an unknown DN must still run the decoy-hash verify");

            await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("wrong")), "10.0.0.1", default);
            hasher.VerifiedHashes.Should().HaveCount(2);
            hasher.VerifiedHashes[1].Should().Be(RecordingHasher.HashOf("s3cret"),
                "a wrong password must run the verify against the real stored hash");
        }

        [Fact]
        public async Task DisabledAccount_StillRunsTheVerify_ThenFailsOnEnabledCheck()
        {
            // The enabled check is AND-ed AFTER the compare, never short-circuited before it: a disabled
            // account must cost the same hash work as an enabled one.
            var store = new FakeStore().Add("uid=bob", "pw", Principal("bob"), enabled: false);
            var auth = Build(store, out var hasher, out _);

            var result = await auth.AuthenticateAsync(SimpleBind("uid=bob", Pw("pw")), "10.0.0.1", default);

            result.Succeeded.Should().BeFalse();
            hasher.VerifiedHashes.Should().ContainSingle().Which.Should().Be(RecordingHasher.HashOf("pw"));
        }

        // ---- invariant 3: store fault never reaches the wire ----

        [Fact]
        public async Task StoreFault_FailsClosed_AsInvalidCredentials_AndStillRunsDecoyVerify()
        {
            var store = new FakeStore { Throw = true };
            var auth = Build(store, out var hasher, out _);

            var result = await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("s3cret")), "10.0.0.1", default);

            result.Succeeded.Should().BeFalse();
            result.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
            hasher.VerifiedHashes.Should().ContainSingle().Which.Should().Be(hasher.DecoyHash,
                "a store fault must be indistinguishable from an unknown DN, decoy verify and all");
        }

        // ---- criterion 3: bounded password (hash-DoS guard) ----

        [Fact]
        public async Task OversizedPassword_IsRefusedBeforeHashing()
        {
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out var hasher, out var observer,
                new LdapWireOptions { MaxPasswordLength = 8 });

            var result = await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw(new string('x', 9))), "10.0.0.1", default);

            result.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
            hasher.VerifiedHashes.Should().BeEmpty("an oversized password must never reach the hasher (hash-DoS guard)");
            observer.Records.Should().ContainSingle().Which.Outcome.Should().Be(LdapBindOutcome.PasswordTooLong);
        }

        [Fact]
        public async Task EmptyPassword_UnauthenticatedBind_IsRefused()
        {
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out var hasher, out _);

            var result = await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("")), "10.0.0.1", default);

            result.Succeeded.Should().BeFalse();
            result.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
            hasher.VerifiedHashes.Should().BeEmpty();
        }

        // ---- criterion 3: secret zeroing ----

        [Fact]
        public async Task PresentedPassword_IsZeroed_AfterVerify()
        {
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out _, out _);
            var password = Pw("s3cret");

            await auth.AuthenticateAsync(SimpleBind("uid=alice", password), "10.0.0.1", default);

            password.Should().OnlyContain(b => b == 0, "the presented password buffer must be wiped after the verify");
        }

        [Fact]
        public async Task PresentedPassword_IsZeroed_EvenOnFailure()
        {
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out _, out _);
            var password = Pw("wrongpw");

            await auth.AuthenticateAsync(SimpleBind("uid=alice", password), "10.0.0.1", default);

            password.Should().OnlyContain(b => b == 0);
        }

        // ---- criterion 3: per-source and per-account rate limits ----

        [Fact]
        public async Task PerAccount_RateLimit_RefusesBeyondCap_BeforeHashing()
        {
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out var hasher, out var observer,
                new LdapWireOptions { MaxBindAttemptsPerAccount = 2, MaxBindAttemptsPerSource = 1000 });

            // Two wrong attempts are admitted (and hashed); the third against the same account is refused.
            await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("x")), "10.0.0.1", default);
            await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("y")), "10.0.0.2", default);
            var hashesBefore = hasher.VerifiedHashes.Count;

            var third = await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("z")), "10.0.0.3", default);

            third.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
            hasher.VerifiedHashes.Should().HaveCount(hashesBefore, "a rate-limited attempt must not reach the hasher");
            observer.Records.Should().Contain(r => r.Outcome == LdapBindOutcome.RateLimited);
        }

        [Fact]
        public async Task PerSource_RateLimit_RefusesBeyondCap()
        {
            var store = new FakeStore();
            var auth = Build(store, out _, out var observer,
                new LdapWireOptions { MaxBindAttemptsPerSource = 2, MaxBindAttemptsPerAccount = 1000 });

            // Same source, different accounts: the per-source cap trips regardless of the account.
            await auth.AuthenticateAsync(SimpleBind("uid=a", Pw("x")), "1.1.1.1", default);
            await auth.AuthenticateAsync(SimpleBind("uid=b", Pw("x")), "1.1.1.1", default);
            var third = await auth.AuthenticateAsync(SimpleBind("uid=c", Pw("x")), "1.1.1.1", default);

            third.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
            observer.Records.Should().Contain(r => r.Outcome == LdapBindOutcome.RateLimited);
        }

        [Fact]
        public async Task RateLimit_Window_Resets_AllowingAttemptsAgain()
        {
            var now = DateTimeOffset.UtcNow;
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out _, out _,
                new LdapWireOptions { MaxBindAttemptsPerAccount = 1, MaxBindAttemptsPerSource = 1000, BindRateLimitWindow = TimeSpan.FromMinutes(1) },
                clock: () => now);

            (await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("x")), "1.1.1.1", default)).Succeeded.Should().BeFalse();
            (await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("x")), "1.1.1.1", default))
                .ResultCode.Should().Be(LdapResultCode.InvalidCredentials); // second in-window: rate-limited

            now = now.AddMinutes(2); // window rolls over
            var afterWindow = await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("s3cret")), "1.1.1.1", default);
            afterWindow.Succeeded.Should().BeTrue("a fresh window admits attempts again");
        }

        // ---- criterion 3: structured audit carries no secret ----

        [Fact]
        public async Task Audit_Records_Outcome_Dn_And_Source_ButNoSecret()
        {
            var store = new FakeStore().Add("uid=alice", "s3cret", Principal("alice"));
            var auth = Build(store, out _, out var observer);

            await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("s3cret")), "203.0.113.7", default);

            var record = observer.Records.Should().ContainSingle().Subject;
            record.Outcome.Should().Be(LdapBindOutcome.Success);
            record.BindDn.Should().Be("uid=alice");
            record.Source.Should().Be("203.0.113.7");
            // The record type structurally has no password/hash field — a secret cannot be logged here.
            typeof(LdapBindAuditRecord).GetProperties().Select(p => p.Name)
                .Should().BeEquivalentTo(new[] { "Outcome", "BindDn", "Source", "TimestampUtc" });
        }

        // ---- criterion 4: anonymous bind default OFF ----

        [Fact]
        public async Task AnonymousBind_IsRefused_ByDefault()
        {
            var auth = Build(new FakeStore(), out var hasher, out var observer);

            var result = await auth.AuthenticateAsync(SimpleBind("", Pw("")), "1.1.1.1", default);

            result.Succeeded.Should().BeFalse();
            result.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
            hasher.VerifiedHashes.Should().BeEmpty("an anonymous bind never runs a credential verify");
            observer.Records.Should().ContainSingle().Which.Outcome.Should().Be(LdapBindOutcome.AnonymousDisabled);
        }

        [Fact]
        public async Task AnonymousBind_WhenEnabled_IsAdmitted_WithNoIdentity()
        {
            var auth = Build(new FakeStore(), out _, out var observer,
                new LdapWireOptions { AnonymousBindEnabled = true });

            var result = await auth.AuthenticateAsync(SimpleBind("", Pw("")), "1.1.1.1", default);

            result.Succeeded.Should().BeTrue();
            result.IsAnonymous.Should().BeTrue("an admitted anonymous bind reads only RootDSE/subschema");
            result.UserContext.Should().BeNull("an anonymous bind carries no projected identity");
            observer.Records.Should().ContainSingle().Which.Outcome.Should().Be(LdapBindOutcome.AnonymousSuccess);
        }

        [Fact]
        public async Task NonEmptyDn_WithEmptyPassword_IsNotAnonymous_AndIsRefused()
        {
            // An "unauthenticated bind" (a DN but no password) is NOT anonymous; it is refused as
            // invalidCredentials even when anonymous binds are enabled.
            var auth = Build(new FakeStore().Add("uid=alice", "s3cret", Principal("alice")), out _, out var observer,
                new LdapWireOptions { AnonymousBindEnabled = true });

            var result = await auth.AuthenticateAsync(SimpleBind("uid=alice", Pw("")), "1.1.1.1", default);

            result.Succeeded.Should().BeFalse();
            result.ResultCode.Should().Be(LdapResultCode.InvalidCredentials);
            observer.Records.Should().ContainSingle().Which.Outcome.Should().Be(LdapBindOutcome.InvalidCredentials);
        }
    }
}

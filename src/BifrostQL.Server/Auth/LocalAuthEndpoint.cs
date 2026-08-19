using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// Maps the local-user login and logout endpoints. The login endpoint accepts a
    /// JSON credential payload, verifies it server-side through <see cref="LocalUserStore"/>,
    /// and on success issues a cookie-backed <see cref="ClaimsPrincipal"/>. Database
    /// credentials never leave the server: only the resulting session cookie is returned
    /// to the client.
    /// </summary>
    public static class LocalAuthEndpoint
    {
        /// <summary>
        /// Process-wide login throttle. Keyed by client IP + login so neither a single IP
        /// spraying many accounts nor many IPs hammering one account is unbounded. In-memory
        /// and best-effort (a load-balanced deployment throttles per node); pair with an
        /// edge rate limiter for cross-node guarantees.
        /// </summary>
        private static readonly LoginThrottle Throttle = new();


        /// <summary>
        /// Registers the local auth login and logout endpoints on the application pipeline.
        /// Call after authentication middleware so the issued cookie is honored on
        /// subsequent requests.
        /// </summary>
        public static IApplicationBuilder UseBifrostLocalAuth(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<LocalAuthOptions>()
                ?? throw new InvalidOperationException(
                    "LocalAuthOptions not registered. Call AddBifrostLocalAuth() during service configuration.");

            app.Map(options.LoginPath, branch => branch.Run(HandleLoginAsync));
            app.Map(options.LogoutPath, branch => branch.Run(HandleLogoutAsync));
            app.Map(options.SessionPath, branch => branch.Run(HandleSessionAsync));
            return app;
        }

        /// <summary>
        /// Builds the cookie-backed <see cref="ClaimsPrincipal"/> for an authenticated
        /// <see cref="AppIdentity"/>. The claims carry exactly the data the
        /// <see cref="BifrostContext"/> needs to reconstruct the same identity contract:
        /// stable id, email, display name, provider, tenant, roles, and the household
        /// provider claim (when the login resolved one).
        /// </summary>
        public static ClaimsPrincipal BuildPrincipal(AppIdentity identity)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, identity.Id),
                new(LocalAuthClaims.Provider, identity.Provider),
            };

            if (!string.IsNullOrWhiteSpace(identity.Email))
                claims.Add(new Claim(ClaimTypes.Email, identity.Email));
            if (!string.IsNullOrWhiteSpace(identity.DisplayName))
                claims.Add(new Claim(ClaimTypes.Name, identity.DisplayName));
            if (!string.IsNullOrWhiteSpace(identity.TenantId))
                claims.Add(new Claim(LocalAuthClaims.Tenant, identity.TenantId));
            foreach (var orgId in identity.OrgIds)
                claims.Add(new Claim(LocalAuthClaims.Org, orgId));
            foreach (var role in identity.Roles)
                claims.Add(new Claim(ClaimTypes.Role, role));

            // The household provider claim resolved from the member row at login
            // is carried through the cookie so BifrostContext can re-surface it.
            if (identity.Claims.TryGetValue(MetadataKeys.Auth.HouseholdClaimKey, out var household)
                && household is not null
                && !string.IsNullOrWhiteSpace(household.ToString()))
                claims.Add(new Claim(LocalAuthClaims.Household, household.ToString()!));

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            return new ClaimsPrincipal(claimsIdentity);
        }

        private static async Task HandleLoginAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var store = context.RequestServices.GetService<LocalUserStore>();
            if (store == null)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            LocalLoginRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<LocalLoginRequest>(context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                request = null;
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrEmpty(request.Password))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var options = context.RequestServices.GetService<LocalAuthOptions>();
            var throttleKey = BuildThrottleKey(context, request.Login);

            // Fail closed against online guessing: once a caller (IP + login) exceeds the
            // configured failure budget, stop verifying and respond 429 until the window
            // elapses. Verification is never even attempted while locked out.
            if (options != null && Throttle.IsLockedOut(throttleKey, options))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            var result = await store
                .VerifyCredentialsAsync(request.Login, request.Password, context.RequestAborted)
                .ConfigureAwait(false);

            if (!result.Succeeded || result.Identity == null)
            {
                if (options != null)
                    Throttle.RecordFailure(throttleKey, options);

                // Same response for missing user and wrong password: do not leak which.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            Throttle.Clear(throttleKey);

            var principal = BuildPrincipal(result.Identity);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal)
                .ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        private static string BuildThrottleKey(HttpContext context, string login)
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return ip + "|" + login.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// In-memory sliding-window failure counter with lockout. Not a substitute for an
        /// edge rate limiter, but denies a naive online brute-force from a single node.
        /// </summary>
        internal sealed class LoginThrottle
        {
            private sealed class Entry
            {
                public int Failures;
                public DateTimeOffset WindowStart;
            }

            private readonly ConcurrentDictionary<string, Entry> _entries = new();

            // Hard cap on tracked keys. Entries are only removed on a SUCCESSFUL login for the
            // exact key, so a peer rotating login strings or source IPs would otherwise grow this
            // map without bound on the unauthenticated path. RecordFailure enforces the cap on
            // INSERT: an already-tracked key always updates in place (a victim already being
            // brute-forced is never evicted, so its lockout can never be bypassed), but a brand-new
            // key past the cap is refused after a throttled sweep of elapsed entries fails to free a
            // slot. Beyond the cap the in-process throttle degrades to best-effort — the edge rate
            // limiter is the real control (see the type summary) — but memory stays bounded and the
            // hot path stays O(1) amortized (the O(n) sweep runs at most once per lockout window).
            internal const int DefaultMaxTrackedKeys = 10_000;
            private readonly int _maxTrackedKeys;
            private readonly object _sweepGate = new();
            private DateTimeOffset _lastSweepUtc = DateTimeOffset.MinValue;

            public LoginThrottle() : this(DefaultMaxTrackedKeys) { }

            // Test seam: a lower cap makes the prune-when-over-cap behaviour exercisable
            // without materializing ten thousand entries.
            internal LoginThrottle(int maxTrackedKeys) => _maxTrackedKeys = maxTrackedKeys;

            /// <summary>Number of tracked keys — for tests asserting the map stays bounded.</summary>
            internal int TrackedKeyCount => _entries.Count;

            public bool IsLockedOut(string key, LocalAuthOptions options)
            {
                if (options.MaxFailedLoginAttempts <= 0)
                    return false;
                if (!_entries.TryGetValue(key, out var entry))
                    return false;

                lock (entry)
                {
                    if (DateTimeOffset.UtcNow - entry.WindowStart >= options.LockoutWindow)
                    {
                        // Window elapsed: the caller is no longer locked out.
                        entry.Failures = 0;
                        entry.WindowStart = DateTimeOffset.UtcNow;
                        return false;
                    }

                    return entry.Failures >= options.MaxFailedLoginAttempts;
                }
            }

            public void RecordFailure(string key, LocalAuthOptions options)
            {
                if (options.MaxFailedLoginAttempts <= 0)
                    return;

                // Already-tracked key: update in place. An account already being brute-forced stays
                // tracked and locked out regardless of map pressure — it is never evicted, so the
                // cap can never be turned into a throttle bypass.
                if (_entries.TryGetValue(key, out var existing))
                {
                    Touch(existing, options);
                    return;
                }

                // New key: enforce the hard cap BEFORE inserting. First try to reclaim slots held by
                // entries whose lockout window has fully elapsed (they carry no live lockout —
                // IsLockedOut resets them on read anyway — so dropping them changes no decision). If
                // the map is still full of LIVE entries, refuse to track the new key rather than
                // evict a live counter (that would drop a victim's lockout) or grow without bound (a
                // memory-exhaustion DoS on the unauthenticated path).
                if (_entries.Count >= _maxTrackedKeys)
                {
                    TrySweepElapsed(options.LockoutWindow);
                    if (_entries.Count >= _maxTrackedKeys)
                        return;
                }

                var entry = _entries.GetOrAdd(key, _ => new Entry { WindowStart = DateTimeOffset.UtcNow });
                Touch(entry, options);
            }

            private static void Touch(Entry entry, LocalAuthOptions options)
            {
                lock (entry)
                {
                    if (DateTimeOffset.UtcNow - entry.WindowStart >= options.LockoutWindow)
                    {
                        entry.Failures = 0;
                        entry.WindowStart = DateTimeOffset.UtcNow;
                    }
                    entry.Failures++;
                }
            }

            private void TrySweepElapsed(TimeSpan window)
            {
                // At most one full-map scan per lockout window, and one thread at a time: a sustained
                // at-cap flood costs O(1) amortized per RecordFailure, never an O(n) scan per failure.
                // A thread that finds the sweep already running (or run too recently) skips it and the
                // caller simply refuses the new key — correctness never depends on the sweep firing.
                if (!Monitor.TryEnter(_sweepGate))
                    return;
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastSweepUtc < window)
                        return;
                    _lastSweepUtc = now;
                    foreach (var kvp in _entries)
                    {
                        var entry = kvp.Value;
                        bool elapsed;
                        lock (entry)
                            elapsed = now - entry.WindowStart >= window;
                        if (elapsed)
                            // Conditional remove: only drops THIS entry instance, so a key another
                            // thread just re-activated (GetOrAdd reuses the object) is not dropped.
                            _entries.TryRemove(KeyValuePair.Create(kvp.Key, entry));
                    }
                }
                finally
                {
                    Monitor.Exit(_sweepGate);
                }
            }

            public void Clear(string key) => _entries.TryRemove(key, out _);
        }

        private static async Task HandleLogoutAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }

        /// <summary>
        /// Returns the current session as the camelCase <see cref="AppIdentity"/> contract
        /// the app-shell SessionProvider reads, reconstructed from the authenticated
        /// <see cref="ClaimsPrincipal"/> via the same <see cref="BifrostContext.BuildAppIdentity"/>
        /// path the GraphQL pipeline uses. Returns 401 when the request carries no
        /// authenticated principal. Only the public AppIdentity fields are written: the
        /// database credentials and the raw cookie claims never reach the client.
        /// </summary>
        internal static async Task HandleSessionAsync(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var principal = context.User;
            if (principal?.Identity == null || !principal.Identity.IsAuthenticated)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var identity = BifrostContext.BuildAppIdentity(principal);
            await context.Response.WriteAsJsonAsync(identity, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>Claim types used by local auth that have no standard <see cref="ClaimTypes"/> equivalent.</summary>
    public static class LocalAuthClaims
    {
        /// <summary>Claim carrying the authentication provider name (e.g. <c>local</c>).</summary>
        public const string Provider = "bifrost:provider";

        /// <summary>Claim carrying the user's primary tenant identifier.</summary>
        public const string Tenant = "bifrost:tenant";

        /// <summary>Claim carrying an organization/group identifier the user belongs to. Repeated per org.</summary>
        public const string Org = "bifrost:org";

        /// <summary>
        /// Claim carrying the household identifier resolved from the user's member
        /// row. Carried through the cookie so <see cref="BifrostContext.BuildAppIdentity"/>
        /// can re-surface it as the <c>household_id</c> provider claim.
        /// </summary>
        public const string Household = "bifrost:household";
    }

    /// <summary>JSON body accepted by the local auth login endpoint.</summary>
    public sealed record LocalLoginRequest
    {
        /// <summary>The login name (matched against the configured login column).</summary>
        public string? Login { get; init; }

        /// <summary>The plaintext password, verified server-side against the stored hash.</summary>
        public string? Password { get; init; }
    }
}

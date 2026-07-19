using BifrostQL.Core.Modules;
using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server
{
    /// <summary>
    /// Outcome of resolving the active profile for a request. Either an error
    /// (<see cref="ErrorMessage"/> set) or a resolved profile. A null
    /// <see cref="ProfileName"/> with no error means the empty default profile.
    /// </summary>
    internal readonly struct BifrostProfileResolution
    {
        /// <summary>The client-requested profile name, or null when none was requested.</summary>
        public string? ProfileName { get; init; }

        /// <summary>The resolved named profile, or null for the empty default profile.</summary>
        public BifrostProfile? Profile { get; init; }

        /// <summary>Set when resolution failed (unknown profile, or role/auth denied).</summary>
        public string? ErrorMessage { get; init; }

        public bool HasError => ErrorMessage != null;

        /// <summary>
        /// The profile to apply: the resolved named profile, or the empty default profile
        /// (raw base schema). The default profile still runs the fail-closed transformer
        /// filter, so security/data-integrity modules remain active.
        /// </summary>
        public BifrostProfile ActiveProfile => Profile ?? BifrostProfileResolver.DefaultProfile;
    }

    /// <summary>
    /// Resolves and authorizes the active BifrostQL profile for a request from its
    /// transport context (header / query param / path segment). Shared by the HTTP
    /// middleware and the protocol-independent <see cref="BifrostEngine"/> (which serves
    /// the binary WebSocket transport) so both enforce identical profile role gating and
    /// per-profile module filtering — the binary path must not become an authorization
    /// bypass around the HTTP path.
    /// </summary>
    internal static class BifrostProfileResolver
    {
        /// <summary>
        /// The system default profile (<see cref="ProfileNames.System.Default"/>): raw base
        /// schema, an explicit empty (non-null) module list so
        /// <see cref="BifrostProfileRegistry.FilterBy(IFilterTransformers, BifrostProfile)"/>
        /// runs the fail-closed filter (security/data-integrity modules stay active, opt-in
        /// application modules are stripped). The empty array is deliberate — a null module
        /// list would short-circuit the filter and activate every application-band opt-in.
        /// </summary>
        public static readonly BifrostProfile DefaultProfile =
            new() { Name = ProfileNames.System.Default, Modules = Array.Empty<string>() };

        /// <summary>
        /// Resolves the active profile for a request, enforcing any <see cref="BifrostProfile.RequireRole"/>.
        /// A request that names no profile resolves to the system <see cref="DefaultProfile"/>.
        /// Every named profile — including one registered under the literal name
        /// <c>default</c> — resolves through the <see cref="BifrostProfileRegistry"/>, so a
        /// registered <c>default</c> is honored rather than shadowed by the synthetic fallback.
        /// A named profile absent from the registry (including an explicit
        /// <see cref="ProfileNames.System.Default"/>, which is never registrable) or whose role
        /// requirement the caller does not satisfy produces an error (fail-closed).
        /// </summary>
        public static BifrostProfileResolution Resolve(BifrostProfileRegistry? registry, HttpContext? context)
        {
            var profileName = context != null ? ResolveProfileName(context) : null;

            // No requested name → the system default profile (raw schema). Note: the literal
            // "default" is NO LONGER special-cased — it resolves through the registry like any
            // name, and an explicit reserved ".default" simply misses the registry (unknown).
            if (profileName == null)
                return new BifrostProfileResolution { ProfileName = profileName };

            // A named profile requires a registry that knows it.
            var profile = registry?.Get(profileName);
            if (profile == null)
            {
                // Never echo raw attacker-controlled input verbatim (reflected-text / log
                // injection surface). Only surface the requested name when it matches a safe
                // identifier charset; otherwise keep the error generic.
                var display = IsSafeProfileName(profileName) ? $" '{profileName}'" : string.Empty;
                return new BifrostProfileResolution { ErrorMessage = $"Unknown profile{display}." };
            }

            if (profile.RequireRole != null)
            {
                var user = context!.User;
                if (user?.Identity?.IsAuthenticated != true)
                    return new BifrostProfileResolution { ErrorMessage = $"Profile '{profileName}' requires authentication." };

                if (!user.IsInRole(profile.RequireRole))
                    return new BifrostProfileResolution { ErrorMessage = $"Profile '{profileName}' requires role '{profile.RequireRole}'." };
            }

            return new BifrostProfileResolution { ProfileName = profileName, Profile = profile };
        }

        /// <summary>
        /// Extracts the requested profile name from the transport context. Priority:
        /// X-BifrostQL-Profile header &gt; ?profile= query parameter. Returns null when none
        /// is present.
        /// </summary>
        /// <remarks>
        /// Decision: the former "any single trailing path segment is a profile name" fallback
        /// has been removed. It turned every URL suffix (e.g. <c>/graphql/v1</c>) into a
        /// security-relevant selector and echoed attacker text into errors, widening the audit
        /// surface for no real benefit — the explicit header and query channels fully cover the
        /// documented use. Profile selection is now an explicit opt-in via header or query only.
        /// </remarks>
        public static string? ResolveProfileName(HttpContext context)
        {
            // Priority: Header > Query parameter
            if (context.Request.Headers.TryGetValue("X-BifrostQL-Profile", out var headerValue)
                && !string.IsNullOrWhiteSpace(headerValue))
            {
                return headerValue.ToString().Trim();
            }

            if (context.Request.Query.TryGetValue("profile", out var queryValue)
                && !string.IsNullOrWhiteSpace(queryValue))
            {
                return queryValue.ToString().Trim();
            }

            return null;
        }

        /// <summary>
        /// Whether a requested profile name is a safe identifier (letters, digits, and a small
        /// set of separators) that may be reflected back in an error message. Anything else is
        /// kept out of responses/logs.
        /// </summary>
        private static bool IsSafeProfileName(string name)
        {
            if (name.Length is 0 or > 64)
                return false;
            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
                    return false;
            }
            return true;
        }
    }
}

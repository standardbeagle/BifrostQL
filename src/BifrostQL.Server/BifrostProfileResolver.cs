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
        /// The empty default profile: raw base schema, an explicit empty (non-null) module
        /// list so <see cref="BifrostProfileRegistry.FilterBy(IFilterTransformers, BifrostProfile)"/>
        /// runs the fail-closed filter (security/data-integrity modules stay active, opt-in
        /// application modules are stripped).
        /// </summary>
        public static readonly BifrostProfile DefaultProfile =
            new() { Name = "default", Modules = Array.Empty<string>() };

        /// <summary>
        /// Resolves the active profile for a request, enforcing any <see cref="BifrostProfile.RequireRole"/>.
        /// A null/empty/"default" name resolves to the empty default profile. A named profile
        /// that is absent from the registry, or whose role requirement the caller does not
        /// satisfy, produces an error (fail-closed).
        /// </summary>
        public static BifrostProfileResolution Resolve(BifrostProfileRegistry? registry, HttpContext? context)
        {
            var profileName = context != null ? ResolveProfileName(context) : null;

            // No name, or the explicit "default" → empty default profile (raw schema).
            if (profileName == null || string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase))
                return new BifrostProfileResolution { ProfileName = profileName };

            // A named profile requires a registry that knows it.
            var profile = registry?.Get(profileName);
            if (profile == null)
                return new BifrostProfileResolution { ErrorMessage = $"Unknown profile '{profileName}'." };

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
        /// X-BifrostQL-Profile header &gt; ?profile= query parameter &gt; single path segment
        /// after the mapped endpoint. Returns null when none is present.
        /// </summary>
        public static string? ResolveProfileName(HttpContext context)
        {
            // Priority: Header > Query parameter > Path segment
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

            // Check for path segment after the mapped endpoint path.
            // After app.Map(), Path contains the remainder (e.g., "/direct" if mapped at "/graphql").
            var path = context.Request.Path.Value;
            if (!string.IsNullOrEmpty(path) && path.Length > 1)
            {
                var segment = path.TrimStart('/');
                if (!string.IsNullOrEmpty(segment) && !segment.Contains('/'))
                    return segment;
            }

            return null;
        }
    }
}

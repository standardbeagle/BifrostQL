namespace BifrostQL.Core.Modules;

/// <summary>
/// Reserved profile-name constants and the system-reserved namespace rule.
/// Profile names beginning with <see cref="SystemPrefix"/> ('.') form a
/// system-only namespace: the host synthesizes them internally (e.g. the
/// implicit <see cref="System.Default"/> fallback profile) and rejects them at
/// registration, so a client-registered profile can never silently shadow one.
/// Centralizing the names here keeps the reserved namespace free of magic
/// strings (mirrors the <c>MetadataKeys</c> pattern).
/// </summary>
public static class ProfileNames
{
    /// <summary>
    /// Prefix marking the system-reserved profile namespace. A profile whose
    /// name starts with this character is host-synthesized and not registrable
    /// (see <see cref="IsSystemReserved"/>).
    /// </summary>
    public const char SystemPrefix = '.';

    /// <summary>System-synthesized profile names; never client-registrable.</summary>
    public static class System
    {
        /// <summary>
        /// The implicit fallback profile, applied when a request names no profile:
        /// the raw base schema carrying an explicit empty (non-null) module list, so
        /// <see cref="BifrostProfileRegistry.FilterBy(IFilterTransformers, BifrostProfile)"/>
        /// runs the fail-closed filter — application-band opt-in modules
        /// (priority &gt;= <see cref="BifrostProfile.ApplicationPriorityFloor"/>) are
        /// stripped while the security/data-integrity band is retained. Requesting
        /// this name explicitly is rejected as an unknown profile (fail-closed): it
        /// is never in the registry because the reserved namespace cannot be
        /// registered, so the resolver's registry lookup misses.
        /// </summary>
        public const string Default = ".default";
    }

    /// <summary>
    /// Whether <paramref name="name"/> lies in the system-reserved namespace
    /// (begins with <see cref="SystemPrefix"/>) and therefore cannot be registered
    /// as a client profile.
    /// </summary>
    public static bool IsSystemReserved(string? name) =>
        !string.IsNullOrEmpty(name) && name[0] == SystemPrefix;
}

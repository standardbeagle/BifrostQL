namespace BifrostQL.Core.Modules;

/// <summary>
/// Marker interface for modules that can be selectively enabled/disabled by profile name.
/// Modules that do not implement this interface are always active regardless of profile.
/// </summary>
public interface IModuleNamed
{
    /// <summary>
    /// The profile-friendly name for this module (e.g., "audit", "soft-delete", "tenant-filter").
    /// Used in profile configuration to selectively enable/disable modules.
    /// </summary>
    string ModuleName { get; }
}

/// <summary>
/// A named configuration profile that controls which modules are active for a request.
/// Profiles allow different views of the same database (e.g., admin, migration, direct).
/// </summary>
public sealed class BifrostProfile
{
    /// <summary>
    /// Key used to store the active profile in UserContext for downstream resolvers.
    /// </summary>
    public const string UserContextKey = "_bifrostProfile";

    /// <summary>
    /// Priority floor for the application band. Transformers whose <c>Priority</c> is
    /// BELOW this value belong to the security band (0-99: tenant isolation, RLS,
    /// audit) or the data-integrity band (100-199: soft-delete, enum coercion). Those
    /// modules are ALWAYS active and cannot be disabled by profile selection —
    /// <see cref="BifrostProfileRegistry.FilterBy(IFilterTransformers, BifrostProfile)"/>
    /// and its mutation counterpart only add/remove application-band modules
    /// (priority &gt;= this floor).
    ///
    /// This makes profile filtering fail-closed: a client-selectable profile (including
    /// the empty "default" profile, whose name is fully client-controlled) cannot
    /// silently strip tenant isolation or soft-delete filtering by omitting the module
    /// name. See <see cref="IsProfileToggleable(int)"/>.
    /// </summary>
    public const int ApplicationPriorityFloor = 200;

    /// <summary>
    /// Whether a module at the given transformer priority may be enabled/disabled by
    /// profile selection. Only application-band modules (priority &gt;= the floor) are
    /// toggleable; security and data-integrity modules below the floor are always active.
    /// </summary>
    public static bool IsProfileToggleable(int priority) => priority >= ApplicationPriorityFloor;

    /// <summary>
    /// Profile name (e.g., "default", "admin", "direct").
    /// </summary>
    public string Name { get; init; } = "default";

    /// <summary>
    /// Human-friendly display label for this profile (e.g., "Sales (curated)").
    /// Null falls back to <see cref="Name"/> for presentation.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Module names enabled for this profile. Empty means no modules active.
    /// Null means all modules active (default profile behavior).
    /// </summary>
    public string[]? Modules { get; init; }

    /// <summary>
    /// Metadata rules (same grammar as the BifrostQL:Metadata config section) that
    /// define this profile's shape: visible tables/columns, opt-in joins, and the
    /// per-table configuration its modules read. Null/empty = no overlay (the raw
    /// base schema). Applied when building this profile's schema.
    /// </summary>
    public IReadOnlyList<string>? Metadata { get; init; }

    /// <summary>
    /// Role required to use this profile. Null means no authorization required.
    /// When set, the user must have this role in their claims to select this profile.
    /// </summary>
    public string? RequireRole { get; init; }

    /// <summary>
    /// Returns true if the given module should be active under this profile.
    /// Modules that do not implement <see cref="IModuleNamed"/> are always active.
    /// When <see cref="Modules"/> is null, all modules are active (default behavior).
    /// </summary>
    public bool IsModuleActive(object module)
    {
        if (Modules == null)
            return true;

        if (module is not IModuleNamed named)
            return true;

        return Modules.Contains(named.ModuleName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the active profile from a UserContext dictionary.
    /// Returns null if no profile is set (default behavior).
    /// </summary>
    public static BifrostProfile? FromUserContext(IDictionary<string, object?> userContext)
    {
        return userContext.TryGetValue(UserContextKey, out var value) ? value as BifrostProfile : null;
    }
}

/// <summary>
/// Registry of named profiles. When no profiles are configured, all requests
/// use the implicit default profile (all modules active).
/// </summary>
public sealed class BifrostProfileRegistry
{
    // Atomic snapshot: reads are lock-free against the current immutable snapshot;
    // mutations build a fresh dictionary under the write lock and swap the volatile
    // reference, so an in-flight request always sees one consistent set of profiles
    // (never a half-applied rebind). Used by the desktop's per-connection rebind.
    private readonly object _writeLock = new();
    private volatile IReadOnlyDictionary<string, BifrostProfile> _profiles =
        new Dictionary<string, BifrostProfile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a profile. Overwrites any existing profile with the same name.
    /// </summary>
    public void Add(BifrostProfile profile)
    {
        lock (_writeLock)
        {
            var next = new Dictionary<string, BifrostProfile>(_profiles, StringComparer.OrdinalIgnoreCase)
            {
                [profile.Name] = profile,
            };
            _profiles = next;
        }
    }

    /// <summary>
    /// Atomically replaces the entire profile set with the supplied profiles.
    /// Old profiles not present in the new set are removed. Safe against in-flight
    /// reads (snapshot swap).
    /// </summary>
    public void ReplaceAll(IEnumerable<BifrostProfile> profiles)
    {
        var next = new Dictionary<string, BifrostProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
            next[profile.Name] = profile;

        lock (_writeLock)
        {
            _profiles = next;
        }
    }

    /// <summary>
    /// Atomically clears all profiles, returning the registry to its empty state
    /// (no named profiles ⇒ default raw-schema behavior).
    /// </summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _profiles = new Dictionary<string, BifrostProfile>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gets a profile by name. Returns null if not found.
    /// </summary>
    public BifrostProfile? Get(string name)
    {
        return _profiles.TryGetValue(name, out var profile) ? profile : null;
    }

    /// <summary>
    /// A consistent snapshot of all registered profiles.
    /// </summary>
    public IReadOnlyCollection<BifrostProfile> All => _profiles.Values.ToArray();

    /// <summary>
    /// Whether any profiles have been explicitly configured.
    /// When false, all requests use default behavior (all modules active).
    /// </summary>
    public bool HasProfiles => _profiles.Count > 0;

    /// <summary>
    /// Creates filtered wrapper collections for a given profile. Fail-closed: security
    /// and data-integrity modules (priority below <see cref="BifrostProfile.ApplicationPriorityFloor"/>)
    /// are always retained regardless of the profile's module list, so a client-selectable
    /// profile can never strip tenant isolation or soft-delete filtering. Only
    /// application-band modules are gated by <see cref="BifrostProfile.IsModuleActive"/>.
    /// </summary>
    public static IFilterTransformers FilterBy(IFilterTransformers source, BifrostProfile profile)
    {
        if (profile.Modules == null)
            return source;

        var filtered = source
            .Where(t => !BifrostProfile.IsProfileToggleable(t.Priority) || profile.IsModuleActive(t))
            .ToList();
        return new FilterTransformersWrap { Transformers = filtered };
    }

    /// <summary>
    /// Creates filtered wrapper collections for a given profile. Fail-closed: see the
    /// filter-transformer overload — security and data-integrity mutation transformers
    /// (priority below <see cref="BifrostProfile.ApplicationPriorityFloor"/>, e.g. the
    /// tenant and soft-delete write guards) are always retained.
    /// </summary>
    public static IMutationTransformers FilterBy(IMutationTransformers source, BifrostProfile profile)
    {
        if (profile.Modules == null)
            return source;

        var filtered = source
            .Where(t => !BifrostProfile.IsProfileToggleable(t.Priority) || profile.IsModuleActive(t))
            .ToList();
        return new MutationTransformersWrap { Transformers = filtered };
    }

    /// <summary>
    /// Write-path convenience: reads the active profile from the request
    /// <paramref name="userContext"/> (stamped there by both the HTTP middleware and the
    /// binary transport) and applies the same fail-closed module filter the read path
    /// uses, so a named profile governs writes and reads symmetrically. A transport that
    /// never stamped a profile (no key present) leaves the full transformer set active —
    /// fail-closed for writes, since keeping a guard can only tighten, never weaken, a
    /// mutation. Every built-in security/data-integrity mutation transformer sits below
    /// <see cref="BifrostProfile.ApplicationPriorityFloor"/> and is therefore always
    /// retained regardless of the profile's module list.
    /// </summary>
    public static IMutationTransformers FilterBy(IMutationTransformers source, IDictionary<string, object?> userContext)
    {
        var profile = BifrostProfile.FromUserContext(userContext);
        return profile == null ? source : FilterBy(source, profile);
    }

    /// <summary>
    /// Returns observers for a given profile. Fail-closed like the filter/mutation
    /// overloads, but observers carry no <c>Priority</c>, so there is no band to place
    /// them in — a profile cannot tell a security-relevant audit observer from a
    /// disposable metrics hook. Treat every observer as non-toggleable and always retain
    /// it, so a client-selectable profile (including the empty "default" profile) can
    /// never silently strip an audit/lifecycle observer by omitting its module name. If
    /// observers ever gain a priority band, mirror the filter/mutation gating here.
    /// </summary>
    public static IQueryObservers FilterBy(IQueryObservers source, BifrostProfile profile)
    {
        _ = profile;
        return source;
    }
}

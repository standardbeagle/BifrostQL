using BifrostQL.Core.Model;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// One published entry family: a mapped table, the container its entries hang under, and the
    /// column that supplies each entry's RDN value.
    /// </summary>
    internal sealed class LdapEntryTarget
    {
        public LdapEntryTarget(
            IDbTable table,
            LdapMappingConfig config,
            string containerDn,
            string containerKey)
        {
            Table = table;
            Config = config;
            ContainerDn = containerDn;
            ContainerKey = containerKey;
        }

        public IDbTable Table { get; }

        public LdapMappingConfig Config { get; }

        /// <summary>The DN of the container these entries sit directly under (base DN included).</summary>
        public string ContainerDn { get; }

        /// <summary>The canonical comparison key of <see cref="ContainerDn"/>.</summary>
        public string ContainerKey { get; }

        public string NamingAttribute => Config.NamingAttribute!;

        public string NamingColumn => Config.NamingColumn!;

        /// <summary>The DN of one entry, from its RDN value. The value is escaped, never interpolated raw.</summary>
        public string EntryDn(string namingValue) =>
            ContainerDn.Length == 0
                ? LdapDn.FormatRdn(NamingAttribute, namingValue)
                : $"{LdapDn.FormatRdn(NamingAttribute, namingValue)},{ContainerDn}";

        /// <summary>
        /// The attribute→column mappings this entry publishes. The credential column is absent by
        /// construction: <see cref="LdapMappingConfig"/> refuses to parse a mapping that exposes it,
        /// and refuses to name an entry by it, so neither this list nor <see cref="EntryDn"/> can
        /// carry it.
        /// </summary>
        public IReadOnlyList<LdapAttributeMapping> Attributes => Config.Attributes;
    }

    /// <summary>What a search's base object and scope resolved to.</summary>
    internal enum LdapScopeKind
    {
        /// <summary>The base names nothing this directory publishes, or names it in a malformed way.</summary>
        NoSuchObject,

        /// <summary>The RootDSE (the empty DN read at base scope).</summary>
        RootDse,

        /// <summary>The subschema subentry.</summary>
        Subschema,

        /// <summary>One specific entry, addressed by its full DN.</summary>
        SingleEntry,

        /// <summary>Every entry of one or more tables, over a container or the whole tree.</summary>
        EntrySet,
    }

    /// <summary>
    /// The outcome of resolving a SearchRequest's base object and scope against the configured
    /// directory. <see cref="Targets"/> is the set of entry families in scope;
    /// <see cref="NamingValue"/> is set only for <see cref="LdapScopeKind.SingleEntry"/> and is the
    /// unescaped RDN value the base DN addressed.
    /// </summary>
    internal sealed record LdapScopeResolution(
        LdapScopeKind Kind,
        IReadOnlyList<LdapEntryTarget> Targets,
        string? NamingValue = null)
    {
        public static readonly LdapScopeResolution None =
            new(LdapScopeKind.NoSuchObject, Array.Empty<LdapEntryTarget>());
    }

    /// <summary>
    /// The DN-addressable view of the directory: the base DN, one <see cref="LdapEntryTarget"/> per
    /// visible mapped table, and the resolution of a search's (baseObject, scope) pair onto those
    /// targets. Built once per model — it is a pure projection of
    /// <see cref="LdapDirectoryModel"/> with no wire, clock, or randomness, so the same model always
    /// yields the same index.
    ///
    /// <para><b>Anti-oracle contract.</b> Resolution answers only "these targets" or "nothing".
    /// Every way of naming something this directory does not publish — a DN outside the base, a DN
    /// that does not parse, a container that exists in no mapping, an entry DN whose row the bound
    /// identity cannot see — produces the SAME <see cref="LdapScopeKind.NoSuchObject"/>, which the
    /// caller answers with one uniform result code and no diagnostic. Nothing in the resolution
    /// path consults row data, so the shape of the answer cannot vary with what exists.</para>
    ///
    /// <para>Note that resolution never authorizes: it narrows the DN space to candidate TABLES.
    /// Whether the bound identity may read any row of those tables is decided by the query pipeline
    /// (tenant, policy, soft-delete) on the executed intent, which is the same gate the GraphQL
    /// front door enforces — never a second, weaker check here
    /// (protocol-adapter-security invariant 4).</para>
    /// </summary>
    internal sealed class LdapDirectoryIndex
    {
        private readonly IReadOnlyList<LdapEntryTarget> _targets;
        private readonly string _baseKey;

        private LdapDirectoryIndex(
            LdapDirectoryModel model,
            IReadOnlyList<LdapEntryTarget> targets,
            string baseKey)
        {
            Model = model;
            _targets = targets;
            _baseKey = baseKey;
        }

        /// <summary>The underlying deterministic directory model (RootDSE, subschema, base DN).</summary>
        public LdapDirectoryModel Model { get; }

        public string BaseDn => Model.BaseDn;

        /// <summary>Every published entry family, in the model's deterministic order.</summary>
        public IReadOnlyList<LdapEntryTarget> Targets => _targets;

        /// <summary>
        /// Builds the index for a model, or null when no table opts into the directory. Throws
        /// <see cref="LdapConfigurationException"/> when the configured base DN or a DN template
        /// does not parse — a directory whose DNs cannot be rendered must not start serving, since
        /// every entry it published would be misnamed.
        /// </summary>
        public static LdapDirectoryIndex? Build(IDbModel model)
        {
            ArgumentNullException.ThrowIfNull(model);

            var directory = LdapDirectoryModel.FromModel(model);
            if (directory is null)
                return null;

            var baseKey = LdapDn.CanonicalKey(directory.BaseDn);
            if (baseKey is null)
                throw new LdapConfigurationException(
                    $"the configured '{MetadataKeys.Ldap.BaseDn}' is not a valid distinguished name. "
                    + "Every published entry is named relative to it, so serving would misname them all.");

            var targets = new List<LdapEntryTarget>();
            foreach (var table in model.Tables.OrderBy(t => $"{t.TableSchema}.{t.DbName}", StringComparer.Ordinal))
            {
                var config = LdapMappingConfig.FromTable(table);
                if (!config.IsMapped)
                    continue;
                if (table.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
                    continue;

                targets.Add(BuildTarget(table, config, directory.BaseDn));
            }

            return new LdapDirectoryIndex(directory, targets, baseKey);
        }

        // The container is the DN template minus its RDN, suffixed with the base DN. The template's
        // grammar is already validated at model load (leftmost component is the RDN, every other is
        // a static attr=value), so anything unparseable here is a configuration fault, not client input.
        private static LdapEntryTarget BuildTarget(IDbTable table, LdapMappingConfig config, string baseDn)
        {
            var components = LdapDn.SplitUnescaped(config.DnTemplate!, ',')
                .Skip(1)
                .Select(c => c.Trim())
                .Where(c => c.Length > 0);

            var container = string.Join(",", components.Append(baseDn).Where(c => c.Length > 0));
            var containerKey = LdapDn.CanonicalKey(container)
                ?? throw new LdapConfigurationException(
                    $"the '{MetadataKeys.Ldap.DnTemplate}' of '{table.TableSchema}.{table.DbName}' does not "
                    + "resolve to a valid container distinguished name.");

            return new LdapEntryTarget(table, config, container, containerKey);
        }

        /// <summary>
        /// Resolves a search's base object and scope. Every failure mode collapses to
        /// <see cref="LdapScopeResolution.None"/> — see the anti-oracle contract on the type.
        /// </summary>
        public LdapScopeResolution Resolve(string baseObject, int scope)
        {
            // The RootDSE is the empty DN, readable only at base scope. A subtree search from the
            // empty DN is a request to walk the whole server, which this directory does not serve.
            if (baseObject.Length == 0)
                return scope == LdapSearchScope.BaseObject
                    ? new LdapScopeResolution(LdapScopeKind.RootDse, Array.Empty<LdapEntryTarget>())
                    : LdapScopeResolution.None;

            var key = LdapDn.CanonicalKey(baseObject);
            if (key is null)
                return LdapScopeResolution.None; // malformed DN: indistinguishable from naming nothing

            var subschemaKey = LdapDn.CanonicalKey(LdapDirectoryModel.SubschemaSubentryDn);
            if (string.Equals(key, subschemaKey, StringComparison.Ordinal))
                return scope == LdapSearchScope.BaseObject
                    ? new LdapScopeResolution(LdapScopeKind.Subschema, Array.Empty<LdapEntryTarget>())
                    : LdapScopeResolution.None;

            return scope switch
            {
                LdapSearchScope.BaseObject => ResolveBaseScope(baseObject, key),
                LdapSearchScope.SingleLevel => ResolveOneLevel(key),
                LdapSearchScope.WholeSubtree => ResolveSubtree(baseObject, key),
                _ => LdapScopeResolution.None,
            };
        }

        // Base scope names exactly ONE entry, so the base DN must be an entry DN: its RDN attribute
        // must be some target's naming attribute and its parent must be that target's container.
        private LdapScopeResolution ResolveBaseScope(string baseObject, string key)
        {
            if (!LdapDn.TryParse(baseObject, out var components) || components.Count == 0)
                return LdapScopeResolution.None;

            var rdn = components[0];
            var parentKey = LdapDn.CanonicalKey(components.Skip(1).ToList());

            foreach (var target in _targets)
            {
                if (!string.Equals(target.ContainerKey, parentKey, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(target.NamingAttribute, rdn.Attribute, StringComparison.OrdinalIgnoreCase))
                    continue;

                return new LdapScopeResolution(
                    LdapScopeKind.SingleEntry, new[] { target }, rdn.Value);
            }

            // The DN parsed but names no published entry family. Same answer as a malformed DN and
            // as an entry the identity cannot see: the client learns only "not here".
            _ = key;
            return LdapScopeResolution.None;
        }

        // One level below a container: every target whose container IS that DN. Entries have no
        // children of their own in this directory, so a one-level search from an entry DN is empty
        // rather than an error — a legal DN naming a legal, childless node.
        private LdapScopeResolution ResolveOneLevel(string key)
        {
            var matched = _targets
                .Where(t => string.Equals(t.ContainerKey, key, StringComparison.Ordinal))
                .ToList();

            return matched.Count == 0
                ? LdapScopeResolution.None
                : new LdapScopeResolution(LdapScopeKind.EntrySet, matched);
        }

        // Subtree: every target at or below the base DN. The base may be the directory root, a
        // container, or a single entry (which resolves to just that entry, per RFC 4511 — the base
        // is included in a whole-subtree search).
        private LdapScopeResolution ResolveSubtree(string baseObject, string key)
        {
            var matched = _targets
                .Where(t => string.Equals(t.ContainerKey, key, StringComparison.Ordinal)
                            || LdapDn.IsDescendantOf(t.ContainerKey, key))
                .ToList();

            if (matched.Count > 0)
                return new LdapScopeResolution(LdapScopeKind.EntrySet, matched);

            // Not a container: the base may still name a single entry, whose subtree is itself.
            return ResolveBaseScope(baseObject, key);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model
{
    /// <summary>A published attribute type in the subschema — its name and derived LDAP syntax.</summary>
    public sealed record LdapAttributeType(string Name, LdapSyntax Syntax);

    /// <summary>
    /// The RootDSE — the anonymous, always-readable root entry a client reads to discover the
    /// directory. Every value is derived deterministically from the configured mappings; there
    /// is no ambient time or randomness, so the same configuration always yields the same
    /// RootDSE.
    /// </summary>
    public sealed record LdapRootDse(
        IReadOnlyList<string> NamingContexts,
        IReadOnlyList<string> SupportedLdapVersion,
        string SubschemaSubentry,
        string VendorName);

    /// <summary>
    /// The subschema subentry — the distinct set of objectClasses and attributeTypes the
    /// directory publishes, generated from only the configured VISIBLE mappings (hidden tables
    /// and the never-returned credential column are excluded). Both lists are sorted so the
    /// output is deterministic.
    /// </summary>
    public sealed record LdapSubschema(
        IReadOnlyList<string> ObjectClasses,
        IReadOnlyList<LdapAttributeType> AttributeTypes);

    /// <summary>A single published directory entry mapping (one per visible mapped table).</summary>
    public sealed record LdapEntryMapping(
        string TableSchema,
        string TableDbName,
        IReadOnlyList<string> ObjectClasses,
        string DnTemplate,
        string NamingAttribute,
        IReadOnlyList<LdapAttributeMapping> Attributes,
        string? MemberRelationship);

    /// <summary>
    /// The deterministic LDAP directory model generated from a validated <see cref="IDbModel"/>:
    /// the base DN, the per-table entry mappings, and the RootDSE / subschema discovery surface
    /// (LDAP slice 1, criterion 3). It is a PURE data projection of the configured mappings — no
    /// wire, no listener, no time or randomness — so <see cref="FromModel"/> is a total function
    /// of the configuration: the same model always produces an equal directory model. Only
    /// configured VISIBLE mappings contribute: a table hidden via <c>visibility: hidden</c> is
    /// excluded entirely, and the credential column is never a subschema attribute (the parser
    /// forbids exposing it). Assumes the model has already passed
    /// <see cref="ModelConfigValidator"/>.
    /// </summary>
    public sealed class LdapDirectoryModel
    {
        private LdapDirectoryModel(
            string baseDn,
            IReadOnlyList<LdapEntryMapping> entries,
            LdapRootDse rootDse,
            LdapSubschema subschema)
        {
            BaseDn = baseDn;
            Entries = entries;
            RootDse = rootDse;
            Subschema = subschema;
        }

        /// <summary>The base DN under which every entry is rooted.</summary>
        public string BaseDn { get; }

        /// <summary>The per-table entry mappings, sorted by qualified table name for determinism.</summary>
        public IReadOnlyList<LdapEntryMapping> Entries { get; }

        /// <summary>The RootDSE discovery entry.</summary>
        public LdapRootDse RootDse { get; }

        /// <summary>The subschema subentry (objectClasses + attributeTypes).</summary>
        public LdapSubschema Subschema { get; }

        /// <summary>The fixed subschema subentry DN a RootDSE points at.</summary>
        public const string SubschemaSubentryDn = "cn=subschema";

        /// <summary>The vendor name published in the RootDSE.</summary>
        public const string VendorName = "BifrostQL";

        /// <summary>
        /// Builds the directory model from a validated model, or null when no table opts into
        /// the LDAP directory (nothing is published absent the opt-in).
        /// </summary>
        public static LdapDirectoryModel? FromModel(IDbModel model)
        {
            if (model is null)
                throw new ArgumentNullException(nameof(model));

            var mapped = model.Tables
                .Select(t => (Table: t, Config: LdapMappingConfig.FromTable(t)))
                .Where(x => x.Config.IsMapped && IsVisible(x.Table))
                .OrderBy(x => $"{x.Table.TableSchema}.{x.Table.DbName}", StringComparer.Ordinal)
                .ToList();

            if (mapped.Count == 0)
                return null;

            var baseDn = model.GetMetadataValue(MetadataKeys.Ldap.BaseDn)?.Trim() ?? string.Empty;

            var entries = mapped
                .Select(x => new LdapEntryMapping(
                    x.Table.TableSchema,
                    x.Table.DbName,
                    x.Config.ObjectClasses,
                    x.Config.DnTemplate!,
                    x.Config.NamingAttribute!,
                    x.Config.Attributes,
                    x.Config.MemberRelationship))
                .ToList();

            var objectClasses = mapped
                .SelectMany(x => x.Config.ObjectClasses)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(o => o, StringComparer.Ordinal)
                .ToList();

            var attributeTypes = BuildAttributeTypes(mapped);

            var rootDse = new LdapRootDse(
                NamingContexts: new[] { baseDn },
                SupportedLdapVersion: new[] { "3" },
                SubschemaSubentry: SubschemaSubentryDn,
                VendorName: VendorName);

            var subschema = new LdapSubschema(objectClasses, attributeTypes);

            return new LdapDirectoryModel(baseDn, entries, rootDse, subschema);
        }

        // The published attribute types: every returned attribute across the visible mappings
        // (with its derived syntax), plus the synthesized 'member' attribute for any entry that
        // declares a membership relationship. The credential column never appears — the parser
        // forbids exposing it as an attribute — so the subschema cannot leak it. An attribute
        // that appears on more than one table keeps its first (ordinal-least table) syntax; a
        // genuine cross-table type conflict is a validation error, not resolved here.
        private static IReadOnlyList<LdapAttributeType> BuildAttributeTypes(
            IReadOnlyList<(IDbTable Table, LdapMappingConfig Config)> mapped)
        {
            var byName = new Dictionary<string, LdapAttributeType>(StringComparer.OrdinalIgnoreCase);

            foreach (var (table, config) in mapped)
            {
                foreach (var attribute in config.Attributes)
                {
                    if (byName.ContainsKey(attribute.Attribute))
                        continue;

                    var syntax = table.ColumnLookup.TryGetValue(attribute.Column, out var column)
                        ? LdapMappingConfig.ColumnSyntax(column.DataType)
                        : LdapSyntax.DirectoryString;
                    byName[attribute.Attribute] = new LdapAttributeType(attribute.Attribute, syntax);
                }

                if (config.MemberRelationship != null &&
                    !byName.ContainsKey(MetadataKeys.Ldap.MemberAttributeName))
                {
                    byName[MetadataKeys.Ldap.MemberAttributeName] =
                        new LdapAttributeType(MetadataKeys.Ldap.MemberAttributeName, LdapSyntax.DirectoryString);
                }
            }

            return byName.Values
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static bool IsVisible(IDbTable table) =>
            !table.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden);
    }
}

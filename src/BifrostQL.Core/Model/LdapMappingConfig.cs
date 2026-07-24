using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// The LDAP syntax class a mapped attribute (or a database column) carries. Reduced to
    /// the small set the directory contract needs in this slice: a text syntax
    /// (DirectoryString / IA5String, treated interchangeably here), an integer syntax, a
    /// boolean syntax, a generalized-time syntax, and an octet-string (binary) syntax.
    /// A column's class is derived from its database type; a well-known attribute's required
    /// class comes from <see cref="LdapMappingConfig.WellKnownAttributeSyntax"/>. An attribute
    /// mapped to a column whose class differs from the attribute's required class is rejected
    /// (attribute-syntax incompatibility) at model load.
    /// </summary>
    public enum LdapSyntax
    {
        DirectoryString,
        Integer,
        Boolean,
        GeneralizedTime,
        OctetString,
    }

    /// <summary>A single attribute→column mapping, canonicalized to the column's database casing.</summary>
    public sealed record LdapAttributeMapping(string Attribute, string Column);

    /// <summary>
    /// Parsed per-table LDAP directory mapping, built from the <c>ldap-object-class</c> /
    /// <c>ldap-dn-template</c> / <c>ldap-attributes</c> / <c>ldap-credential</c> /
    /// <c>ldap-member</c> table metadata. A table with no <c>ldap-object-class</c> key
    /// returns <see cref="None"/>, so nothing is published absent the opt-in.
    ///
    /// This is a STRUCTURAL parse: it validates the DN-template grammar, the naming-attribute
    /// requirement (the RDN attribute must be a returned attribute), the present-but-empty
    /// guards, attribute-list duplicates, and the security invariant that the credential
    /// column is never exposed as a returned attribute — all of which need only the table's
    /// own metadata. Column-existence, attribute-syntax compatibility, cross-table DN
    /// uniqueness, and relationship resolution require the model (or other tables) and live
    /// in <see cref="ModelConfigValidator"/>, which reuses this collector so validation
    /// cannot drift from the runtime parse. Throws <see cref="InvalidOperationException"/> on
    /// a structural error so a typo fails fast rather than silently publishing the wrong (or
    /// no) directory entry.
    /// </summary>
    public sealed class LdapMappingConfig
    {
        // RDN of the DN template: attr={column}. The value must be exactly one placeholder —
        // the naming attribute value always comes from a single column in this slice.
        private static readonly Regex RdnGrammar =
            new(@"^([A-Za-z][A-Za-z0-9-]*)=\{([^{}]+)\}$", RegexOptions.Compiled);

        // A static (non-RDN) DN component: attr=value with no placeholder.
        private static readonly Regex StaticRdnGrammar =
            new(@"^([A-Za-z][A-Za-z0-9-]*)=([^{}=,]+)$", RegexOptions.Compiled);

        // A single attribute mapping token: attribute=column.
        private static readonly Regex AttributeMappingGrammar =
            new(@"^([A-Za-z][A-Za-z0-9-]*)=(.+)$", RegexOptions.Compiled);

        /// <summary>
        /// Well-known LDAP attributes with a required syntax class. An attribute absent from
        /// this registry is a free/custom attribute with no syntax constraint. The set is the
        /// inetOrgPerson / posixAccount / groupOfNames subset this slice recognizes; adding an
        /// attribute here tightens compatibility checking without changing the parse.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, LdapSyntax> WellKnownAttributeSyntax =
            new Dictionary<string, LdapSyntax>(StringComparer.OrdinalIgnoreCase)
            {
                ["uid"] = LdapSyntax.DirectoryString,
                ["cn"] = LdapSyntax.DirectoryString,
                ["sn"] = LdapSyntax.DirectoryString,
                ["givenName"] = LdapSyntax.DirectoryString,
                ["displayName"] = LdapSyntax.DirectoryString,
                ["mail"] = LdapSyntax.DirectoryString,
                ["ou"] = LdapSyntax.DirectoryString,
                ["o"] = LdapSyntax.DirectoryString,
                ["dc"] = LdapSyntax.DirectoryString,
                ["description"] = LdapSyntax.DirectoryString,
                ["title"] = LdapSyntax.DirectoryString,
                ["telephoneNumber"] = LdapSyntax.DirectoryString,
                [MetadataKeys.Ldap.MemberAttributeName] = LdapSyntax.DirectoryString,
                ["memberOf"] = LdapSyntax.DirectoryString,
                ["uidNumber"] = LdapSyntax.Integer,
                ["gidNumber"] = LdapSyntax.Integer,
                ["createTimestamp"] = LdapSyntax.GeneralizedTime,
                ["modifyTimestamp"] = LdapSyntax.GeneralizedTime,
            };

        /// <summary>The no-directory sentinel returned for tables that do not opt in.</summary>
        public static readonly LdapMappingConfig None = new(
            objectClasses: Array.Empty<string>(),
            dnTemplate: null, namingAttribute: null, namingColumn: null,
            normalizedDnTemplate: null,
            attributes: Array.Empty<LdapAttributeMapping>(),
            credentialColumn: null, memberRelationship: null);

        private LdapMappingConfig(
            IReadOnlyList<string> objectClasses,
            string? dnTemplate,
            string? namingAttribute,
            string? namingColumn,
            string? normalizedDnTemplate,
            IReadOnlyList<LdapAttributeMapping> attributes,
            string? credentialColumn,
            string? memberRelationship)
        {
            ObjectClasses = objectClasses;
            DnTemplate = dnTemplate;
            NamingAttribute = namingAttribute;
            NamingColumn = namingColumn;
            NormalizedDnTemplate = normalizedDnTemplate;
            Attributes = attributes;
            CredentialColumn = credentialColumn;
            MemberRelationship = memberRelationship;
        }

        /// <summary>Whether the table publishes an LDAP entry (carries a non-blank <c>ldap-object-class</c>).</summary>
        public bool IsMapped => ObjectClasses.Count > 0;

        /// <summary>The declared objectClasses in declaration order.</summary>
        public IReadOnlyList<string> ObjectClasses { get; }

        /// <summary>The raw DN template (relative to the base DN), or null when not mapped.</summary>
        public string? DnTemplate { get; }

        /// <summary>The RDN (naming) attribute parsed from the DN template, or null when not mapped.</summary>
        public string? NamingAttribute { get; }

        /// <summary>The column supplying the RDN value, canonicalized to DB casing, or null.</summary>
        public string? NamingColumn { get; }

        /// <summary>
        /// The deterministically normalized DN template — lowercased, whitespace-stripped, with
        /// the RDN value placeholder blanked to <c>{}</c> — used for cross-table DN uniqueness
        /// detection. Two tables whose entries would share a DN namespace normalize to the same
        /// value, a rejected collision rather than a silent overlap.
        /// </summary>
        public string? NormalizedDnTemplate { get; }

        /// <summary>The returned attribute→column mappings in declaration order.</summary>
        public IReadOnlyList<LdapAttributeMapping> Attributes { get; }

        /// <summary>The credential-verifier column (bind only, never returned), or null.</summary>
        public string? CredentialColumn { get; }

        /// <summary>The group-membership relationship name, or null when the entry has no members.</summary>
        public string? MemberRelationship { get; }

        // Cached per table instance (the model and its metadata are immutable after load,
        // and both the validator and the directory model ask for the same table's config).
        private static readonly ConditionalWeakTable<IDbTable, LdapMappingConfig> ConfigByTable = new();

        /// <summary>Parses the LDAP mapping for a single table (cached per table instance).</summary>
        public static LdapMappingConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            return ConfigByTable.GetValue(table, Parse);
        }

        private static LdapMappingConfig Parse(IDbTable table)
        {
            // Opt-in marker: no ldap-object-class key at all → not a directory table.
            if (!table.Metadata.ContainsKey(MetadataKeys.Ldap.ObjectClass))
                return None;

            var objectClasses = SplitList(table.GetMetadataValue(MetadataKeys.Ldap.ObjectClass)).ToList();
            if (objectClasses.Count == 0)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Ldap.ObjectClass}' is set but names no objectClass; a directory entry " +
                    "must present at least one objectClass.");

            var templateRaw = table.GetMetadataValue(MetadataKeys.Ldap.DnTemplate);
            if (string.IsNullOrWhiteSpace(templateRaw))
                throw new InvalidOperationException(
                    $"a mapped table requires a '{MetadataKeys.Ldap.DnTemplate}'; without it an entry cannot be named.");

            var (namingAttribute, namingColumn, normalizedTemplate) = ParseDnTemplate(table, templateRaw.Trim());

            var attributes = ParseAttributes(table);
            if (attributes.Count == 0)
                throw new InvalidOperationException(
                    $"a mapped table requires '{MetadataKeys.Ldap.Attributes}'; a directory entry with no " +
                    "returned attributes is empty.");

            // Required naming attribute: the RDN attribute must be a returned attribute, or a
            // search could never surface the value the entry is named by.
            if (!attributes.Any(a => string.Equals(a.Attribute, namingAttribute, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"the DN template's naming attribute '{namingAttribute}' is not listed in " +
                    $"'{MetadataKeys.Ldap.Attributes}'; the naming attribute must be a returned attribute.");

            var credentialColumn = ParseCredentialColumn(table, attributes);

            var memberRelationship = table.GetMetadataValue(MetadataKeys.Ldap.Member);
            if (memberRelationship != null && string.IsNullOrWhiteSpace(memberRelationship))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Ldap.Member}' is set but empty; name the group-membership relationship or remove the key.");
            memberRelationship = string.IsNullOrWhiteSpace(memberRelationship) ? null : memberRelationship.Trim();

            return new LdapMappingConfig(
                objectClasses, templateRaw.Trim(), namingAttribute, namingColumn,
                normalizedTemplate, attributes, credentialColumn, memberRelationship);
        }

        private static (string NamingAttribute, string NamingColumn, string NormalizedTemplate) ParseDnTemplate(
            IDbTable table, string template)
        {
            var components = template.Split(',', StringSplitOptions.TrimEntries);
            if (components.Length == 0 || string.IsNullOrWhiteSpace(components[0]))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Ldap.DnTemplate}' value '{template}' is not a valid DN template.");

            var rdnMatch = RdnGrammar.Match(components[0]);
            if (!rdnMatch.Success)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Ldap.DnTemplate}' RDN '{components[0]}' is not valid; the leftmost component " +
                    "must be 'attribute={column}' (e.g. 'uid={username}').");

            var namingAttribute = rdnMatch.Groups[1].Value;
            var placeholderColumn = rdnMatch.Groups[2].Value.Trim();
            var namingColumn = CanonicalizeColumn(table, placeholderColumn);

            // Every remaining component must be a static attr=value path segment; a stray
            // placeholder outside the RDN is rejected so a DN cannot depend on unresolved input.
            var normalizedParts = new List<string> { $"{namingAttribute.ToLowerInvariant()}={{}}" };
            foreach (var component in components.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(component) || !StaticRdnGrammar.IsMatch(component))
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.Ldap.DnTemplate}' component '{component}' is not a valid static DN " +
                        "segment; only the leftmost RDN may carry a '{column}' placeholder.");
                normalizedParts.Add(StringNormalizer.Normalize(component));
            }

            return (namingAttribute, namingColumn, string.Join(",", normalizedParts));
        }

        private static List<LdapAttributeMapping> ParseAttributes(IDbTable table)
        {
            var mappings = new List<LdapAttributeMapping>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in SplitList(table.GetMetadataValue(MetadataKeys.Ldap.Attributes)))
            {
                var match = AttributeMappingGrammar.Match(token);
                if (!match.Success)
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.Ldap.Attributes}' entry '{token}' is not a valid 'attribute=column' mapping.");

                var attribute = match.Groups[1].Value;
                var column = match.Groups[2].Value.Trim();
                if (column.Length == 0)
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.Ldap.Attributes}' entry '{token}' maps attribute '{attribute}' to an empty column.");

                if (!seen.Add(attribute))
                    throw new InvalidOperationException(
                        $"'{MetadataKeys.Ldap.Attributes}' lists attribute '{attribute}' more than once " +
                        "(attribute names match case-insensitively); remove the duplicate.");

                mappings.Add(new LdapAttributeMapping(attribute, CanonicalizeColumn(table, column)));
            }
            return mappings;
        }

        private static string? ParseCredentialColumn(IDbTable table, IReadOnlyList<LdapAttributeMapping> attributes)
        {
            var credential = table.GetMetadataValue(MetadataKeys.Ldap.Credential);
            if (string.IsNullOrWhiteSpace(credential))
                return null;

            var credentialColumn = CanonicalizeColumn(table, credential.Trim());

            // Security crux: the credential (password-hash) column is a bind-verification
            // input only and must NEVER be reachable as a searchable/returned attribute. A
            // mapping that exposes it — under ANY attribute name — turns the directory into a
            // password-hash read surface, so it is rejected at the parse boundary.
            var exposing = attributes.FirstOrDefault(a =>
                string.Equals(a.Column, credentialColumn, StringComparison.OrdinalIgnoreCase));
            if (exposing != null)
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Ldap.Credential}' column '{credentialColumn}' is also exposed as the " +
                    $"'{exposing.Attribute}' attribute in '{MetadataKeys.Ldap.Attributes}'; the credential column " +
                    "verifies binds only and must never be a searchable or returned attribute. Remove that mapping.");

            return credentialColumn;
        }

        /// <summary>
        /// Derives the LDAP syntax class a database column carries, from its type. Used by the
        /// validator to check attribute-syntax compatibility, and by the directory model to
        /// publish each attribute's syntax in the subschema.
        /// </summary>
        public static LdapSyntax ColumnSyntax(string dataType)
        {
            var type = StringNormalizer.NormalizeType(dataType);
            if (IntegerTypes.Contains(type)) return LdapSyntax.Integer;
            if (BooleanTypes.Contains(type)) return LdapSyntax.Boolean;
            if (TimestampTypes.Contains(type)) return LdapSyntax.GeneralizedTime;
            if (BinaryTypes.Contains(type)) return LdapSyntax.OctetString;
            return LdapSyntax.DirectoryString;
        }

        private static readonly IReadOnlySet<string> IntegerTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "int", "integer", "bigint", "smallint", "tinyint", "mediumint",
                "int2", "int4", "int8", "serial", "bigserial", "smallserial",
                "decimal", "numeric", "dec", "money", "smallmoney",
            };

        private static readonly IReadOnlySet<string> BooleanTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bit", "bool", "boolean" };

        private static readonly IReadOnlySet<string> TimestampTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset",
                "time", "timestamp", "timestamptz",
            };

        private static readonly IReadOnlySet<string> BinaryTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "binary", "varbinary", "image", "bytea", "blob", "rowversion", "timestamp_binary",
            };

        private static string CanonicalizeColumn(IDbTable table, string name) =>
            table.ColumnLookup.TryGetValue(name, out var column) ? column.ColumnName : name;

        private static IEnumerable<string> SplitList(string? raw) =>
            (raw ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

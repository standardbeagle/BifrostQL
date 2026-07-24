using BifrostQL.Core.Model;

namespace BifrostQL.Server.Ldap
{
    /// <summary>
    /// Projects the slice-1 deterministic directory model (<see cref="LdapRootDse"/> /
    /// <see cref="LdapSubschema"/>) into the <see cref="LdapSearchResultEntry"/> wire shape the
    /// codec encodes. These are the "RootDSE and subschema responses needed by the MVP"
    /// (criterion 1): a discovery client reads the RootDSE anonymously to learn the naming
    /// contexts and the subschema DN, then reads <c>cn=subschema</c> for the published classes and
    /// attribute types. The mapping is pure — no wire, no time, no randomness — mirroring the
    /// slice-1 model, so the same directory model always yields the same entries.
    ///
    /// <para>Slice 2 owns only the encoding of these entries (proven by the codec tests); wiring
    /// them to an executed search arrives with the search slice, since search execution is a
    /// non-goal here.</para>
    /// </summary>
    internal static class LdapDirectoryEntries
    {
        /// <summary>The RootDSE entry: DN is the empty string, attributes are the discovery surface.</summary>
        public static LdapSearchResultEntry RootDse(LdapRootDse rootDse) => new(
            ObjectName: string.Empty,
            Attributes: new[]
            {
                new LdapPartialAttribute("objectClass", new[] { "top" }),
                new LdapPartialAttribute("namingContexts", rootDse.NamingContexts),
                new LdapPartialAttribute("supportedLDAPVersion", rootDse.SupportedLdapVersion),
                new LdapPartialAttribute("subschemaSubentry", new[] { rootDse.SubschemaSubentry }),
                new LdapPartialAttribute("vendorName", new[] { rootDse.VendorName }),
            });

        /// <summary>The subschema subentry: the published objectClasses and attributeTypes.</summary>
        public static LdapSearchResultEntry Subschema(LdapSubschema subschema)
        {
            var attributeTypeNames = new List<string>(subschema.AttributeTypes.Count);
            foreach (var attributeType in subschema.AttributeTypes)
                attributeTypeNames.Add(attributeType.Name);

            return new LdapSearchResultEntry(
                ObjectName: LdapDirectoryModel.SubschemaSubentryDn,
                Attributes: new[]
                {
                    new LdapPartialAttribute("objectClass", new[] { "top", "subschema" }),
                    new LdapPartialAttribute("objectClasses", subschema.ObjectClasses),
                    new LdapPartialAttribute("attributeTypes", attributeTypeNames),
                });
        }
    }
}

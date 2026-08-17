using BifrostQL.Core.Model;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Base-object and scope resolution. Two properties are load-bearing here.
    ///
    /// <para><b>Only configured DNs resolve.</b> The DN space a client can address is exactly the
    /// set of containers and naming attributes the mappings declare. Nothing derives a table from
    /// client text, so a base object can never reach a table the configuration did not publish.</para>
    ///
    /// <para><b>Everything else is one answer.</b> A DN outside the base, a DN that does not parse,
    /// a container no mapping declares, and a well-formed entry DN for a row that does not exist all
    /// resolve to the same <c>NoSuchObject</c> — the caller answers them with one uniform result and
    /// no diagnostic. Resolution never reads row data, so the shape of the answer cannot vary with
    /// what exists, which is what keeps it from becoming an existence oracle.</para>
    /// </summary>
    public sealed class LdapDirectoryIndexTests
    {
        private static LdapDirectoryIndex Index(IDbModel? model = null) =>
            LdapDirectoryIndex.Build(model ?? LdapModelBuilder.Create().WithPeople().WithGroups().Build())!;

        [Fact]
        public void Build_WithNoMappedTable_ReturnsNull()
        {
            // Nothing is published absent the opt-in — there is no ambient directory.
            var model = LdapModelBuilder.Create()
                .WithTable("users", t => t.WithColumn("id", "int", isPrimaryKey: true))
                .Build();

            LdapDirectoryIndex.Build(model).Should().BeNull();
        }

        [Fact]
        public void Build_DerivesContainerDnFromTheTemplateAndBaseDn()
        {
            var index = Index();

            index.BaseDn.Should().Be("dc=example,dc=com");
            index.Targets.Should().HaveCount(2);
            index.Targets.Select(t => t.ContainerDn).Should()
                .BeEquivalentTo(new[] { "ou=groups,dc=example,dc=com", "ou=people,dc=example,dc=com" });
        }

        [Fact]
        public void Build_WithAnUnparseableBaseDn_IsAStartupFailure()
        {
            // A directory whose DNs cannot be rendered must not start serving: every entry it
            // published would be misnamed, and misnamed entries are worse than absent ones.
            var model = LdapModelBuilder.Create(baseDn: "not a dn").WithPeople().Build();

            var act = () => LdapDirectoryIndex.Build(model);

            act.Should().Throw<LdapConfigurationException>();
        }

        [Fact]
        public void EntryDn_EscapesTheNamingValue()
        {
            var target = Index().Targets.Single(t => t.Table.DbName == "users");

            target.EntryDn("Doe, John").Should().Be("uid=Doe\\, John,ou=people,dc=example,dc=com");
        }

        // ---- RootDSE / subschema ----

        [Fact]
        public void Resolve_EmptyBaseAtBaseScope_IsTheRootDse()
        {
            Index().Resolve("", LdapSearchScope.BaseObject).Kind.Should().Be(LdapScopeKind.RootDse);
        }

        [Theory]
        [InlineData(LdapSearchScope.SingleLevel)]
        [InlineData(LdapSearchScope.WholeSubtree)]
        public void Resolve_EmptyBaseAtAnyOtherScope_ResolvesToNothing(int scope)
        {
            // A subtree search from the empty DN is a request to walk the whole server. This
            // directory serves the RootDSE as a single readable entry, not as a tree root.
            Index().Resolve("", scope).Kind.Should().Be(LdapScopeKind.NoSuchObject);
        }

        [Fact]
        public void Resolve_SubschemaAtBaseScope_IsTheSubschema()
        {
            Index().Resolve("cn=subschema", LdapSearchScope.BaseObject).Kind
                .Should().Be(LdapScopeKind.Subschema);
        }

        [Fact]
        public void Resolve_SubschemaIsCaseInsensitive()
        {
            Index().Resolve("CN=SubSchema", LdapSearchScope.BaseObject).Kind
                .Should().Be(LdapScopeKind.Subschema);
        }

        // ---- base scope ----

        [Fact]
        public void Resolve_EntryDnAtBaseScope_IsASingleEntryWithItsNamingValue()
        {
            var resolution = Index().Resolve("uid=alice,ou=people,dc=example,dc=com", LdapSearchScope.BaseObject);

            resolution.Kind.Should().Be(LdapScopeKind.SingleEntry);
            resolution.Targets.Should().ContainSingle().Which.Table.DbName.Should().Be("users");
            resolution.NamingValue.Should().Be("alice");
        }

        [Fact]
        public void Resolve_EntryDnWithAnEscapedNamingValue_RecoversTheRawValue()
        {
            // The naming value goes on to become a parameter in a WHERE clause. Recovering the
            // ESCAPED text instead would look up a row whose value literally contains a backslash.
            var resolution = Index().Resolve(
                "cn=Doe\\, John,ou=groups,dc=example,dc=com", LdapSearchScope.BaseObject);

            resolution.Kind.Should().Be(LdapScopeKind.SingleEntry);
            resolution.NamingValue.Should().Be("Doe, John");
        }

        [Fact]
        public void Resolve_EntryDnWithTheWrongNamingAttribute_ResolvesToNothing()
        {
            // 'cn' names a group, not a person. Accepting any RDN attribute over a known container
            // would let a client address a table's rows through an attribute it never mapped.
            Index().Resolve("cn=alice,ou=people,dc=example,dc=com", LdapSearchScope.BaseObject)
                .Kind.Should().Be(LdapScopeKind.NoSuchObject);
        }

        [Theory]
        [InlineData("uid=alice,ou=nowhere,dc=example,dc=com")]    // container no mapping declares
        [InlineData("uid=alice,ou=people,dc=other,dc=com")]       // outside the base DN
        [InlineData("uid=alice,ou=people,dc=notexample,dc=com")]  // text-suffix lookalike
        [InlineData("uid=dangling\\")]                            // does not parse
        [InlineData("ou=people,dc=example,dc=com")]               // a container, not an entry
        public void Resolve_EverythingUnaddressableAtBaseScope_GivesTheSameAnswer(string baseObject)
        {
            // One answer for every way of naming nothing: a client cannot tell a malformed DN from
            // a foreign subtree from an undeclared container. Only 'not here'.
            Index().Resolve(baseObject, LdapSearchScope.BaseObject)
                .Should().BeEquivalentTo(LdapScopeResolution.None);
        }

        // ---- one level ----

        [Fact]
        public void Resolve_OneLevelUnderAContainer_IsThatContainersEntries()
        {
            var resolution = Index().Resolve("ou=people,dc=example,dc=com", LdapSearchScope.SingleLevel);

            resolution.Kind.Should().Be(LdapScopeKind.EntrySet);
            resolution.Targets.Should().ContainSingle().Which.Table.DbName.Should().Be("users");
        }

        [Fact]
        public void Resolve_OneLevelUnderTheBaseDn_ResolvesToNothing()
        {
            // The containers themselves are not published as entries, so one level below the root
            // holds nothing this directory can return. A subtree search is the way to reach entries.
            Index().Resolve("dc=example,dc=com", LdapSearchScope.SingleLevel)
                .Kind.Should().Be(LdapScopeKind.NoSuchObject);
        }

        // ---- subtree ----

        [Fact]
        public void Resolve_SubtreeFromTheBaseDn_IsEveryEntryFamily()
        {
            var resolution = Index().Resolve("dc=example,dc=com", LdapSearchScope.WholeSubtree);

            resolution.Kind.Should().Be(LdapScopeKind.EntrySet);
            resolution.Targets.Select(t => t.Table.DbName).Should().BeEquivalentTo("users", "groups");
        }

        [Fact]
        public void Resolve_SubtreeFromAContainer_IsOnlyThatContainer()
        {
            var resolution = Index().Resolve("ou=groups,dc=example,dc=com", LdapSearchScope.WholeSubtree);

            resolution.Targets.Should().ContainSingle().Which.Table.DbName.Should().Be("groups");
        }

        [Fact]
        public void Resolve_SubtreeFromAnEntryDn_IsThatEntryAlone()
        {
            // RFC 4511: a whole-subtree search includes its base. An entry has no children here, so
            // the subtree of an entry is the entry.
            var resolution = Index().Resolve("uid=alice,ou=people,dc=example,dc=com", LdapSearchScope.WholeSubtree);

            resolution.Kind.Should().Be(LdapScopeKind.SingleEntry);
            resolution.NamingValue.Should().Be("alice");
        }

        [Fact]
        public void Resolve_SubtreeFromAForeignSuffixLookalike_ResolvesToNothing()
        {
            // 'dc=notexample,dc=com' shares a text suffix with the base DN but is a different
            // subtree. A suffix comparison would hand a client every entry in the directory.
            Index().Resolve("dc=notexample,dc=com", LdapSearchScope.WholeSubtree)
                .Kind.Should().Be(LdapScopeKind.NoSuchObject);
        }

        [Fact]
        public void Resolve_AnUnknownScopeValue_ResolvesToNothing()
        {
            Index().Resolve("dc=example,dc=com", scope: 99).Kind.Should().Be(LdapScopeKind.NoSuchObject);
        }

        // ---- credential column egress sweep ----

        [Fact]
        public void Target_AttributesNeverIncludeTheCredentialColumn()
        {
            // One of the egress paths the slice-1 lesson requires sweeping. The parser refuses to
            // build a mapping that exposes the credential column at all, so this holds by
            // construction — pinned here so a future change to the target projection cannot
            // quietly reintroduce it.
            var users = Index().Targets.Single(t => t.Table.DbName == "users");

            users.Config.CredentialColumn.Should().Be("password_hash");
            users.Attributes.Select(a => a.Column).Should().NotContain("password_hash");
            users.NamingColumn.Should().NotBe("password_hash");
        }
    }
}

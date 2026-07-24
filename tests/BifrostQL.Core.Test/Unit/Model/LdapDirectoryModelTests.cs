using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Coverage for the deterministic RootDSE/subschema directory model (LDAP slice 1,
/// criterion 3). The model is a pure projection of the configured VISIBLE mappings — no wire,
/// no listener, no ambient time or randomness — so building it twice from the same model yields
/// an equal result, its subschema is sorted and distinct, hidden tables are excluded, and the
/// credential column never surfaces as an attribute.
/// </summary>
public class LdapDirectoryModelTests
{
    private static DbModelTestFixture DirectoryFixture() =>
        DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithColumn("full_name", "nvarchar")
                .WithColumn("uid_number", "int")
                .WithColumn("password_hash", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username,cn=full_name,uidNumber=uid_number")
                .WithMetadata(MetadataKeys.Ldap.Credential, "password_hash"))
            .WithTable("groups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("name", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "groupOfNames")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "cn={name},ou=groups")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "cn=name")
                .WithMetadata(MetadataKeys.Ldap.Member, "members"))
            .WithTable("user_groups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("GroupId", "int")
                .WithColumn("UserId", "int"))
            .WithManyToManyLink(
                "groups", "Id",
                "user_groups", "GroupId", "UserId",
                "users", "Id",
                "members");

    [Fact]
    public void FromModel_NoLdapConfigured_ReturnsNull()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t.WithSchema("dbo").WithPrimaryKey("Id").WithColumn("username", "nvarchar"))
            .Build();

        LdapDirectoryModel.FromModel(model).Should().BeNull();
    }

    [Fact]
    public void FromModel_IsDeterministic()
    {
        var model = DirectoryFixture().Build();

        var first = LdapDirectoryModel.FromModel(model);
        var second = LdapDirectoryModel.FromModel(model);

        first.Should().NotBeNull();
        first.Should().BeEquivalentTo(second, options => options.WithStrictOrdering());
    }

    [Fact]
    public void FromModel_RootDse_PublishesBaseDnAndVersion()
    {
        var model = DirectoryFixture().Build();

        var directory = LdapDirectoryModel.FromModel(model)!;

        directory.BaseDn.Should().Be("dc=example,dc=com");
        directory.RootDse.NamingContexts.Should().Equal("dc=example,dc=com");
        directory.RootDse.SupportedLdapVersion.Should().Equal("3");
        directory.RootDse.SubschemaSubentry.Should().Be(LdapDirectoryModel.SubschemaSubentryDn);
        directory.RootDse.VendorName.Should().Be(LdapDirectoryModel.VendorName);
    }

    [Fact]
    public void FromModel_Subschema_HasSortedDistinctObjectClassesAndAttributes()
    {
        var model = DirectoryFixture().Build();

        var directory = LdapDirectoryModel.FromModel(model)!;

        directory.Subschema.ObjectClasses.Should().Equal("groupOfNames", "inetOrgPerson");

        // Attribute types are the distinct returned attributes across visible mappings plus the
        // synthesized 'member' attribute — sorted, each with its derived syntax.
        directory.Subschema.AttributeTypes.Select(a => a.Name)
            .Should().Equal("cn", "member", "uid", "uidNumber");
        directory.Subschema.AttributeTypes.Single(a => a.Name == "uidNumber").Syntax
            .Should().Be(LdapSyntax.Integer);
        directory.Subschema.AttributeTypes.Single(a => a.Name == "cn").Syntax
            .Should().Be(LdapSyntax.DirectoryString);
    }

    [Fact]
    public void FromModel_DoesNotExposeCredentialColumn()
    {
        var model = DirectoryFixture().Build();

        var directory = LdapDirectoryModel.FromModel(model)!;

        var users = directory.Entries.Single(e => e.TableDbName == "users");
        users.Attributes.Should().NotContain(a => a.Column == "password_hash");
    }

    [Fact]
    public void FromModel_ExcludesHiddenTable()
    {
        var model = DirectoryFixture()
            .WithTable("groups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("name", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "groupOfNames")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "cn={name},ou=groups")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "cn=name")
                .WithMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
            .Build();

        var directory = LdapDirectoryModel.FromModel(model)!;

        directory.Entries.Should().ContainSingle(e => e.TableDbName == "users");
        directory.Entries.Should().NotContain(e => e.TableDbName == "groups");
        directory.Subschema.ObjectClasses.Should().NotContain("groupOfNames");
    }
}

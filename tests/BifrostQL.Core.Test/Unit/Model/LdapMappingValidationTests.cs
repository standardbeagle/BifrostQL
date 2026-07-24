using System;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Fail-fast validation coverage for the LDAP directory mapping contract (slice 1). A
/// misconfiguration must be caught at model load, before a listener is ever opened: a mapped
/// table with no base DN, an invalid or non-unique DN template, a naming attribute that is not
/// returned, an unknown attribute/naming/credential column, an attribute mapped onto a column
/// whose type is incompatible with its LDAP syntax, a group-membership relationship that is
/// unknown / to-one / composite / points at a non-mapped table, and — the security crux — a
/// credential (password-hash) column exposed as a returned attribute. Nothing is published
/// absent the opt-in.
/// </summary>
public class LdapMappingValidationTests
{
    private static DbModelTestFixture ValidUsersDirectory() =>
        DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithColumn("full_name", "nvarchar")
                .WithColumn("email", "nvarchar")
                .WithColumn("password_hash", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username,cn=full_name,mail=email")
                .WithMetadata(MetadataKeys.Ldap.Credential, "password_hash"));

    [Fact]
    public void Validate_ValidDirectoryMapping_DoesNotThrow()
    {
        var model = ValidUsersDirectory().Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NoLdapConfigured_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ValidGroupMembershipManyToMany_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username"))
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
                "members")
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MappedTableWithoutBaseDn_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Ldap.BaseDn).And.Contain("no base DN");
    }

    [Fact]
    public void Validate_InvalidDnTemplate_Throws()
    {
        var model = ValidUsersDirectory()
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "ou=people") // no RDN placeholder
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Ldap.DnTemplate);
    }

    [Fact]
    public void Validate_DuplicateDnTemplate_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username"))
            .WithTable("admins", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people") // same namespace
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("same DN namespace");
    }

    [Fact]
    public void Validate_NamingAttributeNotReturned_Throws()
    {
        // The DN template names entries by 'uid', but 'uid' is not in ldap-attributes, so a
        // search could never surface the value the entry is named by.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithColumn("full_name", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "cn=full_name")) // uid missing
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("naming attribute").And.Contain("uid");
    }

    [Fact]
    public void Validate_UnknownAttributeColumn_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username,mail=ghost")) // no such column
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Ldap.Attributes).And.Contain("does not exist");
    }

    [Fact]
    public void Validate_AttributeSyntaxIncompatible_Throws()
    {
        // 'mail' is a DirectoryString attribute; mapping it onto an integer column emits values
        // its syntax cannot represent.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithColumn("code", "int")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username,mail=code"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("incompatible with the attribute's syntax");
    }

    [Fact]
    public void Validate_CredentialColumnExposedAsAttribute_Throws()
    {
        // SECURITY: the credential (password-hash) column verifies binds only and must never be
        // a searchable/returned attribute. Here it is mapped under the 'description' attribute —
        // exposing the hash. Removing the credential-exposure guard makes this test go green
        // while the password column leaks (revert-proof of a non-vacuous regression test).
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithColumn("password_hash", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username,description=password_hash")
                .WithMetadata(MetadataKeys.Ldap.Credential, "password_hash"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("password_hash")
            .And.Contain("never be a searchable or returned attribute");
    }

    [Fact]
    public void Validate_UnknownMembershipRelationship_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("groups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("name", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "groupOfNames")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "cn={name},ou=groups")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "cn=name")
                .WithMetadata(MetadataKeys.Ldap.Member, "ghosts")) // no such relationship
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Ldap.Member).And.Contain("does not name a known relationship");
    }

    [Fact]
    public void Validate_CompositeMembershipRelationship_Throws()
    {
        // A composite-key relationship is explicitly unsupported in this slice — rejected by
        // name rather than silently taking the first key column (composite-pk-compliance spirit).
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("groups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Tenant", "int", isPrimaryKey: true)
                .WithColumn("name", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "groupOfNames")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "cn={name},ou=groups")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "cn=name")
                .WithMetadata(MetadataKeys.Ldap.Member, "enrollments"))
            .WithTable("enrollments", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("GroupId", "int")
                .WithColumn("GroupTenant", "int"))
            .WithForeignKey("fk_enr_grp",
                "dbo", "enrollments", new[] { "GroupId", "GroupTenant" },
                "dbo", "groups", new[] { "Id", "Tenant" })
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Ldap.Member).And.Contain("composite");
    }

    [Fact]
    public void Validate_MembershipTargetNotMapped_Throws()
    {
        // The membership target table is not LDAP-mapped, so a member DN cannot be constructed.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Ldap.BaseDn, "dc=example,dc=com")
            .WithTable("groups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("name", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "groupOfNames")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "cn={name},ou=groups")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "cn=name")
                .WithMetadata(MetadataKeys.Ldap.Member, "members"))
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")) // NOT ldap-mapped
            .WithTable("user_groups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("GroupId", "int")
                .WithColumn("UserId", "int"))
            .WithManyToManyLink(
                "groups", "Id",
                "user_groups", "GroupId", "UserId",
                "users", "Id",
                "members")
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("not LDAP-mapped");
    }

    [Fact]
    public void Validate_StrayLdapKeyWithoutObjectClass_Throws()
    {
        // ldap-dn-template without ldap-object-class: the author believes the table is a
        // directory entry and it publishes nothing.
        var model = DbModelTestFixture.Create()
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Ldap.ObjectClass).And.Contain("no effect");
    }

    [Fact]
    public void Validate_UnknownLdapKey_Throws()
    {
        // A typo in the ldap-* family must fail the model-load unknown-key gate.
        var model = ValidUsersDirectory()
            .WithTable("users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("username", "nvarchar")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username")
                .WithMetadata("ldap-attribtes", "uid=username")) // typo
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("ldap-attribtes").And.Contain("unrecognized");
    }
}

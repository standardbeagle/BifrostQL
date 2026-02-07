using BifrostQL.Core.Model;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

#region DbAuthConfig Validation

public class DbAuthConfigValidationTests
{
    [Fact]
    public void Validate_SharedConnection_AlwaysValid()
    {
        var config = new DbAuthConfig { Mode = DbAuthMode.SharedConnection };
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_Impersonation_ValidWithClaimKey()
    {
        var config = new DbAuthConfig
        {
            Mode = DbAuthMode.Impersonation,
            ImpersonationClaimKey = "db_user",
        };
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_Impersonation_InvalidWithoutClaimKey()
    {
        var config = new DbAuthConfig { Mode = DbAuthMode.Impersonation };
        var errors = config.Validate();
        errors.Should().ContainSingle();
        errors[0].Should().Contain("ImpersonationClaimKey");
    }

    [Fact]
    public void Validate_Impersonation_InvalidWithEmptyClaimKey()
    {
        var config = new DbAuthConfig
        {
            Mode = DbAuthMode.Impersonation,
            ImpersonationClaimKey = "  ",
        };
        var errors = config.Validate();
        errors.Should().ContainSingle();
    }

    [Fact]
    public void Validate_SessionContext_ValidWithMappings()
    {
        var config = new DbAuthConfig
        {
            Mode = DbAuthMode.SessionContext,
            ClaimMappings = new Dictionary<string, string>
            {
                { "tenant_id", "tenant_claim" },
            },
        };
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_SessionContext_InvalidWithoutMappings()
    {
        var config = new DbAuthConfig { Mode = DbAuthMode.SessionContext };
        var errors = config.Validate();
        errors.Should().ContainSingle();
        errors[0].Should().Contain("ClaimMappings");
    }

    [Fact]
    public void Validate_PerUser_ValidWithTemplateAndMappings()
    {
        var config = new DbAuthConfig
        {
            Mode = DbAuthMode.PerUser,
            ConnectionStringTemplate = "Server=localhost;Database=db;User Id={username};Password={password}",
            ClaimMappings = new Dictionary<string, string>
            {
                { "user", "username" },
                { "pass", "password" },
            },
        };
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_PerUser_InvalidWithoutTemplate()
    {
        var config = new DbAuthConfig
        {
            Mode = DbAuthMode.PerUser,
            ClaimMappings = new Dictionary<string, string>
            {
                { "user", "username" },
            },
        };
        var errors = config.Validate();
        errors.Should().Contain(e => e.Contains("ConnectionStringTemplate"));
    }

    [Fact]
    public void Validate_PerUser_InvalidWithoutMappings()
    {
        var config = new DbAuthConfig
        {
            Mode = DbAuthMode.PerUser,
            ConnectionStringTemplate = "Server=localhost;Database=db;User Id={username}",
        };
        var errors = config.Validate();
        errors.Should().Contain(e => e.Contains("ClaimMappings"));
    }

    [Fact]
    public void Validate_PerUser_InvalidWithoutBoth()
    {
        var config = new DbAuthConfig { Mode = DbAuthMode.PerUser };
        var errors = config.Validate();
        errors.Should().HaveCount(2);
    }

    [Fact]
    public void Defaults_AreSharedConnection()
    {
        var config = new DbAuthConfig();
        config.Mode.Should().Be(DbAuthMode.SharedConnection);
        config.ClaimMappings.Should().BeEmpty();
        config.ImpersonationClaimKey.Should().BeNull();
        config.ConnectionStringTemplate.Should().BeNull();
    }
}

#endregion

#region DbAuthConnectionWrapper - Per-User Connection String

public class DbAuthConnectionWrapperPerUserTests
{
    [Fact]
    public void BuildPerUserConnectionString_ReplacesPlaceholders()
    {
        var template = "Server=localhost;Database=db;User Id={username};Password={password}";
        var mappings = new Dictionary<string, string>
        {
            { "user", "username" },
            { "pass", "password" },
        };
        var userContext = new Dictionary<string, string>
        {
            { "username", "alice" },
            { "password", "secret123" },
        };

        var result = DbAuthConnectionWrapper.BuildPerUserConnectionString(template, mappings, userContext);

        result.Should().Be("Server=localhost;Database=db;User Id=alice;Password=secret123");
    }

    [Fact]
    public void BuildPerUserConnectionString_ThrowsOnMissingClaim()
    {
        var template = "Server=localhost;User Id={username}";
        var mappings = new Dictionary<string, string>
        {
            { "user", "username" },
        };
        var userContext = new Dictionary<string, string>();

        var act = () => DbAuthConnectionWrapper.BuildPerUserConnectionString(template, mappings, userContext);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*username*not found*");
    }

    [Fact]
    public void BuildPerUserConnectionString_ThrowsOnUnresolvedPlaceholder()
    {
        var template = "Server=localhost;User Id={username};Database={dbname}";
        var mappings = new Dictionary<string, string>
        {
            { "user", "username" },
        };
        var userContext = new Dictionary<string, string>
        {
            { "username", "alice" },
        };

        var act = () => DbAuthConnectionWrapper.BuildPerUserConnectionString(template, mappings, userContext);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unresolved placeholder*{dbname}*");
    }

    [Fact]
    public void BuildPerUserConnectionString_ThrowsOnEmptyTemplate()
    {
        var mappings = new Dictionary<string, string>();
        var userContext = new Dictionary<string, string>();

        var act = () => DbAuthConnectionWrapper.BuildPerUserConnectionString("", mappings, userContext);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*template*");
    }

    [Fact]
    public void BuildPerUserConnectionString_IgnoresMappingsWithNoPlaceholder()
    {
        var template = "Server=localhost;Database=mydb";
        var mappings = new Dictionary<string, string>
        {
            { "extra", "no_placeholder" },
        };
        var userContext = new Dictionary<string, string>
        {
            { "no_placeholder", "value" },
        };

        var result = DbAuthConnectionWrapper.BuildPerUserConnectionString(template, mappings, userContext);

        result.Should().Be("Server=localhost;Database=mydb");
    }
}

#endregion

#region DbAuthConnectionWrapper - Connection String Injection Prevention

public class DbAuthConnectionWrapperSecurityTests
{
    [Theory]
    [InlineData("alice;Server=evil")]
    [InlineData("alice=admin")]
    [InlineData("alice'")]
    public void ValidateConnectionStringValue_RejectsDangerousCharacters(string value)
    {
        var act = () => DbAuthConnectionWrapper.ValidateConnectionStringValue("test_claim", value);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not permitted*");
    }

    [Fact]
    public void ValidateConnectionStringValue_RejectsEmpty()
    {
        var act = () => DbAuthConnectionWrapper.ValidateConnectionStringValue("test_claim", "");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void ValidateConnectionStringValue_AcceptsSafeValues()
    {
        var act = () => DbAuthConnectionWrapper.ValidateConnectionStringValue("test_claim", "alice_bob-123");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateConnectionStringValue_AcceptsEmailFormat()
    {
        var act = () => DbAuthConnectionWrapper.ValidateConnectionStringValue("email", "user@example.com");

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildPerUserConnectionString_RejectsSemicolonInClaimValue()
    {
        var template = "Server=localhost;User Id={username}";
        var mappings = new Dictionary<string, string>
        {
            { "user", "username" },
        };
        var userContext = new Dictionary<string, string>
        {
            { "username", "alice;Server=evil" },
        };

        var act = () => DbAuthConnectionWrapper.BuildPerUserConnectionString(template, mappings, userContext);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not permitted*");
    }
}

#endregion

#region DbAuthConnectionWrapper - Impersonation SQL

public class DbAuthConnectionWrapperImpersonationTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnection()
    {
        var config = new DbAuthConfig();
        var context = new Dictionary<string, string>();

        var act = () => new DbAuthConnectionWrapper(null!, config, context);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connection");
    }

    [Fact]
    public void Constructor_ThrowsOnNullConfig()
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection();
        var context = new Dictionary<string, string>();

        var act = () => new DbAuthConnectionWrapper(conn, null!, context);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("config");
    }

    [Fact]
    public void Constructor_ThrowsOnNullUserContext()
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection();
        var config = new DbAuthConfig();

        var act = () => new DbAuthConnectionWrapper(conn, config, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("userContext");
    }

    [Fact]
    public void Connection_ExposesUnderlyingConnection()
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection();
        var config = new DbAuthConfig();
        var context = new Dictionary<string, string>();

        using var wrapper = new DbAuthConnectionWrapper(conn, config, context);

        wrapper.Connection.Should().BeSameAs(conn);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection();
        var config = new DbAuthConfig();
        var context = new Dictionary<string, string>();

        var wrapper = new DbAuthConnectionWrapper(conn, config, context);
        var act = () =>
        {
            wrapper.Dispose();
            wrapper.Dispose();
        };

        act.Should().NotThrow();
    }
}

#endregion

#region DbAuthMode Enum

public class DbAuthModeTests
{
    [Fact]
    public void AllModes_AreDefined()
    {
        Enum.GetValues<DbAuthMode>().Should().HaveCount(4);
    }

    [Fact]
    public void SharedConnection_IsDefault()
    {
        default(DbAuthMode).Should().Be(DbAuthMode.SharedConnection);
    }
}

#endregion

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// <c>BifrostQL:Metadata</c> must be an array of rule strings. A scalar has no children and an
/// object entry has no value, so a reader that keeps only valued children drops both: a single
/// tenant-filter rule written as a string meant no tenant scoping and no error. The Host
/// registration refuses both shapes at startup.
/// </summary>
public sealed class MetadataConfigShapeTests
{
    private const string TenantRule = "dbo.orders { tenant-filter: tenant_id }";

    [Fact]
    public void ScalarMetadata_IsRefusedAtStartup()
    {
        var act = () => Register($$"""{ "BifrostQL": { "DisableAuth": true, "Metadata": "{{TenantRule}}" } }""");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BifrostQL:Metadata*array of rule strings*")
            .Which.Message.Should().NotContain("tenant_id");
    }

    [Fact]
    public void ObjectEntryInMetadata_IsRefusedAtStartup()
    {
        var act = () => Register($$"""{ "BifrostQL": { "DisableAuth": true, "Metadata": [ "{{TenantRule}}", { "rule": "dbo.items { tenant-filter: tenant_id }" } ] } }""");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BifrostQL:Metadata:1*rule string*")
            .Which.Message.Should().NotContain("tenant_id");
    }

    [Fact]
    public void EmptyObjectEntryInMetadata_IsRefusedAtStartup()
    {
        var act = () => Register($$"""{ "BifrostQL": { "DisableAuth": true, "Metadata": [ "{{TenantRule}}", {} ] } }""");

        act.Should().Throw<InvalidOperationException>().WithMessage("*BifrostQL:Metadata:1*rule string*");
    }

    [Fact]
    public void ArrayOfRuleStrings_Registers()
    {
        var act = () => Register($$"""{ "BifrostQL": { "DisableAuth": true, "Metadata": [ "{{TenantRule}}" ] } }""");

        act.Should().NotThrow();
    }

    private static void Register(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBifrostQL(o => o
            .BindConfiguration(config.GetSection("BifrostQL"))
            .BindConnectionString("Data Source=:memory:")
            .BindProvider("sqlite"));
    }
}

using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Host.Test;

/// <summary>
/// The shipped Host config stamps created-by/updated-by/deleted-by from the claim its
/// <c>user-audit-key</c> names. That claim must be one the identity mapper actually writes for an
/// HTTP caller, or every actor column is stamped NULL (and a NOT NULL actor column fails the write).
/// </summary>
public class ShippedActorKeyTests
{
    [Theory]
    [InlineData("Metadata")]
    [InlineData("Metadata-mba")]
    public void UserAuditKey_NamesTheClaimTheIdentityMapperWrites(string section)
    {
        var rules = ReadMetadata(section);
        rules.Should().Contain(r => r.Contains("populate: created-by"), "the config must declare an actor column");
        var root = new Dictionary<string, object?>();
        new MetadataLoader(rules).ApplyDatabaseMetadata(root);

        var auditKey = root[MetadataKeys.Audit.UserKey] as string;
        var context = new IdentityContextMapper().ToUserContext(new AppIdentity("u-42", "local"));

        context.Should().ContainKey(auditKey!).WhoseValue.Should().Be("u-42");
    }

    private static IReadOnlyCollection<string> ReadMetadata(string section)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BifrostQL.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "Could not locate repo root (BifrostQL.sln) above the test assembly.");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "BifrostQL.Host", "appsettings.json")));
        return doc.RootElement.GetProperty("BifrostQL").GetProperty(section).EnumerateArray()
            .Select(e => e.GetString()!).ToList();
    }
}

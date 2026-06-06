using BifrostQL.Core.AppMetadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for the app-metadata overlay source + loader
/// integration (<see cref="IAppMetadataSource"/>, <see cref="FileAppMetadataSource"/>,
/// <see cref="CompositeAppMetadataSource"/>, <see cref="AppMetadataLoader"/>, and
/// <see cref="AppMetadataServiceCollectionExtensions"/>).
///
/// The overlay is a separate, coexisting pipeline: it loads independently of
/// the schema-metadata system, is keyed by qualified table name so it aligns
/// with <c>DbModel</c> tables without modifying them, and its DI extension does
/// not interfere with <c>AddBifrostQL</c>.
/// </summary>
public class AppMetadataLoaderTests
{
    /// <summary>
    /// An in-memory <see cref="IAppMetadataSource"/> for tests — exercises the
    /// loader and composite without touching disk or a database.
    /// </summary>
    private sealed class InMemoryAppMetadataSource : IAppMetadataSource
    {
        private readonly IDictionary<string, EntityMetadata> _entities;

        public int Priority { get; }

        public InMemoryAppMetadataSource(
            int priority, IDictionary<string, EntityMetadata> entities)
        {
            Priority = priority;
            _entities = entities;
        }

        public Task<IDictionary<string, EntityMetadata>> LoadEntityMetadataAsync()
            => Task.FromResult(_entities);
    }

    [Fact]
    public async Task Loader_BuildsAggregate_FromSourceEntries()
    {
        var source = new InMemoryAppMetadataSource(0, new Dictionary<string, EntityMetadata>
        {
            ["dbo.users"] = new EntityMetadata { Label = "Users" },
            ["sales.orders"] = new EntityMetadata { Label = "Orders" },
        });
        var loader = new AppMetadataLoader(source);

        var model = await loader.LoadAsync();

        model.Entities.Should().HaveCount(2);
        model.Entities["dbo.users"].Label.Should().Be("Users");
        model.Entities["sales.orders"].Label.Should().Be("Orders");
    }

    [Fact]
    public async Task Loader_KeyedByQualifiedTableName_IsCaseInsensitive()
    {
        // Overlay aligns with DbModel tables by qualified name; lookups must
        // not be defeated by casing differences.
        var source = new InMemoryAppMetadataSource(0, new Dictionary<string, EntityMetadata>
        {
            ["dbo.Users"] = new EntityMetadata { Label = "Users" },
        });
        var loader = new AppMetadataLoader(source);

        var model = await loader.LoadAsync();

        model.Entities.ContainsKey("DBO.USERS").Should().BeTrue();
    }

    [Fact]
    public async Task Loader_EmptySource_ProducesEmptyAggregate()
    {
        var source = new InMemoryAppMetadataSource(
            0, new Dictionary<string, EntityMetadata>());
        var loader = new AppMetadataLoader(source);

        var model = await loader.LoadAsync();

        model.Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Composite_HigherPrioritySource_OverridesEntry()
    {
        var low = new InMemoryAppMetadataSource(0, new Dictionary<string, EntityMetadata>
        {
            ["dbo.users"] = new EntityMetadata { Label = "From File" },
        });
        var high = new InMemoryAppMetadataSource(100, new Dictionary<string, EntityMetadata>
        {
            ["dbo.users"] = new EntityMetadata { Label = "From Database" },
        });
        var composite = new CompositeAppMetadataSource(new IAppMetadataSource[] { high, low });

        var entities = await composite.LoadEntityMetadataAsync();

        entities["dbo.users"].Label.Should().Be("From Database");
    }

    [Fact]
    public async Task Composite_MergesDistinctEntries_FromAllSources()
    {
        var fileSource = new InMemoryAppMetadataSource(0, new Dictionary<string, EntityMetadata>
        {
            ["dbo.users"] = new EntityMetadata { Label = "Users" },
        });
        var dbSource = new InMemoryAppMetadataSource(100, new Dictionary<string, EntityMetadata>
        {
            ["sales.orders"] = new EntityMetadata { Label = "Orders" },
        });
        var composite = new CompositeAppMetadataSource(
            new IAppMetadataSource[] { fileSource, dbSource });

        var entities = await composite.LoadEntityMetadataAsync();

        entities.Should().HaveCount(2);
        entities.Should().ContainKey("dbo.users");
        entities.Should().ContainKey("sales.orders");
    }

    [Fact]
    public async Task FileSource_MissingFile_YieldsEmptyOverlay()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(), $"bifrost-app-metadata-missing-{Guid.NewGuid():N}.json");
        var source = new FileAppMetadataSource(missingPath);

        var entities = await source.LoadEntityMetadataAsync();

        entities.Should().BeEmpty();
    }

    [Fact]
    public async Task FileSource_ReadsOverlayJson_FromDisk()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"bifrost-app-metadata-{Guid.NewGuid():N}.json");
        var written = new AppMetadataModel
        {
            Entities = new Dictionary<string, EntityMetadata>
            {
                ["dbo.users"] = new EntityMetadata { Label = "Users", Icon = "person" },
            },
        };
        await File.WriteAllTextAsync(path, AppMetadataJson.Serialize(written));

        try
        {
            var source = new FileAppMetadataSource(path);

            var entities = await source.LoadEntityMetadataAsync();

            entities.Should().ContainKey("dbo.users");
            entities["dbo.users"].Label.Should().Be("Users");
            entities["dbo.users"].Icon.Should().Be("person");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddBifrostAppMetadata_RegistersOverlayModel_AsSingleton()
    {
        var source = new InMemoryAppMetadataSource(0, new Dictionary<string, EntityMetadata>
        {
            ["dbo.users"] = new EntityMetadata { Label = "Users" },
        });
        var services = new ServiceCollection();

        services.AddBifrostAppMetadata(source);
        using var provider = services.BuildServiceProvider();

        var model = await provider.GetRequiredService<Lazy<Task<AppMetadataModel>>>().Value;
        model.Entities.Should().ContainKey("dbo.users");
        // Singleton: resolving again returns the same memoized instance.
        (await provider.GetRequiredService<Lazy<Task<AppMetadataModel>>>().Value).Should().BeSameAs(model);
    }

    [Fact]
    public void AddBifrostAppMetadata_DoesNotInterfere_WithUnrelatedRegistrations()
    {
        // The overlay extension must be additive: it registers only its own
        // services and leaves any pre-existing registration (a stand-in for
        // AddBifrostQL's services) untouched.
        var sentinel = new object();
        var services = new ServiceCollection();
        services.AddSingleton(sentinel);

        services.AddBifrostAppMetadata(new InMemoryAppMetadataSource(
            0, new Dictionary<string, EntityMetadata>()));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<object>().Should().BeSameAs(sentinel);
        provider.GetService<Lazy<Task<AppMetadataModel>>>().Should().NotBeNull();
        provider.GetService<AppMetadataLoader>().Should().NotBeNull();
    }
}

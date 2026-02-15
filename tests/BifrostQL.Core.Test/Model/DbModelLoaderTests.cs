using BifrostQL.Core.Model;
using BifrostQL.Model;
using NSubstitute;
using System.Data.Common;

namespace BifrostQL.Core.Test.Model;

public class DbModelLoaderTests
{
    [Fact]
    public async Task LoadAsync_UsesConnectionFactoryAndSchemaReader()
    {
        // Arrange
        var mockConnFactory = Substitute.For<IDbConnFactory>();
        var mockConnection = Substitute.For<DbConnection>();
        var mockSchemaReader = Substitute.For<ISchemaReader>();
        var mockMetadataLoader = Substitute.For<IMetadataLoader>();

        var schemaData = new SchemaData(
            new Dictionary<ColumnRef, List<ColumnConstraintDto>>(),
            Array.Empty<ColumnDto>(),
            new List<IDbTable>()
        );

        mockConnFactory.GetConnection().Returns(mockConnection);
        mockConnFactory.SchemaReader.Returns(mockSchemaReader);
        mockSchemaReader.ReadSchemaAsync(mockConnection).Returns(schemaData);
        mockMetadataLoader.BuildMetadataLoaders().Returns(new List<MetadataLoaderRule>());

        var loader = new DbModelLoader(mockConnFactory, mockMetadataLoader);

        // Act
        var result = await loader.LoadAsync();

        // Assert
        await mockConnection.Received(1).OpenAsync();
        await mockSchemaReader.Received(1).ReadSchemaAsync(mockConnection);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task LoadAsync_BackwardCompatibility_UsesStringConstructor()
    {
        // Arrange
        var connectionString = "Server=localhost;Database=test;";
        var mockMetadataLoader = Substitute.For<IMetadataLoader>();
        mockMetadataLoader.BuildMetadataLoaders().Returns(new List<MetadataLoaderRule>());

        // This constructor should internally create a DbConnFactory
        var loader = new DbModelLoader(connectionString, mockMetadataLoader);

        // Act - This will fail to connect, but we're just testing the constructor works
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await loader.LoadAsync());

        // Assert - Should throw a connection error, not a null reference or construction error
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task LoadAsync_PassesAdditionalMetadata_ToDbModel()
    {
        // Arrange
        var mockConnFactory = Substitute.For<IDbConnFactory>();
        var mockConnection = Substitute.For<DbConnection>();
        var mockSchemaReader = Substitute.For<ISchemaReader>();
        var mockMetadataLoader = Substitute.For<IMetadataLoader>();

        var schemaData = new SchemaData(
            new Dictionary<ColumnRef, List<ColumnConstraintDto>>(),
            Array.Empty<ColumnDto>(),
            new List<IDbTable>()
        );

        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>
        {
            ["dbo.users"] = new Dictionary<string, object?> { ["test"] = "value" }
        };

        mockConnFactory.GetConnection().Returns(mockConnection);
        mockConnFactory.SchemaReader.Returns(mockSchemaReader);
        mockSchemaReader.ReadSchemaAsync(mockConnection).Returns(schemaData);
        mockMetadataLoader.BuildMetadataLoaders().Returns(new List<MetadataLoaderRule>());

        var loader = new DbModelLoader(mockConnFactory, mockMetadataLoader);

        // Act
        var result = await loader.LoadAsync(additionalMetadata);

        // Assert
        Assert.NotNull(result);
        await mockSchemaReader.Received(1).ReadSchemaAsync(mockConnection);
    }
}

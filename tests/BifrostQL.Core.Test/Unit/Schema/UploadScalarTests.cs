using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL.Types;
using GraphQLParser.AST;
using Xunit;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// File storage metadata makes the SDL declare <c>_fileUpload(file: Upload!)</c>. Before the fix
/// no <c>Upload</c> type existed, so any storage metadata broke the WHOLE endpoint the first time
/// the schema initialized ("Unable to resolve reference to type 'Upload'").
/// </summary>
public sealed class UploadScalarTests
{
    [Fact]
    public void StorageMetadata_SchemaInitializes_WithAnUploadScalar()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Docs", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("file_data", "nvarchar")
                .WithColumnMetadata("file_data", MetadataKeys.Storage.Config, "provider:local;bucket:docs"))
            .Build();

        var schema = DbSchema.FromModel(model);
        var initialize = () => schema.Initialize();

        initialize.Should().NotThrow();
        schema.AllTypes["Upload"].Should().BeOfType<UploadGraphType>();
    }

    [Fact]
    public void Upload_AcceptsBase64AsBytes_AndRefusesAnythingElse()
    {
        var scalar = new UploadGraphType();
        var bytes = Encoding.UTF8.GetBytes("hello");

        scalar.ParseValue(Convert.ToBase64String(bytes)).Should().BeEquivalentTo(bytes);
        scalar.ParseValue(bytes).Should().BeSameAs(bytes);
        scalar.ParseLiteral(new GraphQLStringValue(Convert.ToBase64String(bytes))).Should().BeEquivalentTo(bytes);
        scalar.ParseValue(null).Should().BeNull();

        var notBase64 = () => scalar.ParseValue("not base64!");
        var number = () => scalar.ParseValue(42);
        notBase64.Should().Throw<FormatException>();
        number.Should().Throw<FormatException>();
    }
}

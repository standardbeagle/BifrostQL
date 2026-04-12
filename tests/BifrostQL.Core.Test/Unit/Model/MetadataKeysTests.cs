using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model
{
    public class MetadataKeysTests
    {
        [Theory]
        [InlineData(MetadataKeys.Eav.Parent, "eav-parent")]
        [InlineData(MetadataKeys.Eav.ForeignKey, "eav-fk")]
        [InlineData(MetadataKeys.Eav.Key, "eav-key")]
        [InlineData(MetadataKeys.Eav.Value, "eav-value")]
        public void EavKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Theory]
        [InlineData(MetadataKeys.FileStorage.Storage, "file-storage")]
        [InlineData(MetadataKeys.FileStorage.MaxSize, "max-size")]
        [InlineData(MetadataKeys.FileStorage.ContentTypeColumn, "content-type-column")]
        [InlineData(MetadataKeys.FileStorage.FileNameColumn, "file-name-column")]
        [InlineData(MetadataKeys.FileStorage.Accept, "accept")]
        public void FileStorageKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Theory]
        [InlineData(MetadataKeys.DataType.Type, "type")]
        [InlineData(MetadataKeys.DataType.Format, "format")]
        [InlineData(MetadataKeys.DataType.PhpSerialized, "php_serialized")]
        public void DataTypeKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Theory]
        [InlineData(MetadataKeys.Storage.Bucket, "bucket")]
        [InlineData(MetadataKeys.Storage.Provider, "provider")]
        [InlineData(MetadataKeys.Storage.Prefix, "prefix")]
        [InlineData(MetadataKeys.Storage.BasePath, "basePath")]
        public void StorageKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Theory]
        [InlineData(MetadataKeys.Ui.Label, "label")]
        [InlineData(MetadataKeys.Ui.Hidden, "hidden")]
        [InlineData(MetadataKeys.Ui.ReadOnly, "readonly")]
        public void UiKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Theory]
        [InlineData(MetadataKeys.Validation.Min, "min")]
        [InlineData(MetadataKeys.Validation.Max, "max")]
        [InlineData(MetadataKeys.Validation.Step, "step")]
        [InlineData(MetadataKeys.Validation.MinLength, "minlength")]
        [InlineData(MetadataKeys.Validation.MaxLength, "maxlength")]
        [InlineData(MetadataKeys.Validation.Pattern, "pattern")]
        [InlineData(MetadataKeys.Validation.PatternMessage, "pattern-message")]
        [InlineData(MetadataKeys.Validation.InputType, "input-type")]
        [InlineData(MetadataKeys.Validation.Required, "required")]
        public void ValidationKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Theory]
        [InlineData(MetadataKeys.Enum.Values, "enum-values")]
        [InlineData(MetadataKeys.Enum.Labels, "enum-labels")]
        public void EnumKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Theory]
        [InlineData(MetadataKeys.AutoPopulate.Timestamp, "timestamp")]
        [InlineData(MetadataKeys.AutoPopulate.User, "user")]
        [InlineData(MetadataKeys.AutoPopulate.Guid, "guid")]
        public void AutoPopulateKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Fact]
        public void AllKeys_AreNonEmpty()
        {
            // Verify all constants are properly defined
            MetadataKeys.Eav.Parent.Should().NotBeNullOrEmpty();
            MetadataKeys.FileStorage.Storage.Should().NotBeNullOrEmpty();
            MetadataKeys.DataType.Type.Should().NotBeNullOrEmpty();
            MetadataKeys.Storage.Bucket.Should().NotBeNullOrEmpty();
            MetadataKeys.Ui.Label.Should().NotBeNullOrEmpty();
            MetadataKeys.Validation.Min.Should().NotBeNullOrEmpty();
            MetadataKeys.Enum.Values.Should().NotBeNullOrEmpty();
            MetadataKeys.AutoPopulate.Timestamp.Should().NotBeNullOrEmpty();
        }
        
        [Fact]
        public void Keys_AreConsistentWithUsage()
        {
            // Verify keys match expected patterns used in the codebase
            MetadataKeys.Eav.Parent.Should().Contain("eav");
            MetadataKeys.FileStorage.Storage.Should().Contain("file");
            MetadataKeys.DataType.PhpSerialized.Should().Contain("php");
            MetadataKeys.Validation.PatternMessage.Should().Contain("pattern");
        }
    }
}

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
        [InlineData(MetadataKeys.Security.TenantFilter, "tenant-filter")]
        [InlineData(MetadataKeys.Security.TenantContextKey, "tenant-context-key")]
        [InlineData(MetadataKeys.Security.AutoFilter, "auto-filter")]
        [InlineData(MetadataKeys.Security.AutoFilterBypassRole, "auto-filter-bypass-role")]
        public void SecurityKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }

        [Theory]
        [InlineData(MetadataKeys.StateMachine.StateColumn, "state-column")]
        [InlineData(MetadataKeys.StateMachine.InitialState, "initial-state")]
        [InlineData(MetadataKeys.StateMachine.States, "states")]
        [InlineData(MetadataKeys.StateMachine.Transitions, "transitions")]
        public void StateMachineKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }

        [Theory]
        [InlineData(MetadataKeys.SoftDelete.Column, "soft-delete")]
        [InlineData(MetadataKeys.SoftDelete.DeletedBy, "soft-delete-by")]
        [InlineData(MetadataKeys.SoftDelete.LegacyType, "soft-delete-type")]
        [InlineData(MetadataKeys.SoftDelete.LegacyColumn, "soft-delete-column")]
        [InlineData(MetadataKeys.SoftDelete.DeleteType, "delete-type")]
        public void SoftDeleteKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }

        [Theory]
        [InlineData(MetadataKeys.Audit.Table, "audit-table")]
        [InlineData(MetadataKeys.Audit.LegacyUserKey, "audit-user-key")]
        [InlineData(MetadataKeys.Audit.UserKey, "user-audit-key")]
        public void AuditKeys_HaveCorrectValues(string actual, string expected)
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
        [InlineData(MetadataKeys.Validation.Server, "server-validation")]
        [InlineData(MetadataKeys.Validation.Plugin, "validation-plugin")]
        public void ValidationKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }

        [Theory]
        [InlineData(MetadataKeys.Computed.Sql, "computed-sql")]
        [InlineData(MetadataKeys.Computed.Provider, "computed-plugin")]
        public void ComputedKeys_HaveCorrectValues(string actual, string expected)
        {
            actual.Should().Be(expected);
        }
        
        [Theory]
        [InlineData(MetadataKeys.Enum.Values, "enum-values")]
        [InlineData(MetadataKeys.Enum.Labels, "enum-labels")]
        [InlineData(MetadataKeys.Enum.Ref, "enum-ref")]
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
            MetadataKeys.Security.TenantFilter.Should().NotBeNullOrEmpty();
            MetadataKeys.StateMachine.StateColumn.Should().NotBeNullOrEmpty();
            MetadataKeys.SoftDelete.Column.Should().NotBeNullOrEmpty();
            MetadataKeys.Audit.UserKey.Should().NotBeNullOrEmpty();
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

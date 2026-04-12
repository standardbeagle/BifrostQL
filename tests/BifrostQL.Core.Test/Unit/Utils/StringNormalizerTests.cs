using BifrostQL.Core.Utils;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Utils
{
    public class StringNormalizerTests
    {
        [Theory]
        [InlineData("VARCHAR", "varchar")]
        [InlineData("varchar", "varchar")]
        [InlineData("VARCHAR(255)", "varchar(255)")]
        [InlineData("  VARCHAR  ", "varchar")]
        [InlineData("  VARCHAR(255)  ", "varchar(255)")]
        [InlineData("Nvarchar", "nvarchar")]
        [InlineData("INT", "int")]
        [InlineData("Int", "int")]
        public void NormalizeType_WithVariousInputs_ReturnsNormalized(string input, string expected)
        {
            // Act
            var result = StringNormalizer.NormalizeType(input);
            
            // Assert
            result.Should().Be(expected);
        }
        
        [Theory]
        [InlineData("UserName", "username")]
        [InlineData("user_name", "user_name")]
        [InlineData("  UserName  ", "username")]
        [InlineData("USER_NAME", "user_name")]
        public void NormalizeName_WithVariousInputs_ReturnsNormalized(string input, string expected)
        {
            // Act
            var result = StringNormalizer.NormalizeName(input);
            
            // Assert
            result.Should().Be(expected);
        }
        
        [Fact]
        public void NormalizeType_WithNull_ReturnsEmptyString()
        {
            // Act
            var result = StringNormalizer.NormalizeType(null);
            
            // Assert
            result.Should().BeEmpty();
        }
        
        [Fact]
        public void NormalizeName_WithNull_ReturnsEmptyString()
        {
            // Act
            var result = StringNormalizer.NormalizeName(null);
            
            // Assert
            result.Should().BeEmpty();
        }
        
        [Fact]
        public void Normalize_WithNull_ReturnsEmptyString()
        {
            // Act
            var result = StringNormalizer.Normalize(null);
            
            // Assert
            result.Should().BeEmpty();
        }
        
        [Theory]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData("a", "a")]
        public void Normalize_WithEdgeCases_ReturnsExpected(string input, string expected)
        {
            // Act
            var result = StringNormalizer.Normalize(input);
            
            // Assert
            result.Should().Be(expected);
        }
        
        [Fact]
        public void NormalizeType_WithRealWorldDatabaseTypes_ReturnsExpected()
        {
            // Test real-world database type variations
            StringNormalizer.NormalizeType("VARCHAR(255)").Should().Be("varchar(255)");
            StringNormalizer.NormalizeType("nvarchar(max)").Should().Be("nvarchar(max)");
            StringNormalizer.NormalizeType("DECIMAL(10,2)").Should().Be("decimal(10,2)");
            StringNormalizer.NormalizeType("  INT  ").Should().Be("int");
            StringNormalizer.NormalizeType("datetime2").Should().Be("datetime2");
            StringNormalizer.NormalizeType("DateTime").Should().Be("datetime");
        }
    }
}

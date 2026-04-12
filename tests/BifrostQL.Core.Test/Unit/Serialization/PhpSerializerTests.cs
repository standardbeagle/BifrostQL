using BifrostQL.Core.Serialization;
using FluentAssertions;

namespace BifrostQL.Core.Serialization;

public class PhpSerializerTests
{
    [Fact]
    public void Deserialize_String()
    {
        PhpSerializer.Deserialize("s:5:\"hello\";").Should().Be("hello");
    }

    [Fact]
    public void Deserialize_Integer()
    {
        PhpSerializer.Deserialize("i:42;").Should().Be(42L);
    }

    [Fact]
    public void Deserialize_NegativeInteger()
    {
        PhpSerializer.Deserialize("i:-7;").Should().Be(-7L);
    }

    [Fact]
    public void Deserialize_Float()
    {
        PhpSerializer.Deserialize("d:3.14;").Should().Be(3.14);
    }

    [Fact]
    public void Deserialize_BoolTrue()
    {
        PhpSerializer.Deserialize("b:1;").Should().Be(true);
    }

    [Fact]
    public void Deserialize_BoolFalse()
    {
        PhpSerializer.Deserialize("b:0;").Should().Be(false);
    }

    [Fact]
    public void Deserialize_Null()
    {
        PhpSerializer.Deserialize("N;").Should().BeNull();
    }

    [Fact]
    public void Deserialize_EmptyArray()
    {
        var result = PhpSerializer.Deserialize("a:0:{}");
        result.Should().BeOfType<List<object?>>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_IndexedArray()
    {
        var result = PhpSerializer.Deserialize("a:3:{i:0;s:1:\"a\";i:1;s:1:\"b\";i:2;s:1:\"c\";}");
        result.Should().BeOfType<List<object?>>()
            .Which.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Deserialize_AssociativeArray()
    {
        var result = PhpSerializer.Deserialize("a:2:{s:4:\"name\";s:3:\"foo\";s:3:\"age\";i:30;}");
        var dict = result.Should().BeOfType<Dictionary<string, object?>>().Subject;
        dict["name"].Should().Be("foo");
        dict["age"].Should().Be(30L);
    }

    [Fact]
    public void Deserialize_NestedArray()
    {
        // a:2:{s:5:"inner";a:2:{i:0;i:1;i:1;i:2;}s:5:"outer";s:2:"ok";}
        var input = "a:2:{s:5:\"inner\";a:2:{i:0;i:1;i:1;i:2;}s:5:\"outer\";s:2:\"ok\";}";
        var result = PhpSerializer.Deserialize(input);
        var dict = result.Should().BeOfType<Dictionary<string, object?>>().Subject;
        dict["outer"].Should().Be("ok");
        var inner = dict["inner"].Should().BeOfType<List<object?>>().Subject;
        inner.Should().Equal(1L, 2L);
    }

    [Fact]
    public void Deserialize_Object()
    {
        var input = "O:8:\"stdClass\":1:{s:4:\"name\";s:3:\"foo\";}";
        var result = PhpSerializer.Deserialize(input);
        var dict = result.Should().BeOfType<Dictionary<string, object?>>().Subject;
        dict["name"].Should().Be("foo");
    }

    [Fact]
    public void Deserialize_EmptyString()
    {
        PhpSerializer.Deserialize("s:0:\"\";").Should().Be("");
    }

    [Fact]
    public void Deserialize_StringWithSpecialChars()
    {
        // String containing quotes, backslashes, and semicolons: he"l\o;
        // That's 7 bytes
        var input = "s:7:\"he\"l\\o;\";";
        var result = PhpSerializer.Deserialize(input);
        result.Should().Be("he\"l\\o;");
    }

    [Fact]
    public void Deserialize_NonPhpString_ReturnsOriginal()
    {
        PhpSerializer.Deserialize("just a plain string").Should().Be("just a plain string");
    }

    [Fact]
    public void Deserialize_NullInput_ReturnsNull()
    {
        PhpSerializer.Deserialize(null).Should().BeNull();
    }

    [Fact]
    public void Deserialize_MalformedInput_ReturnsOriginal()
    {
        PhpSerializer.Deserialize("a:broken{").Should().Be("a:broken{");
    }

    [Fact]
    public void Deserialize_RealWorldWordPress_ActivePlugins()
    {
        var input = "a:2:{i:0;s:19:\"akismet/akismet.php\";i:1;s:27:\"woocommerce/woocommerce.php\";}";
        var result = PhpSerializer.Deserialize(input);
        var list = result.Should().BeOfType<List<object?>>().Subject;
        list.Should().Equal("akismet/akismet.php", "woocommerce/woocommerce.php");
    }

    [Fact]
    public void Deserialize_RealWorldWordPress_Capabilities()
    {
        var input = "a:1:{s:13:\"administrator\";b:1;}";
        var result = PhpSerializer.Deserialize(input);
        var dict = result.Should().BeOfType<Dictionary<string, object?>>().Subject;
        dict["administrator"].Should().Be(true);
    }

    [Fact]
    public void ToJson_IndexedArray()
    {
        var input = "a:3:{i:0;s:1:\"a\";i:1;s:1:\"b\";i:2;s:1:\"c\";}";
        var json = PhpSerializer.ToJson(input);
        json.Should().Be("[\"a\",\"b\",\"c\"]");
    }

    [Fact]
    public void ToJson_AssociativeArray()
    {
        var input = "a:2:{s:4:\"name\";s:3:\"foo\";s:3:\"age\";i:30;}";
        var json = PhpSerializer.ToJson(input);
        json.Should().Be("{\"name\":\"foo\",\"age\":30}");
    }

    [Fact]
    public void ToJson_NonPhp_ReturnsOriginal()
    {
        PhpSerializer.ToJson("hello world").Should().Be("hello world");
    }

    [Theory]
    [InlineData("s:5:\"hello\";")]
    [InlineData("i:42;")]
    [InlineData("d:3.14;")]
    [InlineData("b:1;")]
    [InlineData("N;")]
    [InlineData("a:0:{}")]
    [InlineData("O:8:\"stdClass\":0:{}")]
    public void IsPhpSerialized_ValidFormats(string input)
    {
        PhpSerializer.IsPhpSerialized(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("123")]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("{\"json\":true}")]
    public void IsPhpSerialized_PlainStrings(string input)
    {
        PhpSerializer.IsPhpSerialized(input).Should().BeFalse();
    }

    [Fact]
    public void IsPhpSerialized_NullInput()
    {
        PhpSerializer.IsPhpSerialized(null).Should().BeFalse();
    }
}

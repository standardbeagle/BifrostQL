using System.Text;
using BifrostQL.Core.Forms;

namespace BifrostQL.Core.Test.Forms;

public class TypeMapperTests
{
    #region GetInputType

    [Theory]
    [InlineData("int", "number")]
    [InlineData("bigint", "number")]
    [InlineData("smallint", "number")]
    [InlineData("tinyint", "number")]
    [InlineData("decimal", "number")]
    [InlineData("numeric", "number")]
    [InlineData("money", "number")]
    [InlineData("smallmoney", "number")]
    [InlineData("float", "number")]
    [InlineData("real", "number")]
    [InlineData("bit", "checkbox")]
    [InlineData("boolean", "checkbox")]
    [InlineData("date", "date")]
    [InlineData("datetime", "datetime-local")]
    [InlineData("datetime2", "datetime-local")]
    [InlineData("smalldatetime", "datetime-local")]
    [InlineData("datetimeoffset", "datetime-local")]
    [InlineData("timestamp", "datetime-local")]
    [InlineData("time", "time")]
    [InlineData("uniqueidentifier", "text")]
    [InlineData("uuid", "text")]
    [InlineData("varchar", "text")]
    [InlineData("nvarchar", "text")]
    [InlineData("char", "text")]
    [InlineData("nchar", "text")]
    [InlineData("text", "textarea")]
    [InlineData("ntext", "textarea")]
    public void GetInputType_ReturnsCorrectHtmlType(string dataType, string expectedHtmlType)
    {
        var result = TypeMapper.GetInputType(dataType);

        Assert.Equal(expectedHtmlType, result);
    }

    [Fact]
    public void GetInputType_IsCaseInsensitive()
    {
        Assert.Equal("number", TypeMapper.GetInputType("INT"));
        Assert.Equal("number", TypeMapper.GetInputType("Int"));
        Assert.Equal("checkbox", TypeMapper.GetInputType("BIT"));
        Assert.Equal("datetime-local", TypeMapper.GetInputType("DateTime2"));
    }

    [Fact]
    public void GetInputType_UnknownType_ReturnsText()
    {
        Assert.Equal("text", TypeMapper.GetInputType("xml"));
        Assert.Equal("text", TypeMapper.GetInputType("geography"));
        Assert.Equal("text", TypeMapper.GetInputType("image"));
    }

    #endregion

    #region IsTextArea

    [Theory]
    [InlineData("text", true)]
    [InlineData("ntext", true)]
    [InlineData("varchar", false)]
    [InlineData("nvarchar", false)]
    [InlineData("int", false)]
    public void IsTextArea_ReturnsCorrectResult(string dataType, bool expected)
    {
        Assert.Equal(expected, TypeMapper.IsTextArea(dataType));
    }

    #endregion

    #region AppendTypeAttributes

    [Theory]
    [InlineData("int", "step=\"1\"")]
    [InlineData("bigint", "step=\"1\"")]
    [InlineData("smallint", "step=\"1\"")]
    [InlineData("tinyint", "step=\"1\"")]
    [InlineData("decimal", "step=\"0.01\"")]
    [InlineData("numeric", "step=\"0.01\"")]
    [InlineData("money", "step=\"0.01\"")]
    [InlineData("float", "step=\"any\"")]
    [InlineData("real", "step=\"any\"")]
    [InlineData("uniqueidentifier", "pattern=")]
    public void AppendTypeAttributes_AppendsExpectedAttributes(string dataType, string expectedContains)
    {
        var sb = new StringBuilder();

        TypeMapper.AppendTypeAttributes(sb, dataType);

        Assert.Contains(expectedContains, sb.ToString());
    }

    [Theory]
    [InlineData("varchar")]
    [InlineData("bit")]
    [InlineData("datetime2")]
    [InlineData("date")]
    public void AppendTypeAttributes_NoAttributesForTheseTypes(string dataType)
    {
        var sb = new StringBuilder();

        TypeMapper.AppendTypeAttributes(sb, dataType);

        Assert.Empty(sb.ToString());
    }

    #endregion

    #region IsNumericType

    [Theory]
    [InlineData("int", true)]
    [InlineData("decimal", true)]
    [InlineData("float", true)]
    [InlineData("varchar", false)]
    [InlineData("bit", false)]
    [InlineData("datetime2", false)]
    public void IsNumericType_ReturnsCorrectResult(string dataType, bool expected)
    {
        Assert.Equal(expected, TypeMapper.IsNumericType(dataType));
    }

    #endregion

    #region IsDateTimeType

    [Theory]
    [InlineData("date", true)]
    [InlineData("datetime", true)]
    [InlineData("datetime2", true)]
    [InlineData("time", true)]
    [InlineData("datetimeoffset", true)]
    [InlineData("varchar", false)]
    [InlineData("int", false)]
    public void IsDateTimeType_ReturnsCorrectResult(string dataType, bool expected)
    {
        Assert.Equal(expected, TypeMapper.IsDateTimeType(dataType));
    }

    #endregion

    #region IsBooleanType

    [Theory]
    [InlineData("bit", true)]
    [InlineData("boolean", true)]
    [InlineData("int", false)]
    [InlineData("varchar", false)]
    public void IsBooleanType_ReturnsCorrectResult(string dataType, bool expected)
    {
        Assert.Equal(expected, TypeMapper.IsBooleanType(dataType));
    }

    #endregion

    #region IsBinaryType

    [Theory]
    [InlineData("varbinary", true)]
    [InlineData("binary", true)]
    [InlineData("image", true)]
    [InlineData("blob", true)]
    [InlineData("VARBINARY", true)]
    [InlineData("Binary", true)]
    [InlineData("varchar", false)]
    [InlineData("int", false)]
    [InlineData("text", false)]
    [InlineData("ntext", false)]
    public void IsBinaryType_ReturnsCorrectResult(string dataType, bool expected)
    {
        Assert.Equal(expected, TypeMapper.IsBinaryType(dataType));
    }

    #endregion
}

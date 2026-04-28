using BifrostQL.Core.Views;

namespace BifrostQL.Core.Test.Views;

public class ValueFormatterTests
{
    #region FormatDateTime

    [Fact]
    public void FormatDateTime_DateTime_ReturnsTimeElement()
    {
        var dt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        var result = ValueFormatter.FormatDateTime(dt);

        Assert.Contains("<time datetime=", result);
        Assert.Contains("</time>", result);
        Assert.Contains("January 15, 2024", result);
    }

    [Fact]
    public void FormatDateTime_DateTimeOffset_ReturnsTimeElement()
    {
        var dto = new DateTimeOffset(2024, 6, 20, 14, 0, 0, TimeSpan.Zero);

        var result = ValueFormatter.FormatDateTime(dto);

        Assert.Contains("<time datetime=", result);
        Assert.Contains("June 20, 2024", result);
    }

    [Fact]
    public void FormatDateTime_StringDate_ParsesAndFormats()
    {
        var result = ValueFormatter.FormatDateTime("2024-03-10");

        Assert.Contains("<time datetime=", result);
        Assert.Contains("March 10, 2024", result);
    }

    [Fact]
    public void FormatDateTime_NonDateValue_ReturnsEncodedString()
    {
        var result = ValueFormatter.FormatDateTime("not a date");

        Assert.DoesNotContain("<time", result);
        Assert.Contains("not a date", result);
    }

    #endregion

    #region FormatBoolean

    [Fact]
    public void FormatBoolean_True_ReturnsYes()
    {
        Assert.Equal("Yes", ValueFormatter.FormatBoolean(true));
    }

    [Fact]
    public void FormatBoolean_False_ReturnsNo()
    {
        Assert.Equal("No", ValueFormatter.FormatBoolean(false));
    }

    [Fact]
    public void FormatBoolean_StringTrue_ReturnsYes()
    {
        Assert.Equal("Yes", ValueFormatter.FormatBoolean("true"));
        Assert.Equal("Yes", ValueFormatter.FormatBoolean("1"));
        Assert.Equal("Yes", ValueFormatter.FormatBoolean("True"));
    }

    [Fact]
    public void FormatBoolean_StringFalse_ReturnsNo()
    {
        Assert.Equal("No", ValueFormatter.FormatBoolean("false"));
        Assert.Equal("No", ValueFormatter.FormatBoolean("0"));
    }

    #endregion

    #region FormatNull

    [Fact]
    public void FormatNull_ReturnsNullSpan()
    {
        var result = ValueFormatter.FormatNull();

        Assert.Contains("(null)", result);
        Assert.Contains("class=\"null-value\"", result);
    }

    #endregion

    #region TruncateText

    [Fact]
    public void TruncateText_ShortText_ReturnsFullText()
    {
        var result = ValueFormatter.TruncateText("Hello", 10);

        Assert.Equal("Hello", result);
    }

    [Fact]
    public void TruncateText_ExactLength_ReturnsFullText()
    {
        var result = ValueFormatter.TruncateText("Hello", 5);

        Assert.Equal("Hello", result);
    }

    [Fact]
    public void TruncateText_LongText_TruncatesWithEllipsis()
    {
        var result = ValueFormatter.TruncateText("Hello World", 5);

        Assert.Contains("Hello", result);
        Assert.Contains("&hellip;", result);
        Assert.DoesNotContain("World", result);
    }

    [Fact]
    public void TruncateText_HtmlEncodes()
    {
        var result = ValueFormatter.TruncateText("<b>Hi</b>", 20);

        Assert.DoesNotContain("<b>", result);
        Assert.Contains("&lt;b&gt;", result);
    }

    [Fact]
    public void TruncateText_ZeroMaxLength_ReturnsFullEncodedText()
    {
        var result = ValueFormatter.TruncateText("Hello", 0);

        Assert.Equal("Hello", result);
    }

    #endregion

    #region FormatFileSize

    [Fact]
    public void FormatFileSize_Bytes_ReturnsBytes()
    {
        Assert.Equal("500 B", ValueFormatter.FormatFileSize(500));
    }

    [Fact]
    public void FormatFileSize_Kilobytes_ReturnsKB()
    {
        Assert.Equal("1 KB", ValueFormatter.FormatFileSize(1024));
    }

    [Fact]
    public void FormatFileSize_Megabytes_ReturnsMB()
    {
        Assert.Equal("1.5 MB", ValueFormatter.FormatFileSize(1572864));
    }

    [Fact]
    public void FormatFileSize_Zero_ReturnsZeroBytes()
    {
        Assert.Equal("0 B", ValueFormatter.FormatFileSize(0));
    }

    [Fact]
    public void FormatFileSize_Negative_ReturnsZeroBytes()
    {
        Assert.Equal("0 B", ValueFormatter.FormatFileSize(-1));
    }

    [Fact]
    public void FormatFileSize_Gigabytes_ReturnsGB()
    {
        Assert.Equal("1 GB", ValueFormatter.FormatFileSize(1073741824));
    }

    #endregion
}

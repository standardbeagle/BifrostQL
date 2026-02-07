using System.Text.Json;
using System.Text.Json.Serialization;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Handles output formatting for the CLI tool, supporting both
/// human-readable (with ANSI colors) and JSON output modes.
/// </summary>
public sealed class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TextWriter _writer;
    private readonly bool _jsonMode;
    private readonly bool _supportsAnsi;

    public OutputFormatter(TextWriter writer, bool jsonMode, bool supportsAnsi = true)
    {
        _writer = writer;
        _jsonMode = jsonMode;
        _supportsAnsi = supportsAnsi;
    }

    public bool IsJsonMode => _jsonMode;

    public void WriteSuccess(string message)
    {
        if (_jsonMode) return;
        _writer.Write(Colorize(message, AnsiColor.Green));
        _writer.WriteLine();
    }

    public void WriteError(string message)
    {
        if (_jsonMode) return;
        _writer.Write(Colorize(message, AnsiColor.Red));
        _writer.WriteLine();
    }

    public void WriteWarning(string message)
    {
        if (_jsonMode) return;
        _writer.Write(Colorize(message, AnsiColor.Yellow));
        _writer.WriteLine();
    }

    public void WriteInfo(string message)
    {
        if (_jsonMode) return;
        _writer.WriteLine(message);
    }

    public void WriteHeader(string message)
    {
        if (_jsonMode) return;
        _writer.Write(Colorize(message, AnsiColor.Cyan));
        _writer.WriteLine();
    }

    public void WriteJson(object data)
    {
        _writer.WriteLine(JsonSerializer.Serialize(data, JsonOptions));
    }

    public void WritePlain(string text)
    {
        _writer.WriteLine(text);
    }

    internal string Colorize(string text, AnsiColor color)
    {
        if (!_supportsAnsi)
            return text;
        return $"\u001b[{(int)color}m{text}\u001b[0m";
    }
}

internal enum AnsiColor
{
    Red = 31,
    Green = 32,
    Yellow = 33,
    Cyan = 36,
}

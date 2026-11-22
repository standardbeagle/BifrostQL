using System.Data.Common;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Constant CLI error text for failures whose exception message can quote the connection
/// string. A provider's parse error names the keyword it rejected, and a password holding an
/// unquoted ';' splits into a second keyword: <c>Password=ab;cd=ef</c> fails with
/// "Keyword not supported: 'cd'", which prints part of the password.
/// </summary>
internal static class CliErrorText
{
    public const string MalformedConnectionString =
        "Connection string is malformed. The provider's message is withheld because it can quote part of a value; quote any value that contains ';'.";

    public const string UnhandledFailure =
        "Command failed. The exception message is withheld because it can quote connection-string text.";

    /// <summary>
    /// A well-shaped config file with no connection string, and none given by flag. One text,
    /// so every command that reads the file refuses it in the same words.
    /// </summary>
    public static string NoConnectionStringInConfigFile(string path)
        => $"No connectionStrings.bifrost found in {path}";

    /// <summary>
    /// True when <paramref name="exception"/>, or any exception it wraps, is of a family whose
    /// message can quote connection-string text: <see cref="ArgumentException"/> (every
    /// provider's connection-string parse error) or <see cref="DbException"/> (driver errors).
    /// Other exceptions (a port already in use, a bad export path) carry no connection-string
    /// text, and their message is the diagnosis.
    /// </summary>
    public static bool MayQuoteConnectionString(Exception exception)
    {
        if (exception is ArgumentException or DbException)
            return true;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(MayQuoteConnectionString);
        return exception.InnerException is { } inner && MayQuoteConnectionString(inner);
    }
}

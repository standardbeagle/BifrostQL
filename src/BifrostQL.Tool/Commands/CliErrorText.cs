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
}

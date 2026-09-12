namespace BifrostQL.Core.Auth;

/// <summary>
/// The shared <c>name[bracket-value]</c> grammar used by metadata collectors
/// (state-machine <c>transitions</c> roles, <c>policy-actions</c> grants). One
/// tokenizer, two callers — a bracket bug must not have two behaviors.
///
/// Shape: an optional <c>[...]</c> suffix exactly at the end of the token.
/// A stray <c>]</c> without <c>[</c>, an unclosed bracket, a nested <c>[</c>,
/// or trailing text after <c>]</c> is malformed and fails via the caller's
/// error factory, so each collector keeps its own error text.
/// </summary>
internal static class BracketGrammar
{
    /// <summary>
    /// Splits <paramref name="value"/> into the part before an optional
    /// terminal bracket and the bracket's inner content (null when absent).
    /// </summary>
    public static (string Value, string? BracketValue) SplitOptionalBracket(
        string value, Func<Exception> error)
    {
        var start = value.IndexOf('[');
        if (start < 0)
        {
            if (value.Contains(']'))
                throw error();

            return (value.Trim(), null);
        }

        var end = value.IndexOf(']', start + 1);
        if (end < 0 || end != value.Length - 1 || value.IndexOf('[', start + 1) >= 0)
            throw error();

        return (value[..start].Trim(), value[(start + 1)..end]);
    }

    /// <summary>
    /// Splits a list on <paramref name="separator"/> at bracket depth zero, so
    /// commas inside a grant bracket (<c>delete[a,b]</c>) do not split the
    /// token. An unbalanced bracket fails via the caller's error factory.
    /// </summary>
    public static IEnumerable<string> SplitTopLevel(string raw, char separator, Func<Exception> error)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            if (depth < 0)
                throw error();
            if (c == separator && depth == 0)
            {
                var token = raw[start..i].Trim();
                if (token.Length > 0)
                    yield return token;
                start = i + 1;
            }
        }
        if (depth != 0)
            throw error();
        var last = raw[start..].Trim();
        if (last.Length > 0)
            yield return last;
    }
}

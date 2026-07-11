namespace BifrostQL.Mcp
{
    /// <summary>
    /// A tool-argument failure whose message is written as a prompt the calling
    /// agent can act on directly (did-you-mean suggestions, allowed-value lists,
    /// inline examples). The MCP dispatch layer converts it into a tool result
    /// with <c>isError = true</c> — never a protocol fault — so the agent can
    /// self-correct and retry without operator intervention.
    /// </summary>
    internal sealed class ToolPromptException : Exception
    {
        public ToolPromptException(string message) : base(message) { }
    }
}

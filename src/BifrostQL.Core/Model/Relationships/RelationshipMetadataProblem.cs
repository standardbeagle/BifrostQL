namespace BifrostQL.Core.Model.Relationships
{
    /// <summary>
    /// One relationship metadata value a strategy's parse could not use: the key it came from,
    /// the offending value, and why. Discovery skips the entry; <c>ModelConfigValidator</c>
    /// reports it, so a typo fails model load instead of silently dropping a relationship.
    /// </summary>
    internal sealed record RelationshipMetadataProblem(string Key, string Value, string Message);
}

namespace BifrostQL.Core.Test;

/// <summary>
/// Facts that swap the process-wide <see cref="BifrostQL.Core.Resolvers.BifrostErrorSink"/>
/// logger with <c>OverrideForTesting</c>. Two overrides on parallel threads overwrite each
/// other, so one class's fact can read a sink another class installed and miss its own entry.
/// Every class that calls <c>OverrideForTesting</c> joins this collection, which xUnit runs
/// with no other collection in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ErrorSinkLoggerCollection
{
    public const string Name = "BifrostErrorSink logger override";
}

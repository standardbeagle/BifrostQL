using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Facts that swap the process-wide <see cref="BifrostQL.Core.Resolvers.BifrostErrorSink"/>
/// logger. Two swaps on parallel threads overwrite each other, and one class's restore can
/// remove another's sink before its fault is logged, so the fact reads an empty capture. Every
/// class that sets <c>BifrostErrorSink.Logger</c> joins this collection, which xUnit runs with no
/// other collection in parallel (the BifrostQL.Core.Test collection of the same name does the
/// same for that assembly).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ErrorSinkLoggerCollection
{
    public const string Name = "BifrostErrorSink logger override";
}

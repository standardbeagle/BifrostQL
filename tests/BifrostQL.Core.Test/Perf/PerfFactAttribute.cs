using Xunit;

namespace BifrostQL.Core.Test.Perf;

/// <summary>
/// A measurement fact: it prints numbers and asserts only that the run completed.
/// It runs only when <c>BIFROST_PERF=1</c>, so the epic and release tiers never pay
/// for it and never flake on a slow machine.
/// </summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BIFROST_PERF") != "1")
            Skip = "Measurement only: set BIFROST_PERF=1 to run.";
    }
}

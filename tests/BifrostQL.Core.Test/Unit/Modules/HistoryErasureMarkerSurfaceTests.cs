using System.Reflection;
using BifrostQL.Core.Modules.History;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// The erasure marker deletes a row's whole audit trail, so only the retention engine may
/// produce it (protocol-adapter-security invariant 14). This assembly sits inside Core's
/// InternalsVisibleTo boundary and can always reach it, so the guard is the public surface:
/// an out-of-tree <c>IMutationIntentExecutor</c> caller must have no way to read the instance.
/// </summary>
public sealed class HistoryErasureMarkerSurfaceTests
{
    [Fact]
    public void Marker_IsNotReachableFromThePublicSurface()
    {
        var type = typeof(HistoryErasure);
        const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static;

        type.GetField(nameof(HistoryErasure.Marker), publicStatic)
            .Should().BeNull("a public field hands every intent caller the erasure capability");
        type.GetFields(publicStatic).Where(f => !f.IsLiteral)
            .Should().BeEmpty("no public static instance may stand in for the marker");
        type.GetProperties(publicStatic)
            .Should().BeEmpty("no public static property may hand out the marker");
        type.GetMethods(publicStatic).Where(m => m.ReturnType == typeof(object))
            .Should().BeEmpty("no public factory may hand out the marker");
    }
}

using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model.AppSchema;

public class AppSchemaDetectionServiceTests
{
    private static readonly IReadOnlyList<IDbTable> EmptyTables = Array.Empty<IDbTable>();
    private static readonly IReadOnlyCollection<string> EmptySchemas = Array.Empty<string>();

    [Fact]
    public void Detect_AutoDetectDisabled_ReturnsNull()
    {
        var detector = new TestDetector("test-app", alwaysMatch: true);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?> { ["auto-detect-app"] = "disabled" };

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().BeNull();
        detector.DetectCallCount.Should().Be(0);
    }

    [Fact]
    public void Detect_ForcedAppSchema_UsesSpecificDetector()
    {
        var targetDetector = new TestDetector("target-app", alwaysMatch: true);
        var otherDetector = new TestDetector("other-app", alwaysMatch: true);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { otherDetector, targetDetector });
        var metadata = new Dictionary<string, object?> { ["app-schema"] = "target-app" };

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("target-app");
        targetDetector.DetectCallCount.Should().Be(1);
        otherDetector.DetectCallCount.Should().Be(0);
    }

    [Fact]
    public void Detect_ForcedAppSchema_UnknownApp_ReturnsNull()
    {
        var detector = new TestDetector("real-app", alwaysMatch: true);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?> { ["app-schema"] = "nonexistent" };

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().BeNull();
        detector.DetectCallCount.Should().Be(0);
    }

    [Fact]
    public void Detect_NoDetectorsMatch_ReturnsNull()
    {
        var detector1 = new TestDetector("app-a", alwaysMatch: false);
        var detector2 = new TestDetector("app-b", alwaysMatch: false);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { detector1, detector2 });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().BeNull();
        metadata.Should().NotContainKey("detected-app");
    }

    [Fact]
    public void Detect_FirstMatchWins()
    {
        var first = new TestDetector("first-app", alwaysMatch: true);
        var second = new TestDetector("second-app", alwaysMatch: true);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { first, second });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("first-app");
        first.DetectCallCount.Should().Be(1);
        second.DetectCallCount.Should().Be(0);
    }

    [Fact]
    public void Detect_SetsDetectedAppMetadata()
    {
        var detector = new TestDetector("wordpress", alwaysMatch: true);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        metadata.Should().ContainKey("detected-app");
        metadata["detected-app"].Should().Be("wordpress");
    }

    [Fact]
    public void Detect_DisabledDetector_IsSkipped()
    {
        var disabled = new TestDetector("disabled-app", alwaysMatch: true, enabled: false);
        var enabled = new TestDetector("enabled-app", alwaysMatch: true);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { disabled, enabled });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("enabled-app");
        disabled.DetectCallCount.Should().Be(0);
    }

    [Fact]
    public void Detect_ForcedAppSchema_BypassesIsEnabled()
    {
        // When app-schema is forced, the detector's IsEnabled is not checked
        var detector = new TestDetector("forced-app", alwaysMatch: true, enabled: false);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?> { ["app-schema"] = "forced-app" };

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("forced-app");
    }

    [Fact]
    public void Detect_NullExistingSchemas_DefaultsToEmpty()
    {
        var detector = new TestDetector("test-app", alwaysMatch: true);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata);

        result.Should().NotBeNull();
        detector.DetectCallCount.Should().Be(1);
    }

    /// <summary>
    /// Configurable test detector for unit testing the detection service.
    /// </summary>
    private sealed class TestDetector : IAppSchemaDetector
    {
        private readonly bool _alwaysMatch;
        private readonly bool _enabled;

        public TestDetector(string appName, bool alwaysMatch, bool enabled = true)
        {
            AppName = appName;
            _alwaysMatch = alwaysMatch;
            _enabled = enabled;
        }

        public string AppName { get; }
        public int DetectCallCount { get; private set; }

        public bool IsEnabled(IDictionary<string, object?> dbMetadata) => _enabled;

        public AppSchemaResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas)
        {
            DetectCallCount++;
            if (!_alwaysMatch)
                return null;

            return new AppSchemaResult(
                AppName,
                Array.Empty<PrefixGroup>(),
                new Dictionary<string, IDictionary<string, object?>>(),
                Array.Empty<SyntheticForeignKey>());
        }
    }
}

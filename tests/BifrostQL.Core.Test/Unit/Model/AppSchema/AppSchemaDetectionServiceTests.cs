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
        var detector = new TestDetector("test-app", confidence: 0.9);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?> { ["auto-detect-app"] = "disabled" };

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().BeNull();
        detector.DetectCallCount.Should().Be(0);
    }

    [Fact]
    public void Detect_ForcedAppSchema_UsesSpecificDetector()
    {
        var targetDetector = new TestDetector("target-app", confidence: 0.9);
        var otherDetector = new TestDetector("other-app", confidence: 0.9);
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
        var detector = new TestDetector("real-app", confidence: 0.9);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?> { ["app-schema"] = "nonexistent" };

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().BeNull();
        detector.DetectCallCount.Should().Be(0);
    }

    [Fact]
    public void Detect_NoDetectorsMatch_ReturnsNull()
    {
        var detector1 = new TestDetector("app-a", confidence: 0.0);
        var detector2 = new TestDetector("app-b", confidence: 0.0);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { detector1, detector2 });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().BeNull();
        metadata.Should().NotContainKey("detected-app");
    }

    [Fact]
    public void Detect_HighestConfidenceWins()
    {
        var lowConfidence = new TestDetector("low-app", confidence: 0.6);
        var highConfidence = new TestDetector("high-app", confidence: 0.95);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { lowConfidence, highConfidence });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("high-app");
        lowConfidence.DetectCallCount.Should().Be(1);
        highConfidence.DetectCallCount.Should().Be(1);
    }

    [Fact]
    public void Detect_SetsDetectedAppMetadata()
    {
        var detector = new TestDetector("wordpress", confidence: 0.9);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        metadata.Should().ContainKey("detected-app");
        metadata["detected-app"].Should().Be("wordpress");
        metadata.Should().ContainKey("detection-confidence");
        metadata["detection-confidence"].Should().Be(0.9);
    }

    [Fact]
    public void Detect_DisabledDetector_IsSkipped()
    {
        var disabled = new TestDetector("disabled-app", confidence: 0.9, enabled: false);
        var enabled = new TestDetector("enabled-app", confidence: 0.9);
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
        var detector = new TestDetector("forced-app", confidence: 0.9, enabled: false);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?> { ["app-schema"] = "forced-app" };

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("forced-app");
    }

    [Fact]
    public void Detect_NullExistingSchemas_DefaultsToEmpty()
    {
        var detector = new TestDetector("test-app", confidence: 0.9);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata);

        result.Should().NotBeNull();
        detector.DetectCallCount.Should().Be(1);
    }

    [Fact]
    public void Detect_BelowMinimumConfidenceThreshold_ReturnsNull()
    {
        var detector = new TestDetector("low-confidence-app", confidence: 0.3);
        var service = new AppSchemaDetectionService(new[] { detector })
        {
            MinimumConfidenceThreshold = 0.5
        };
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().BeNull();
    }

    [Fact]
    public void Detect_AtMinimumConfidenceThreshold_ReturnsResult()
    {
        var detector = new TestDetector("threshold-app", confidence: 0.5);
        var service = new AppSchemaDetectionService(new[] { detector })
        {
            MinimumConfidenceThreshold = 0.5
        };
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("threshold-app");
    }

    [Fact]
    public void DetectAll_ReturnsAllResultsOrderedByConfidence()
    {
        var lowConfidence = new TestDetector("low-app", confidence: 0.6);
        var mediumConfidence = new TestDetector("medium-app", confidence: 0.75);
        var highConfidence = new TestDetector("high-app", confidence: 0.95);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { mediumConfidence, lowConfidence, highConfidence });
        var metadata = new Dictionary<string, object?>();

        var results = service.DetectAll(EmptyTables, metadata, EmptySchemas);

        results.Should().HaveCount(3);
        results[0].AppName.Should().Be("high-app");
        results[0].Confidence.Should().Be(0.95);
        results[1].AppName.Should().Be("medium-app");
        results[1].Confidence.Should().Be(0.75);
        results[2].AppName.Should().Be("low-app");
        results[2].Confidence.Should().Be(0.6);
    }

    [Fact]
    public void DetectAll_WithDisabledDetectors_ExcludesDisabled()
    {
        var enabled = new TestDetector("enabled-app", confidence: 0.9);
        var disabled = new TestDetector("disabled-app", confidence: 0.9, enabled: false);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { enabled, disabled });
        var metadata = new Dictionary<string, object?>();

        var results = service.DetectAll(EmptyTables, metadata, EmptySchemas);

        results.Should().HaveCount(1);
        results[0].AppName.Should().Be("enabled-app");
    }

    [Fact]
    public void DetectAll_WithAutoDetectDisabled_ReturnsEmpty()
    {
        var detector = new TestDetector("test-app", confidence: 0.9);
        var service = new AppSchemaDetectionService(new[] { detector });
        var metadata = new Dictionary<string, object?> { ["auto-detect-app"] = "disabled" };

        var results = service.DetectAll(EmptyTables, metadata, EmptySchemas);

        results.Should().BeEmpty();
    }

    [Fact]
    public void Detect_TieBreaker_FirstDetectorWins()
    {
        // When confidence is equal, the first detector in the list wins
        var first = new TestDetector("first-app", confidence: 0.8);
        var second = new TestDetector("second-app", confidence: 0.8);
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[] { first, second });
        var metadata = new Dictionary<string, object?>();

        var result = service.Detect(EmptyTables, metadata, EmptySchemas);

        result.Should().NotBeNull();
        result!.AppName.Should().Be("first-app");
    }

    /// <summary>
    /// Configurable test detector for unit testing the detection service.
    /// </summary>
    private sealed class TestDetector : IAppSchemaDetector
    {
        private readonly double _confidence;
        private readonly bool _enabled;

        public TestDetector(string appName, double confidence, bool enabled = true)
        {
            AppName = appName;
            _confidence = confidence;
            _enabled = enabled;
        }

        public string AppName { get; }
        public int DetectCallCount { get; private set; }

        public bool IsEnabled(IDictionary<string, object?> dbMetadata) => _enabled;

        public DetectionResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas)
        {
            DetectCallCount++;
            if (_confidence <= 0)
                return null;

            var schemaResult = new AppSchemaResult(
                AppName,
                Array.Empty<PrefixGroup>(),
                new Dictionary<string, IDictionary<string, object?>>(),
                Array.Empty<SyntheticForeignKey>());

            return DetectionResult.Create(AppName, _confidence, schemaResult);
        }
    }
}

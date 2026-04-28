# App Schema Detection Framework

The App Schema Detection Framework automatically identifies known application schemas (WordPress, Drupal, etc.) in your database and applies optimized configurations. This eliminates manual setup for common applications.

## Overview

When BifrostQL connects to a database, it scans table names for patterns that identify known applications. Each detector looks for signature tables—specific tables that indicate a particular application schema. When detected, BifrostQL automatically:

1. **Injects foreign keys** — Adds relationships the application relies on but doesn't declare in DDL
2. **Hides internal tables** — Conceals infrastructure tables not useful through the API
3. **Applies friendly labels** — Adds human-readable names to tables and columns
4. **Configures EAV flattening** — Registers meta tables for automatic key-value flattening
5. **Handles serialized data** — Detects and deserializes application-specific data formats

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                 AppSchemaDetectionService                   │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────┐ │
│  │ WordPressDetector│  │  DrupalDetector │  │  YourDetector│ │
│  └────────┬────────┘  └────────┬────────┘  └──────┬──────┘ │
└───────────┼────────────────────┼──────────────────┼────────┘
            │                    │                  │
            └────────────────────┴──────────────────┘
                              │
                              ▼
                    ┌──────────────────┐
                    │ DetectionResult  │
                    │ - AppName        │
                    │ - Confidence     │
                    │ - SchemaResult   │
                    └──────────────────┘
```

### Core Components

| Component | Description |
|-----------|-------------|
| `IAppSchemaDetector` | Interface for implementing custom detectors |
| `AppSchemaDetectionService` | Orchestrates detection and selects best match |
| `DetectionResult` | Contains confidence score and schema configuration |
| `AppSchemaResult` | Detailed schema configuration (prefix groups, metadata, FKs) |
| `WordPressDetector` | Built-in detector for WordPress schemas |
| `DrupalDetector` | Built-in detector for Drupal schemas |

## Confidence-Based Detection

Each detector returns a confidence score from 0.0 to 1.0:

- **0.0-0.49**: Insufficient confidence, result discarded
- **0.50-0.69**: Moderate confidence (basic signature tables found)
- **0.70-0.89**: High confidence (signature + supporting tables)
- **0.90-1.0**: Very high confidence (comprehensive match)

The `AppSchemaDetectionService` runs all enabled detectors and selects the result with the highest confidence above the minimum threshold (default: 0.5).

### Confidence Calculation

Detectors calculate confidence based on:

1. **Signature tables** (required): All must be present for detection
2. **Supporting tables** (optional): Increase confidence when found
3. **Multisite patterns**: Multiple prefixes indicate higher confidence

Example from `WordPressDetector`:

```csharp
// Base confidence for having all signature tables
const double baseConfidence = 0.6;

// Additional confidence from supporting tables
var supportingRatio = supportingTablesFound / (double)SupportingTables.Length;
var additionalConfidence = supportingRatio * 0.35;

// Multisite bonus
var multisiteBonus = prefixCount > 1 ? 0.05 : 0.0;

return Math.Min(baseConfidence + additionalConfidence + multisiteBonus, 1.0);
```

## Built-in Detectors

### WordPress Detector

**Signature Tables** (required):
- `{prefix}users`
- `{prefix}posts`
- `{prefix}options`

**Supporting Tables** (increase confidence):
- `{prefix}postmeta`, `{prefix}usermeta`, `{prefix}termmeta`, `{prefix}commentmeta`
- `{prefix}comments`, `{prefix}terms`, `{prefix}term_taxonomy`, `{prefix}term_relationships`

**Features Applied**:
- Injects 10+ foreign key relationships
- Hides Action Scheduler tables
- Configures EAV flattening for meta tables
- Marks PHP serialized columns
- Adds friendly table labels
- Supports multisite (multiple prefixes)

### Drupal Detector

**Signature Tables** (required):
- `node`
- `node_field_data`
- `users_field_data`

**Supporting Tables** (increase confidence):
- `node_access`, `node_revision`, `node_field_revision`
- `taxonomy_term_field_data`, `taxonomy_term_hierarchy`, `taxonomy_index`
- `comment_field_data`, `comment_entity_statistics`
- Cache tables (`cache_*`), `sessions`, `watchdog`

**Features Applied**:
- Injects 11 foreign key relationships
- Hides cache and internal tables
- Adds friendly table labels

## Configuration

### Global Toggle

Enable or disable auto-detection globally:

```json
{
  "BifrostQL": {
    "Metadata": [
      "* { auto-detect-app: enabled }"
    ]
  }
}
```

Values:
- `enabled` — Run all registered detectors (default)
- `disabled` — Skip automatic detection entirely

### Force Specific App Schema

Bypass detection and force a specific detector:

```json
{
  "BifrostQL": {
    "Metadata": [
      "* { app-schema: wordpress }"
    ]
  }
}
```

Available values:
- `wordpress` — Force WordPress detection
- `drupal` — Force Drupal detection
- `none` — Disable app-specific handling

### Detection Results

After detection runs, results are available as read-only metadata:

```json
{
  "BifrostQL": {
    "Metadata": [
      "* { detected-app: wordpress, detection-confidence: 0.85 }"
    ]
  }
}
```

These are informational only—use `app-schema` to force a specific detector.

## Prefix Groups

Applications often use table prefixes to group related tables. The detection framework identifies these prefixes and scopes configuration within each group.

### Single Prefix

```
wp_users, wp_posts, wp_options, wp_postmeta → group: "wp"
```

### Multisite (WordPress)

```
wp_users, wp_posts, wp_options       → group: "wp"
wp_2_posts, wp_2_options             → group: "wp_2"
wp_3_posts, wp_3_options             → group: "wp_3"
```

Each group gets its own configuration, with foreign keys scoped within the group.

## Programmatic Usage

### Using the Detection Service

```csharp
// Create with default detectors
var detectionService = AppSchemaDetectionService.Default;

// Or create with custom detectors
var detectionService = new AppSchemaDetectionService(new[]
{
    new WordPressDetector(),
    new DrupalDetector(),
    new CustomDetector()
});

// Run detection
var tables = dbModel.Tables;
var dbMetadata = new Dictionary<string, object?>();
var result = detectionService.Detect(tables, dbMetadata);

if (result != null)
{
    Console.WriteLine($"Detected: {dbMetadata["detected-app"]}");
    Console.WriteLine($"Confidence: {dbMetadata["detection-confidence"]}");
}
```

### Getting All Detection Results

For debugging or diagnostics, get results from all detectors:

```csharp
var allResults = detectionService.DetectAll(tables, dbMetadata);

foreach (var detection in allResults)
{
    Console.WriteLine($"{detection.AppName}: {detection.Confidence:P}");
}
```

### Custom Minimum Confidence

Adjust the confidence threshold:

```csharp
var detectionService = new AppSchemaDetectionService(detectors)
{
    MinimumConfidenceThreshold = 0.7  // Require higher confidence
};
```

## Integration with BifrostQL

The detection framework integrates automatically when using `AddBifrostQL`:

```csharp
services.AddBifrostQL(options =>
{
    options.BindStandardConfig(configuration);
    // Detection runs automatically on database connection
});
```

### Manual Integration

For advanced scenarios, integrate detection manually:

```csharp
// In your DbModel loader
var detectionService = AppSchemaDetectionService.Default;
var schemaResult = detectionService.Detect(tables, metadata);

if (schemaResult != null)
{
    // Apply detected configuration
    foreach (var (tableKey, tableMetadata) in schemaResult.AdditionalMetadata)
    {
        // Apply metadata to tables
    }
    
    // Register EAV configs
    foreach (var eavConfig in schemaResult.EavConfigs)
    {
        model.EavConfigs.Add(eavConfig);
    }
    
    // Add synthetic foreign keys
    foreach (var fk in schemaResult.ExplicitForeignKeys)
    {
        model.AddSyntheticForeignKey(fk);
    }
}
```

## Data Structures

### DetectionResult

```csharp
public sealed class DetectionResult
{
    public required string AppName { get; init; }
    public required double Confidence { get; init; }
    public required AppSchemaResult SchemaResult { get; init; }
}
```

### AppSchemaResult

```csharp
public record AppSchemaResult(
    string AppName,
    IReadOnlyList<PrefixGroup> PrefixGroups,
    IDictionary<string, IDictionary<string, object?>> AdditionalMetadata,
    IReadOnlyList<SyntheticForeignKey> ExplicitForeignKeys)
{
    public IDictionary<string, IDictionary<string, object?>> ColumnMetadata { get; init; }
}
```

### PrefixGroup

```csharp
public record PrefixGroup(
    string Prefix,           // e.g., "wp_"
    string GroupName,        // e.g., "wp"
    IReadOnlySet<string> TableNames);
```

### SyntheticForeignKey

```csharp
public record SyntheticForeignKey(
    string ChildTable, string ChildColumn,
    string ParentTable, string ParentColumn);
```

## Best Practices

1. **Always validate confidence scores** — Don't rely on low-confidence detections for critical functionality
2. **Use prefix groups correctly** — Ensure FKs reference tables within the same prefix group
3. **Test with real databases** — Detection patterns should be validated against actual application installations
4. **Document signature tables** — Clearly document which tables are required vs. supporting
5. **Handle edge cases** — Consider renamed tables, custom prefixes, and partial installations

## Troubleshooting

### Detection Not Working

1. Check that all signature tables exist
2. Verify table names match expected patterns (case-insensitive)
3. Ensure `auto-detect-app` is not set to `disabled`
4. Check that tables share a common prefix (if applicable)

### Low Confidence Scores

1. Add more supporting tables to increase confidence
2. Check for missing tables that the detector expects
3. Consider forcing the app schema with `app-schema: <name>`

### Cross-Prefix Issues

1. Verify that explicit FKs are correctly defined with full table names
2. Check that prefix groups don't conflict with existing database schemas

## See Also

- [Creating Custom Detectors](CreatingCustomDetectors.md) — Guide for building your own detectors
- [WordPress Schema Bundle](WordPressSchemaBundle.md) — WordPress-specific documentation
- [EAV Module](EavModule.md) — Entity-Attribute-Value flattening documentation

# Creating Custom Detectors

This guide walks you through creating custom app schema detectors for the BifrostQL detection framework. You'll learn how to identify your application's database schema and automatically apply optimized configurations.

## Overview

Custom detectors allow BifrostQL to recognize your application's database schema and automatically:

- Inject foreign key relationships
- Hide internal/infrastructure tables
- Apply friendly labels
- Configure EAV flattening for meta tables
- Handle application-specific data formats

## Getting Started

### Implementing IAppSchemaDetector

Create a class that implements `IAppSchemaDetector`:

```csharp
using BifrostQL.Core.Model.AppSchema;

public sealed class MyAppDetector : IAppSchemaDetector
{
    public string AppName => "myapp";

    public bool IsEnabled(IDictionary<string, object?> dbMetadata)
    {
        // Check if detection is disabled globally
        if (dbMetadata.TryGetValue("auto-detect-app", out var val)
            && string.Equals(val?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase))
            return false;
        
        return true;
    }

    public DetectionResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas)
    {
        // Implementation here
    }
}
```

## Detection Strategy

### 1. Define Signature Tables

Signature tables are required for detection. All must be present for your detector to match.

```csharp
/// <summary>Signature tables (base names without prefix) — ALL must be present.</summary>
private static readonly string[] SignatureTables = { "users", "orders", "products" };
```

### 2. Define Supporting Tables

Supporting tables increase confidence when present but aren't required.

```csharp
/// <summary>Additional tables that strengthen confidence when present.</summary>
private static readonly string[] SupportingTables =
{
    "order_items", "product_categories", "user_profiles",
    "inventory", "shipping_methods", "payment_methods"
};
```

### 3. Implement Detection Logic

Here's a complete detector implementation:

```csharp
public sealed class MyAppDetector : IAppSchemaDetector
{
    public string AppName => "myapp";

    private static readonly string[] SignatureTables = { "users", "orders", "products" };
    private static readonly string[] SupportingTables = { "order_items", "categories", "inventory" };

    public bool IsEnabled(IDictionary<string, object?> dbMetadata)
    {
        if (dbMetadata.TryGetValue("auto-detect-app", out var val)
            && string.Equals(val?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public DetectionResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas)
    {
        var tableDbNames = new HashSet<string>(tables.Select(t => t.DbName), StringComparer.OrdinalIgnoreCase);

        // Find valid prefixes where all signature tables exist
        var validPrefixes = FindValidPrefixes(tableDbNames);
        if (validPrefixes.Count == 0)
            return null;

        // Check for conflicts with existing schemas
        var existingSchemasSet = new HashSet<string>(existingSchemas, StringComparer.OrdinalIgnoreCase);
        validPrefixes = validPrefixes
            .Where(p => !existingSchemasSet.Contains(GroupNameFromPrefix(p)))
            .ToList();

        if (validPrefixes.Count == 0)
            return null;

        // Build prefix groups and collect metadata
        var prefixGroups = new List<PrefixGroup>();
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>();
        var explicitForeignKeys = new List<SyntheticForeignKey>();
        var supportingTablesFound = 0;

        foreach (var prefix in validPrefixes)
        {
            var groupTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dbName in tableDbNames)
            {
                if (dbName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    groupTableNames.Add(dbName);
            }

            // Count supporting tables for confidence
            foreach (var supportingTable in SupportingTables)
            {
                if (groupTableNames.Contains(prefix + supportingTable))
                    supportingTablesFound++;
            }

            var groupName = GroupNameFromPrefix(prefix);
            prefixGroups.Add(new PrefixGroup(prefix, groupName, groupTableNames));

            // Add metadata for tables
            InjectTableMetadata(prefix, groupTableNames, additionalMetadata);
            
            // Add foreign keys
            InjectForeignKeys(prefix, groupTableNames, explicitForeignKeys);
        }

        // Calculate confidence
        var confidence = CalculateConfidence(supportingTablesFound, validPrefixes.Count);

        var schemaResult = new AppSchemaResult(AppName, prefixGroups, additionalMetadata, explicitForeignKeys);
        return DetectionResult.Create(AppName, confidence, schemaResult);
    }

    private static List<string> FindValidPrefixes(HashSet<string> tableDbNames)
    {
        var candidatePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in tableDbNames)
        {
            foreach (var sig in SignatureTables)
            {
                if (name.Length > sig.Length
                    && name.EndsWith(sig, StringComparison.OrdinalIgnoreCase)
                    && name[name.Length - sig.Length - 1] == '_')
                {
                    var prefix = name[..(name.Length - sig.Length)];
                    candidatePrefixes.Add(prefix);
                }
            }
        }

        return candidatePrefixes
            .Where(prefix => SignatureTables.All(
                sig => tableDbNames.Contains(prefix + sig)))
            .ToList();
    }

    private static string GroupNameFromPrefix(string prefix) =>
        prefix.EndsWith('_') ? prefix[..^1] : prefix;

    private static double CalculateConfidence(int supportingTablesFound, int prefixCount)
    {
        const double baseConfidence = 0.6;
        const double maxAdditionalConfidence = 0.35;
        
        var normalizedSupportingTables = supportingTablesFound / (double)prefixCount;
        var supportingRatio = normalizedSupportingTables / SupportingTables.Length;
        var additionalConfidence = supportingRatio * maxAdditionalConfidence;
        
        return Math.Min(baseConfidence + additionalConfidence, 1.0);
    }
}
```

## Adding Metadata

### Table Metadata

Inject metadata to configure table behavior:

```csharp
private void InjectTableMetadata(
    string prefix, 
    HashSet<string> groupTableNames,
    Dictionary<string, IDictionary<string, object?>> metadata)
{
    // Hide internal tables
    var internalTables = new[] { "logs", "cache", "sessions" };
    foreach (var table in internalTables)
    {
        var fullName = prefix + table;
        if (groupTableNames.Contains(fullName))
        {
            metadata[fullName] = new Dictionary<string, object?>
            {
                ["visibility"] = "hidden"
            };
        }
    }

    // Add friendly labels
    var labels = new Dictionary<string, string>
    {
        ["users"] = "Users",
        ["orders"] = "Orders",
        ["products"] = "Products",
        ["order_items"] = "Order Items"
    };

    foreach (var (baseName, label) in labels)
    {
        var fullName = prefix + baseName;
        if (groupTableNames.Contains(fullName))
        {
            if (metadata.TryGetValue(fullName, out var existing))
                existing["label"] = label;
            else
                metadata[fullName] = new Dictionary<string, object?> { ["label"] = label };
        }
    }
}
```

### Column Metadata

Inject column-level metadata for special handling:

```csharp
// Mark columns containing JSON data
var jsonColumns = new[] { ("users", "preferences"), ("products", "attributes") };
var columnMetadata = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

foreach (var (tableBase, columnName) in jsonColumns)
{
    var fullTableName = prefix + tableBase;
    if (groupTableNames.Contains(fullTableName))
    {
        var columnKey = $"{fullTableName}.{columnName}";
        columnMetadata[columnKey] = new Dictionary<string, object?>
        {
            ["type"] = "json"
        };
    }
}

// Include in AppSchemaResult
var schemaResult = new AppSchemaResult(AppName, prefixGroups, metadata, foreignKeys)
{
    ColumnMetadata = columnMetadata
};
```

### EAV Configuration

Configure EAV flattening for meta tables:

```csharp
// Define EAV table relationships
var eavTables = new[]
{
    ("product_meta", "products", "product_id", "meta_key", "meta_value"),
    ("user_meta", "users", "user_id", "meta_key", "meta_value")
};

foreach (var (meta, parent, fk, keyCol, valueCol) in eavTables)
{
    var metaFull = prefix + meta;
    var parentFull = prefix + parent;
    
    if (!groupTableNames.Contains(metaFull) || !groupTableNames.Contains(parentFull))
        continue;

    // Add EAV metadata to the meta table
    if (!metadata.TryGetValue(metaFull, out var metaDict))
    {
        metaDict = new Dictionary<string, object?>();
        metadata[metaFull] = metaDict;
    }
    
    metaDict["eav-parent"] = parentFull;
    metaDict["eav-fk"] = fk;
    metaDict["eav-key"] = keyCol;
    metaDict["eav-value"] = valueCol;
}
```

## Adding Foreign Keys

Inject synthetic foreign keys for relationships not declared in DDL:

```csharp
private static readonly SyntheticForeignKey[] MyAppForeignKeys =
{
    new("orders", "user_id", "users", "id"),
    new("order_items", "order_id", "orders", "id"),
    new("order_items", "product_id", "products", "id"),
    new("products", "category_id", "categories", "id"),
};

private void InjectForeignKeys(
    string prefix,
    HashSet<string> groupTableNames,
    List<SyntheticForeignKey> foreignKeys)
{
    foreach (var fk in MyAppForeignKeys)
    {
        var childFull = prefix + fk.ChildTable;
        var parentFull = prefix + fk.ParentTable;
        
        if (groupTableNames.Contains(childFull) && groupTableNames.Contains(parentFull))
        {
            foreignKeys.Add(new SyntheticForeignKey(
                childFull, fk.ChildColumn,
                parentFull, fk.ParentColumn));
        }
    }
}
```

## Registering Your Detector

### With Default Service

Add your detector to the default detection service:

```csharp
// Create custom detection service with built-in + custom detectors
var detectionService = new AppSchemaDetectionService(new IAppSchemaDetector[]
{
    new WordPressDetector(),
    new DrupalDetector(),
    new MyAppDetector()  // Your custom detector
});

// Use in your application
services.AddSingleton(detectionService);
```

### With Dependency Injection

Register your detector with the service collection:

```csharp
public static class MyAppExtensions
{
    public static IServiceCollection AddMyAppSupport(
        this IServiceCollection services)
    {
        // Register the detector
        services.AddSingleton<IAppSchemaDetector, MyAppDetector>();
        
        // Or register a custom service
        services.AddSingleton(provider =>
        {
            var detectors = provider.GetServices<IAppSchemaDetector>();
            return new AppSchemaDetectionService(detectors);
        });
        
        return services;
    }
}
```

Usage:

```csharp
services.AddMyAppSupport();
services.AddBifrostQL(options =>
{
    options.BindStandardConfig(configuration);
});
```

## Testing Your Detector

### Unit Tests

Create unit tests for your detector:

```csharp
public class MyAppDetectorTests
{
    [Fact]
    public void Detect_WithSignatureTables_ReturnsResult()
    {
        // Arrange
        var detector = new MyAppDetector();
        var tables = new List<IDbTable>
        {
            CreateTable("myapp_users"),
            CreateTable("myapp_orders"),
            CreateTable("myapp_products")
        };

        // Act
        var result = detector.Detect(tables, Array.Empty<string>());

        // Assert
        result.Should().NotBeNull();
        result!.AppName.Should().Be("myapp");
        result.Confidence.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Detect_WithSupportingTables_IncreasesConfidence()
    {
        // Arrange
        var detector = new MyAppDetector();
        var tables = new List<IDbTable>
        {
            CreateTable("myapp_users"),
            CreateTable("myapp_orders"),
            CreateTable("myapp_products"),
            CreateTable("myapp_order_items"),
            CreateTable("myapp_categories"),
            CreateTable("myapp_inventory")
        };

        // Act
        var result = detector.Detect(tables, Array.Empty<string>());

        // Assert
        result.Should().NotBeNull();
        result!.Confidence.Should().BeGreaterThan(0.7);
    }

    [Fact]
    public void Detect_MissingSignatureTable_ReturnsNull()
    {
        // Arrange
        var detector = new MyAppDetector();
        var tables = new List<IDbTable>
        {
            CreateTable("myapp_users"),
            CreateTable("myapp_orders")
            // Missing: myapp_products
        };

        // Act
        var result = detector.Detect(tables, Array.Empty<string>());

        // Assert
        result.Should().BeNull();
    }

    private static IDbTable CreateTable(string name)
    {
        return Substitute.For<IDbTable>();
    }
}
```

### Integration Tests

Test the full detection pipeline:

```csharp
public class AppSchemaDetectionIntegrationTests
{
    [Fact]
    public void DetectAll_WithMultipleDetectors_ReturnsAllResults()
    {
        // Arrange
        var service = new AppSchemaDetectionService(new IAppSchemaDetector[]
        {
            new WordPressDetector(),
            new MyAppDetector()
        });

        var tables = new List<IDbTable>
        {
            // WordPress tables
            CreateTable("wp_users"),
            CreateTable("wp_posts"),
            CreateTable("wp_options"),
            // MyApp tables
            CreateTable("myapp_users"),
            CreateTable("myapp_orders"),
            CreateTable("myapp_products")
        };

        var metadata = new Dictionary<string, object?>();

        // Act
        var results = service.DetectAll(tables, metadata);

        // Assert
        results.Should().HaveCount(2);
        results.Should().Contain(r => r.AppName == "wordpress");
        results.Should().Contain(r => r.AppName == "myapp");
    }
}
```

## Advanced Patterns

### Prefix-less Applications

Some applications don't use prefixes (like Drupal):

```csharp
public DetectionResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas)
{
    var tableDbNames = new HashSet<string>(tables.Select(t => t.DbName), StringComparer.OrdinalIgnoreCase);

    // Check for signature tables directly (no prefix)
    var hasAllSignatures = SignatureTables.All(sig => tableDbNames.Contains(sig));
    if (!hasAllSignatures)
        return null;

    // Use empty prefix for prefix-less apps
    var prefixGroups = new List<PrefixGroup>
    {
        new PrefixGroup("", "myapp", tableDbNames)
    };

    // ... rest of detection logic
}
```

### Multiple Prefix Support

Handle applications with multiple prefixes:

```csharp
// Find all valid prefixes
var validPrefixes = FindValidPrefixes(tableDbNames);

// Create a group for each prefix
foreach (var prefix in validPrefixes)
{
    var groupTableNames = tableDbNames
        .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var groupName = GroupNameFromPrefix(prefix);
    prefixGroups.Add(new PrefixGroup(prefix, groupName, groupTableNames));
}
```

### Conditional Detection

Enable/disable based on database metadata:

```csharp
public bool IsEnabled(IDictionary<string, object?> dbMetadata)
{
    // Check global disable
    if (dbMetadata.TryGetValue("auto-detect-app", out var autoDetect)
        && string.Equals(autoDetect?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase))
        return false;

    // Check for app-specific enable flag
    if (dbMetadata.TryGetValue("myapp-detection", out var myappDetect))
    {
        return string.Equals(myappDetect?.ToString(), "enabled", StringComparison.OrdinalIgnoreCase);
    }

    return true; // Enabled by default
}
```

## Best Practices

1. **Use case-insensitive comparisons** — Table names may vary in case across databases
2. **Validate column existence** — Check that columns exist before adding column metadata
3. **Handle schema-qualified names** — Support tables with schema prefixes (e.g., `dbo.users`)
4. **Document signature tables** — Clearly document which tables are required
5. **Test with real databases** — Validate against actual application installations
6. **Use confidence scoring** — Don't return 1.0 confidence unless absolutely certain
7. **Avoid false positives** — Be conservative with signature table selection

## Example: Complete Custom Detector

Here's a complete example for a hypothetical e-commerce application:

```csharp
public sealed class EcommerceDetector : IAppSchemaDetector
{
    public string AppName => "ecommerce";

    private static readonly string[] SignatureTables = { "customers", "orders", "products" };
    private static readonly string[] SupportingTables = { "order_items", "categories", "inventory", "reviews" };
    
    private static readonly SyntheticForeignKey[] ForeignKeys =
    {
        new("orders", "customer_id", "customers", "id"),
        new("order_items", "order_id", "orders", "id"),
        new("order_items", "product_id", "products", "id"),
        new("products", "category_id", "categories", "id"),
        new("reviews", "product_id", "products", "id"),
        new("reviews", "customer_id", "customers", "id"),
    };

    public bool IsEnabled(IDictionary<string, object?> dbMetadata)
    {
        if (dbMetadata.TryGetValue("auto-detect-app", out var val)
            && string.Equals(val?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public DetectionResult? Detect(IReadOnlyList<IDbTable> tables, IReadOnlyCollection<string> existingSchemas)
    {
        var tableDbNames = new HashSet<string>(tables.Select(t => t.DbName), StringComparer.OrdinalIgnoreCase);
        
        var validPrefixes = FindValidPrefixes(tableDbNames);
        if (validPrefixes.Count == 0)
            return null;

        var existingSchemasSet = new HashSet<string>(existingSchemas, StringComparer.OrdinalIgnoreCase);
        validPrefixes = validPrefixes
            .Where(p => !existingSchemasSet.Contains(GroupNameFromPrefix(p)))
            .ToList();

        if (validPrefixes.Count == 0)
            return null;

        var prefixGroups = new List<PrefixGroup>();
        var additionalMetadata = new Dictionary<string, IDictionary<string, object?>>();
        var explicitForeignKeys = new List<SyntheticForeignKey>();
        var supportingTablesFound = 0;

        foreach (var prefix in validPrefixes)
        {
            var groupTableNames = tableDbNames
                .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            supportingTablesFound += SupportingTables.Count(t => groupTableNames.Contains(prefix + t));

            prefixGroups.Add(new PrefixGroup(prefix, GroupNameFromPrefix(prefix), groupTableNames));

            InjectMetadata(prefix, groupTableNames, additionalMetadata);
            InjectForeignKeys(prefix, groupTableNames, explicitForeignKeys);
        }

        var confidence = CalculateConfidence(supportingTablesFound, validPrefixes.Count);
        var schemaResult = new AppSchemaResult(AppName, prefixGroups, additionalMetadata, explicitForeignKeys);
        
        return DetectionResult.Create(AppName, confidence, schemaResult);
    }

    private static List<string> FindValidPrefixes(HashSet<string> tableDbNames)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var name in tableDbNames)
        {
            foreach (var sig in SignatureTables)
            {
                if (name.EndsWith($"_{sig}", StringComparison.OrdinalIgnoreCase))
                {
                    prefixes.Add(name[..^(sig.Length + 1)] + "_");
                }
            }
        }

        return prefixes.Where(p => SignatureTables.All(
            s => tableDbNames.Contains(p + s))).ToList();
    }

    private static string GroupNameFromPrefix(string prefix) =>
        prefix.TrimEnd('_');

    private static double CalculateConfidence(int supportingFound, int prefixCount)
    {
        var baseConfidence = 0.6;
        var additional = (supportingFound / (double)(SupportingTables.Length * prefixCount)) * 0.35;
        return Math.Min(baseConfidence + additional, 1.0);
    }

    private static void InjectMetadata(string prefix, HashSet<string> tables, Dictionary<string, IDictionary<string, object?>> metadata)
    {
        // Hide internal tables
        foreach (var internalTable in new[] { "logs", "cache", "sessions" })
        {
            var fullName = prefix + internalTable;
            if (tables.Contains(fullName))
                metadata[fullName] = new Dictionary<string, object?> { ["visibility"] = "hidden" };
        }

        // Add labels
        var labels = new Dictionary<string, string>
        {
            ["customers"] = "Customers",
            ["orders"] = "Orders",
            ["products"] = "Products",
            ["order_items"] = "Order Items",
            ["categories"] = "Categories"
        };

        foreach (var (table, label) in labels)
        {
            var fullName = prefix + table;
            if (tables.Contains(fullName))
            {
                if (metadata.TryGetValue(fullName, out var existing))
                    existing["label"] = label;
                else
                    metadata[fullName] = new Dictionary<string, object?> { ["label"] = label };
            }
        }
    }

    private static void InjectForeignKeys(string prefix, HashSet<string> tables, List<SyntheticForeignKey> fks)
    {
        foreach (var fk in ForeignKeys)
        {
            var child = prefix + fk.ChildTable;
            var parent = prefix + fk.ParentTable;
            if (tables.Contains(child) && tables.Contains(parent))
                fks.Add(new SyntheticForeignKey(child, fk.ChildColumn, parent, fk.ParentColumn));
        }
    }
}
```

## See Also

- [App Schema Detection Framework](AppSchemaDetection.md) — Framework overview
- [WordPress Detector](src/BifrostQL.Core/Model/AppSchema/WordPressDetector.cs) — Reference implementation
- [Drupal Detector](src/BifrostQL.Core/Model/AppSchema/DrupalDetector.cs) — Prefix-less example

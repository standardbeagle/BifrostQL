# BifrostQL Utilities

This folder contains centralized utility classes used throughout BifrostQL.

## StringNormalizer

Centralized string normalization to ensure consistent handling of database identifiers and type names.

### Purpose

Eliminates duplication of `ToLowerInvariant().Trim()` patterns throughout the codebase and ensures consistent string comparison behavior.

### Usage

```csharp
// Instead of: type?.ToLowerInvariant().Trim() ?? ""
// Use:
var normalizedType = StringNormalizer.NormalizeType(column.DataType);

// Instead of: name?.ToLowerInvariant().Trim() ?? ""
// Use:
var normalizedName = StringNormalizer.NormalizeName(tableName);

// General normalization:
var normalized = StringNormalizer.Normalize(value);
```

### Methods

- `NormalizeType(string?)` - Normalizes database type names (e.g., "NVARCHAR" → "nvarchar")
- `NormalizeName(string?)` - Normalizes column/table names
- `Normalize(string?)` - General purpose normalization

All methods return empty string for null input and apply `ToLowerInvariant().Trim()`.

## When to Add Utilities

Add a new utility class when:
- The same logic appears in 3+ places
- The logic is pure (no side effects)
- The logic is domain-agnostic (could be used in multiple contexts)

Place domain-specific helpers in their appropriate folders (e.g., SQL dialect helpers in QueryModel).

# EAV Flattening Module

This module provides Entity-Attribute-Value (EAV) flattening functionality for BifrostQL, enabling dynamic pivot of EAV tables into queryable, wide-format virtual tables.

## Overview

EAV (Entity-Attribute-Value) is a common database pattern used for flexible schemas, particularly in WordPress and similar applications:

```sql
-- EAV table structure (e.g., wp_postmeta)
| post_id | meta_key | meta_value |
|---------|----------|------------|
| 1       | title    | Hello      |
| 1       | views    | 100        |
| 2       | title    | World      |
```

The flattening module transforms this into:

```
-- Flattened virtual table
| post_id | title | views |
|---------|-------|-------|
| 1       | Hello | 100   |
| 2       | World | null  |
```

## Components

### 1. EavDetector

Detects EAV patterns in database tables:
- `DetectFromMetadata()` - Detects from table metadata (eav-parent, eav-fk, eav-key, eav-value)
- `DetectHeuristic()` - Heuristic detection based on column naming patterns

### 2. EavFlattener

Core flattening logic:
- `EavFlattenedTable` - Represents a flattened virtual table
- `EavColumn` - Represents a dynamic column discovered from meta_keys
- `EavColumnDiscoverer` - Discovers columns by querying distinct meta_keys

### 3. EavSchemaTransformer

Schema generation for flattened tables:
- Generates GraphQL type definitions
- Creates field extensions on parent tables
- Provides naming conventions for flattened types

### 4. EavQueryTransformer

SQL generation for flattened queries:
- Generates dynamic pivot SQL using `MAX(CASE WHEN ...)` pattern
- Handles pagination and filtering
- Joins parent tables with pivoted EAV data

### 5. EavModule

Main orchestration class:
- Manages flattened table definitions
- Caches discovered column schemas
- Executes queries against flattened tables

### 6. EavResolver

GraphQL resolvers:
- `EavResolver` - Root-level query resolver
- `EavSingleResolver` - Single entity resolver (for nested queries)
- `EavColumnResolver` - Individual column value resolver

## Usage

### Configuration

EAV tables are automatically detected when table metadata includes:
- `eav-parent`: Parent table name (e.g., "wp_posts")
- `eav-fk`: Foreign key column (e.g., "post_id")
- `eav-key`: Attribute key column (e.g., "meta_key")
- `eav-value`: Attribute value column (e.g., "meta_value")

### WordPress Integration

The WordPressDetector automatically configures EAV metadata for WordPress databases:
- `wp_postmeta` → `wp_posts` (via post_id)
- `wp_usermeta` → `wp_users` (via user_id)
- `wp_termmeta` → `wp_terms` (via term_id)
- `wp_commentmeta` → `wp_comments` (via comment_id)

### GraphQL Queries

Once configured, flattened EAV data is accessible via:

```graphql
# Root-level query
query {
  wp_posts_flattened_postmeta(limit: 10) {
    data {
      ID
      _meta  # JSON object with all meta values
    }
    total
  }
}

# Nested query via parent entity
query {
  wp_posts(limit: 10) {
    data {
      ID
      post_title
      _flattened_postmeta {
        _meta
      }
    }
  }
}
```

## Type Conversion

The module includes `EavTypeConverter` for automatic type inference:
- Integer values → `Int`
- Decimal values → `Float`
- Boolean values → `Boolean`
- DateTime values → `DateTime`
- Mixed/Other → `String`

## Caching

Column discovery is cached via `EavSchemaCache`:
- Default TTL: 5 minutes
- Cache can be invalidated per table or globally
- Prevents repeated database queries for meta_key discovery

## Testing

Run EAV-specific tests:
```bash
dotnet test --filter "FullyQualifiedName~Eav"
```

Test files:
- `EavFlattenerTests.cs` - Core detection and type conversion tests
- `EavQueryTransformerTests.cs` - SQL generation tests
- `EavSchemaTransformerTests.cs` - Schema generation tests
- `EavModuleTests.cs` - Module integration tests

## Architecture

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│  EavDetector    │────▶│  EavModule       │────▶│ EavSchemaTransformer│
│  (Detection)    │     │  (Orchestration) │     │ (Schema Gen)    │
└─────────────────┘     └──────────────────┘     └─────────────────┘
         │                       │                         │
         ▼                       ▼                         ▼
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│  Table Metadata │     │ EavQueryTransformer    │ │ GraphQL Schema  │
│  (eav-parent,   │     │ (SQL Generation) │     │ (Type System)   │
│   eav-fk, etc.) │     └──────────────────┘     └─────────────────┘
└─────────────────┘              │                         │
                                 ▼                         ▼
                          ┌──────────────────┐     ┌─────────────────┐
                          │  EavResolver     │────▶│  Query Results  │
                          │  (GraphQL)       │     │  (Flattened)    │
                          └──────────────────┘     └─────────────────┘
```

## Future Enhancements

- [ ] Support for filtering on flattened columns
- [ ] Support for sorting by meta values
- [ ] Type inference from sample values
- [ ] Configurable column name mapping
- [ ] Support for JSON meta values

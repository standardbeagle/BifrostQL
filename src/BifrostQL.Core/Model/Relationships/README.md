# Table Relationship Detection

This folder contains the strategy pattern implementation for detecting and establishing relationships between database tables.

## Overview

Table relationships are detected through multiple strategies that run in sequence:

1. **ForeignKeyRelationshipStrategy** - Detects relationships from database foreign key constraints
2. **NameBasedRelationshipStrategy** - Detects relationships from column naming conventions (e.g., `user_id` → `users` table)
3. **ManyToManyDetectionStrategy** - Detects many-to-many relationships from metadata or junction table patterns

The `TableRelationshipOrchestrator` coordinates these strategies, passing results between them to avoid duplicate detection.

## Strategy Interface

All strategies implement `ITableRelationshipStrategy`:

```csharp
public interface ITableRelationshipStrategy
{
    void DiscoverRelationships(IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys);
}
```

## Adding a New Strategy

To add a new relationship detection strategy:

1. Create a class implementing `ITableRelationshipStrategy`
2. Add it to `TableRelationshipOrchestrator.LinkTables()` method
3. Consider what information it needs from previous strategies

Example:

```csharp
public sealed class CustomRelationshipStrategy : ITableRelationshipStrategy
{
    public void DiscoverRelationships(IDbModel model, IReadOnlyCollection<DbForeignKey> foreignKeys)
    {
        foreach (var table in model.Tables)
        {
            // Your detection logic here
            // Add links to table.SingleLinks or table.MultiLinks
        }
    }
}
```

## EAV Configuration

The `EavConfigCollector` uses the collector pattern to gather Entity-Attribute-Value (EAV) configurations from table metadata. EAV tables store key-value pairs that are flattened into JSON on the parent entity.

Metadata keys used:
- `eav-parent` - The parent table name
- `eav-fk` - Foreign key column in the EAV table
- `eav-key` - Column storing the attribute name
- `eav-value` - Column storing the attribute value

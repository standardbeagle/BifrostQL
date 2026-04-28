namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Configuration for an EAV (Entity-Attribute-Value) meta table.
/// Links a meta table to its parent entity table, enabling flattening
/// of key-value rows into a single JSON field on the parent.
/// </summary>
public record EavConfig(
    string MetaTableDbName,      // e.g., "wp_postmeta"
    string ParentTableDbName,    // e.g., "wp_posts"
    string ForeignKeyColumn,     // e.g., "post_id" — column in meta table referencing parent
    string KeyColumn,            // e.g., "meta_key"
    string ValueColumn);         // e.g., "meta_value"

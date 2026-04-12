# WordPress Schema Bundle

The WordPress Schema Bundle provides comprehensive support for WordPress databases in BifrostQL. It packages all WordPress-specific configurations, detectors, and transformations into a cohesive, easy-to-use bundle.

## Features

- **Automatic Detection**: Automatically detects WordPress schemas by looking for signature tables (`users`, `posts`, `options`)
- **PHP Serialization**: Handles PHP serialized data in `meta_value` and `option_value` columns
- **EAV Flattening**: Flattens Entity-Attribute-Value meta tables (`postmeta`, `usermeta`, `termmeta`, `commentmeta`) into JSON fields
- **File Storage**: Supports WordPress attachment file uploads and downloads
- **Foreign Keys**: Injects implicit WordPress foreign key relationships
- **Hidden Tables**: Automatically hides Action Scheduler tables
- **Friendly Labels**: Adds human-readable labels to WordPress tables

## Quick Start

### Basic Usage

The WordPress bundle is automatically enabled when BifrostQL detects a WordPress database:

```csharp
services.AddBifrostQL(options =>
{
    options.BindStandardConfig(configuration);
    // WordPress detection happens automatically
});
```

### Manual Configuration

For more control, configure the bundle explicitly:

```csharp
services.AddWordPressBundle(config =>
{
    config.EnableAutoDetection = true;
    config.EnablePhpSerialization = true;
    config.EnableEavFlattening = true;
    config.EnableFileStorage = true;
    config.FileStorageConfig = new StorageBucketConfig
    {
        BucketName = "/var/www/wp-content/uploads",
        ProviderType = "local"
    };
});
```

### Using Pre-defined Configurations

```csharp
// Minimal configuration (detection + FKs only)
services.AddWordPressBundle(WordPressBundleConfiguration.Minimal);

// Full-featured with file storage
var storageConfig = new StorageBucketConfig
{
    BucketName = "my-wp-uploads",
    ProviderType = "s3",
    Region = "us-east-1"
};
services.AddWordPressBundle(WordPressBundleConfiguration.FullFeatured(storageConfig));
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `EnableAutoDetection` | `true` | Enable automatic WordPress schema detection |
| `EnablePhpSerialization` | `true` | Handle PHP serialized data in meta columns |
| `EnableEavFlattening` | `true` | Enable EAV flattening for meta tables |
| `EnableFileStorage` | `false` | Enable file storage for attachments |
| `HideActionSchedulerTables` | `true` | Hide Action Scheduler tables by default |
| `InjectTableLabels` | `true` | Add friendly labels to tables |
| `InjectForeignKeys` | `true` | Inject WordPress FK relationships |
| `CustomPrefix` | `null` | Override auto-detected table prefix |
| `FileStorageConfig` | `null` | Storage configuration for file uploads |
| `AdditionalMetadata` | `null` | Custom metadata to merge with bundle output |

## WordPress Detector

The `WordPressDetector` identifies WordPress databases by looking for:

### Signature Tables (Required)
- `{prefix}users`
- `{prefix}posts`
- `{prefix}options`

### Supporting Tables (Increase Confidence)
- `{prefix}postmeta`
- `{prefix}usermeta`
- `{prefix}comments`
- `{prefix}commentmeta`
- `{prefix}terms`
- `{prefix}termmeta`
- `{prefix}term_taxonomy`
- `{prefix}term_relationships`

### Multisite Support

The detector supports WordPress multisite installations with multiple prefixes:

```
wp_users, wp_posts, wp_options       (main site)
wp_2_users, wp_2_posts, wp_2_options (site 2)
wp_3_users, wp_3_posts, wp_3_options (site 3)
```

## PHP Serialization

WordPress stores serialized PHP data in several columns. The bundle automatically:

1. Detects columns containing PHP serialized data
2. Deserializes the data when reading
3. Serializes data when writing (if needed)

### Affected Columns

- `{prefix}postmeta.meta_value`
- `{prefix}usermeta.meta_value`
- `{prefix}termmeta.meta_value`
- `{prefix}commentmeta.meta_value`
- `{prefix}options.option_value`

### Example

```graphql
query {
  wp_posts {
    ID
    post_title
    _flattened_postmeta {
      _meta  # PHP serialized data is automatically deserialized
    }
  }
}
```

## EAV Flattening

Entity-Attribute-Value (EAV) tables store flexible key-value pairs. The bundle flattens these into JSON fields on parent tables.

### Meta Tables Supported

| Meta Table | Parent Table | Foreign Key |
|------------|--------------|-------------|
| `postmeta` | `posts` | `post_id` |
| `usermeta` | `users` | `user_id` |
| `termmeta` | `terms` | `term_id` |
| `commentmeta` | `comments` | `comment_id` |

### GraphQL Schema

```graphql
type wp_posts {
  ID: Int!
  post_title: String!
  _flattened_postmeta: wp_posts_flattened_postmeta
}

type wp_posts_flattened_postmeta {
  ID: Int!
  _meta: JSON  # Contains all meta_key/meta_value pairs as JSON
}

type Query {
  database {
    wp_posts: [wp_posts!]!
    _flattened_wp_postmeta(limit: Int, offset: Int, filter: JSON): wp_posts_flattened_postmeta_paged
  }
}
```

### Query Example

```graphql
query {
  database {
    wp_posts(limit: 10) {
      ID
      post_title
      _flattened_postmeta {
        _meta
      }
    }
    _flattened_wp_postmeta(filter: { meta_key: { _eq: "featured_image" } }) {
      data {
        ID
        _meta
      }
      total
    }
  }
}
```

## File Storage

Enable file storage to handle WordPress media attachments:

```csharp
services.AddWordPressBundle(config =>
{
    config.EnableFileStorage = true;
    config.FileStorageConfig = new StorageBucketConfig
    {
        BucketName = "/var/www/wordpress/wp-content/uploads",
        ProviderType = "local",
        MaxFileSize = 10 * 1024 * 1024,  // 10MB
        AllowedMimeTypes = new[] { "image/*", "application/pdf" }
    };
});
```

### Supported Storage Providers

- `local` - Local filesystem storage
- `s3` - Amazon S3
- `azure` - Azure Blob Storage

### GraphQL Mutations

```graphql
mutation UploadAttachment($file: Upload!, $postId: Int!) {
  insert_wp_posts(
    data: {
      post_type: "attachment"
      post_parent: $postId
      _file_upload: { column: "guid", file: $file }
    }
  ) {
    ID
    guid
  }
}
```

## Foreign Key Relationships

The bundle injects these implicit foreign keys:

| Child Table | Column | Parent Table | Column |
|-------------|--------|--------------|--------|
| `posts` | `post_author` | `users` | `ID` |
| `posts` | `post_parent` | `posts` | `ID` |
| `postmeta` | `post_id` | `posts` | `ID` |
| `usermeta` | `user_id` | `users` | `ID` |
| `comments` | `comment_post_ID` | `posts` | `ID` |
| `comments` | `user_id` | `users` | `ID` |
| `commentmeta` | `comment_id` | `comments` | `comment_ID` |
| `termmeta` | `term_id` | `terms` | `term_id` |
| `term_taxonomy` | `term_id` | `terms` | `term_id` |
| `term_relationships` | `term_taxonomy_id` | `term_taxonomy` | `term_taxonomy_id` |

## Hidden Tables

Action Scheduler tables are automatically hidden from the GraphQL schema:

- `{prefix}actionscheduler_actions`
- `{prefix}actionscheduler_claims`
- `{prefix}actionscheduler_groups`
- `{prefix}actionscheduler_logs`

## Extension Methods

The bundle provides several extension methods:

```csharp
// Check if table is a WordPress meta table
bool isMeta = table.IsWordPressMetaTable();

// Check if column contains PHP serialized data
bool isSerialized = column.IsPhpSerialized();

// Get the WordPress table type (posts, users, etc.)
string? type = table.GetWordPressTableType();

// Get the table prefix
string prefix = table.GetWordPressPrefix();

// Check if table is an Action Scheduler table
bool isActionScheduler = table.IsActionSchedulerTable();
```

## Integration with BifrostQL Server

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add WordPress bundle
builder.Services.AddWordPressBundle(config =>
{
    config.EnableFileStorage = true;
    config.FileStorageConfig = new StorageBucketConfig
    {
        BucketName = builder.Configuration["WordPress:UploadPath"]!,
        ProviderType = "local"
    };
});

// Add BifrostQL
builder.Services.AddBifrostQL(options =>
{
    options.BindStandardConfig(builder.Configuration);
});

var app = builder.Build();

app.UseBifrostQL();
app.Run();
```

## Disabling WordPress Features

To disable WordPress detection entirely:

```json
{
  "BifrostQL": {
    "Metadata": [
      "* { auto-detect-app: disabled }"
    ]
  }
}
```

Or force a specific app schema:

```json
{
  "BifrostQL": {
    "Metadata": [
      "* { app-schema: none }"
    ]
  }
}
```

## Testing

The bundle includes comprehensive tests:

```bash
# Run WordPress detector tests
dotnet test --filter "FullyQualifiedName~WordPressDetectorTests"

# Run bundle tests
dotnet test --filter "FullyQualifiedName~WordPressSchemaBundleTests"
```

## Troubleshooting

### Detection Not Working

1. Ensure all three signature tables exist: `users`, `posts`, `options`
2. Check that tables share a common prefix (e.g., `wp_`)
3. Verify `auto-detect-app` is not set to `disabled`

### PHP Serialization Issues

1. Check column metadata includes `type: php_serialized`
2. Verify the column exists in the table
3. Review logs for deserialization errors

### EAV Flattening Not Working

1. Ensure EAV metadata is present: `eav-parent`, `eav-fk`, `eav-key`, `eav-value`
2. Check that parent table exists
3. Verify foreign key column exists in meta table

## See Also

- [App Schema Detection Framework](AppSchemaDetection.md) — Framework overview and architecture
- [Creating Custom Detectors](CreatingCustomDetectors.md) — Guide for building custom detectors
- [Application Schema Detection](src/content/docs/concepts/app-schema-detection.md) — Concept documentation
- [WordPress Database Guide](src/content/docs/guides/wordpress.md) — User guide for WordPress databases
- [EAV Module Documentation](EavModule.md) — Entity-Attribute-Value flattening documentation
- [File Storage Documentation](file-storage-system.md) — File upload/download documentation

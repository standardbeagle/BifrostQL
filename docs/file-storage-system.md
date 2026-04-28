# File Storage System

This document describes the file storage system implemented in BifrostQL, which allows storing files associated with database columns using configurable storage buckets.

## Overview

The file storage system provides:

- **Column-level file location tags**: Mark columns as file storage columns using metadata
- **Configurable storage buckets**: Configure storage at database, table, or column level
- **Multiple storage providers**: Local filesystem (extensible to S3, Azure, etc.)
- **GraphQL file operations**: Upload, download, and delete files via GraphQL mutations
- **Integration with BifrostFormBuilder**: Automatic file input generation for file columns

## Configuration

### Column-Level File Tags

Mark a column as a file storage column using the `file` metadata tag:

```
"dbo.users.avatar { file: type:image;maxSize:5242880;accept:image/* }"
```

Available file configuration options:
- `type`: File type category (image, document, video, audio, archive)
- `maxSize`: Maximum file size in bytes
- `accept`: Accepted MIME types (comma-separated)
- `thumbnails`: Whether to generate thumbnails (true/false)
- `sizes`: Thumbnail sizes (e.g., "100x100,300x300")
- `public`: Whether files are publicly accessible (true/false)
- `path`: Custom storage path

### Storage Bucket Configuration

Configure storage buckets at different levels:

**Database level** (applies to all tables):
```
":root { storage: bucket:my-app-files;provider:local;maxSize:10485760 }"
```

**Table level** (applies to all file columns in the table):
```
"dbo.documents { storage: bucket:documents;prefix:docs/2024 }"
```

**Column level** (highest priority):
```
"dbo.users.avatar { storage: bucket:avatars;provider:s3;region:us-east-1 }"
```

Storage configuration options:
- `bucket`/`bucketName`: The bucket name or directory path
- `provider`/`providerType`: Storage provider (local, s3, azure)
- `prefix`/`pathPrefix`: Path prefix for files
- `region`: Cloud storage region
- `endpoint`/`endpointUrl`: Custom endpoint for S3-compatible services
- `maxSize`/`maxFileSize`: Maximum file size in bytes
- `mimetypes`/`allowedMimeTypes`: Allowed MIME types

## GraphQL Operations

When file storage is enabled, the following GraphQL operations are available:

### Queries

```graphql
# Get file download URL and metadata
query {
  _fileDownload(
    table: "users"
    column: "avatar"
    recordId: "123"
    expirationMinutes: 15
  ) {
    fileKey
    originalName
    contentType
    size
    accessUrl
    uploadedAt
    expiresAt
  }
}
```

### Mutations

```graphql
# Upload a file
mutation {
  _fileUpload(
    table: "users"
    column: "avatar"
    recordId: "123"
    file: Upload!  # File upload scalar
    filename: "photo.png"
    contentType: "image/png"
  ) {
    success
    fileKey
    originalName
    contentType
    size
    accessUrl
    uploadedAt
  }
}

# Delete a file
mutation {
  _fileDelete(
    table: "users"
    column: "avatar"
    recordId: "123"
  )
}
```

## Database Schema

File metadata is stored as JSON in the database column:

```json
{
  "FileKey": "users/avatar/123_20240115143000_a1b2c3d4.png",
  "OriginalName": "photo.png",
  "ContentType": "image/png",
  "Size": 102456,
  "BucketName": "my-app-files",
  "ProviderType": "local",
  "UploadedAt": "2024-01-15T14:30:00Z",
  "AccessUrl": "/path/to/file"
}
```

## Storage Providers

### Local Storage Provider

The local storage provider stores files on the filesystem:

```csharp
var config = new StorageBucketConfig
{
    BucketName = "/var/www/uploads",
    ProviderType = "local",
    PathPrefix = "2024/documents"
};
```

### Custom Storage Providers

Implement `IStorageProvider` to add support for additional storage backends:

```csharp
public class S3StorageProvider : IStorageProvider
{
    public string ProviderType => "s3";
    
    public Task<string> UploadAsync(StorageBucketConfig config, string fileKey, byte[] content, string? contentType = null, CancellationToken ct = default)
    {
        // S3 upload implementation
    }
    
    // ... other methods
}

// Register the provider
var factory = new StorageProviderFactory();
factory.RegisterProvider(new S3StorageProvider());
```

## Form Integration

The BifrostFormBuilder automatically generates file inputs for file storage columns:

```csharp
var builder = new BifrostFormBuilder(dbModel);
var form = builder.GenerateForm("users", FormMode.Insert);
```

For file columns, this generates:

```html
<div class="form-group">
    <label for="avatar">Avatar</label>
    <input type="file" id="avatar" name="avatar" accept="image/*" data-file-storage="type:image">
</div>
```

## API Usage

### FileStorageService

The `FileStorageService` provides programmatic access to file operations:

```csharp
var service = new FileStorageService();

// Check if column is a file storage column
bool isFileColumn = service.IsFileStorageColumn(table, column, model);

// Get bucket configuration
var config = service.GetBucketConfig(table, column, model);

// Upload a file
var metadata = await service.UploadFileAsync(
    table, column, model, recordId, 
    fileContent, "photo.png", "image/png"
);

// Download a file
var content = await service.DownloadFileAsync(metadata, model);

// Get presigned URL
var url = await service.GetFileUrlAsync(metadata, model, expirationMinutes: 30);
```

## Security Considerations

1. **File size limits**: Configure `maxSize` to prevent large file uploads
2. **MIME type restrictions**: Use `mimetypes` to restrict allowed file types
3. **Path sanitization**: File keys are sanitized to prevent directory traversal
4. **Access control**: Use `public` flag and implement authorization in your application

## Examples

### Basic Image Upload

```
"dbo.users.avatar { file: type:image;accept:image/png,image/jpeg }"
"dbo.users { storage: bucket:uploads;prefix:avatars }"
```

### Documents with Size Limit

```
"dbo.documents.file_path { file: type:document;maxSize:10485760 }"
"dbo.documents { storage: bucket:documents;maxSize:20971520 }"
```

### S3-Compatible Storage

```
"dbo.media.file_path { storage: bucket:media;provider:s3;region:us-west-2;endpoint:https://minio.example.com }"
```

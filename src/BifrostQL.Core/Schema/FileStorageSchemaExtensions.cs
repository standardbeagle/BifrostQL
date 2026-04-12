namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// GraphQL schema extensions for file storage operations.
    /// Adds file upload/download queries and mutations to the schema.
    /// </summary>
    public static class FileStorageSchemaExtensions
    {
        /// <summary>
        /// Gets the GraphQL type definitions for file storage operations
        /// </summary>
        public static string GetFileStorageTypeDefinitions()
        {
            return @"
# File upload result type
type FileUploadResult {
    success: Boolean!
    fileKey: String
    originalName: String
    contentType: String
    size: Int
    accessUrl: String
    uploadedAt: String
}

# File download result type
type FileDownloadResult {
    fileKey: String
    originalName: String
    contentType: String
    size: Int
    accessUrl: String
    uploadedAt: String
    expiresAt: String
}

# File metadata stored in database columns
type FileMetadata {
    fileKey: String!
    originalName: String
    contentType: String
    size: Int!
    bucketName: String
    providerType: String
    uploadedAt: String!
    accessUrl: String
}

# Storage bucket configuration
type StorageBucketConfig {
    bucketName: String!
    providerType: String!
    pathPrefix: String
    region: String
    endpointUrl: String
    maxFileSize: Int
    allowedMimeTypes: [String]
}

# Input for file upload mutation
input FileUploadInput {
    table: String!
    column: String!
    recordId: String!
    file: Upload!
    filename: String
    contentType: String
}

# Input for file download query
input FileDownloadInput {
    table: String!
    column: String!
    recordId: String!
    expirationMinutes: Int
}
";
        }

        /// <summary>
        /// Gets the file storage query field definitions
        /// </summary>
        public static string GetFileStorageQueryFields()
        {
            return @"
    # Get file download URL and metadata
    _fileDownload(table: String!, column: String!, recordId: String!, expirationMinutes: Int): FileDownloadResult
";
        }

        /// <summary>
        /// Gets the file storage mutation field definitions
        /// </summary>
        public static string GetFileStorageMutationFields()
        {
            return @"
    # Upload a file and update the database record
    _fileUpload(table: String!, column: String!, recordId: String!, file: Upload!, filename: String, contentType: String): FileUploadResult
    
    # Delete a file from storage and clear the database record
    _fileDelete(table: String!, column: String!, recordId: String!): Boolean
";
        }

        /// <summary>
        /// Checks if file storage is enabled for the database model
        /// </summary>
        public static bool IsFileStorageEnabled(Model.IDbModel model)
        {
            // File storage is enabled if any table has storage metadata
            // or if database-level storage config exists
            var dbStorageConfig = model.GetMetadataValue("storage");
            if (!string.IsNullOrWhiteSpace(dbStorageConfig))
                return true;

            // Check if any table or column has storage config
            foreach (var table in model.Tables)
            {
                if (!string.IsNullOrWhiteSpace(table.GetMetadataValue("storage")))
                    return true;

                foreach (var column in table.Columns)
                {
                    if (!string.IsNullOrWhiteSpace(column.GetMetadataValue("storage")) ||
                        !string.IsNullOrWhiteSpace(column.GetMetadataValue("file")))
                        return true;
                }
            }

            return false;
        }
    }
}

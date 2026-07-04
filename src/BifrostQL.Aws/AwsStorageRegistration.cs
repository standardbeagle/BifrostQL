using BifrostQL.Core.Storage;

namespace BifrostQL.Aws
{
    /// <summary>
    /// Registers the AWS storage providers with BifrostQL's storage subsystem.
    /// Call <see cref="Register"/> once at application startup so the "s3"
    /// provider becomes available to <see cref="StorageProviderFactory"/>.
    /// </summary>
    public static class AwsStorageRegistration
    {
        /// <summary>The storage provider type identifier for AWS S3.</summary>
        public const string S3ProviderType = "s3";

        /// <summary>
        /// Registers the S3 storage provider so it can be resolved by provider type.
        /// </summary>
        public static void Register()
        {
            StorageProviderRegistry.Register(S3ProviderType, () => new S3StorageProvider());
        }
    }
}

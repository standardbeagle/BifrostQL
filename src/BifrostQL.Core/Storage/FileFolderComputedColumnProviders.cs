using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;

namespace BifrostQL.Core.Storage;

public abstract class FileFolderComputedColumnProviderBase : IComputedColumnProvider
{
    private static readonly Regex PlaceholderPattern = new(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private readonly StorageProviderFactory _providerFactory;

    protected FileFolderComputedColumnProviderBase(StorageProviderFactory? providerFactory = null)
    {
        _providerFactory = providerFactory ?? new StorageProviderFactory();
    }

    public abstract string Name { get; }
    protected abstract string ProviderType { get; }

    public async ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
    {
        var config = ResolveBucketConfig(context);
        config.ProviderType = ProviderType;

        var provider = _providerFactory.GetProvider(config);
        if (provider is not IStorageFolderProvider folderProvider)
            throw new InvalidOperationException($"Storage provider '{config.ProviderType}' does not support folder listing.");

        var folder = RenderFolderTemplate(context);
        var recursive = TryBool(context.Column.Options, "recursive");
        return await folderProvider.ListFolderAsync(config, folder, recursive, cancellationToken);
    }

    private StorageBucketConfig ResolveBucketConfig(ComputedColumnContext context)
    {
        var options = context.Column.Options;
        var config = options != null && options.TryGetValue("storage", out var storageRaw)
            ? StorageBucketConfig.FromMetadata(storageRaw)
            : null;

        config ??= StorageBucketConfig.FromMetadata(context.Table.GetMetadataValue(MetadataKeys.Storage.Config));
        config ??= StorageBucketConfig.FromMetadata(context.Model.GetMetadataValue(MetadataKeys.Storage.Config));
        config ??= new StorageBucketConfig();

        if (options != null)
        {
            if (options.TryGetValue("bucket", out var bucket))
                config.BucketName = bucket;
            if (options.TryGetValue("prefix", out var prefix))
                config.PathPrefix = prefix;
            if (options.TryGetValue("region", out var region))
                config.Region = region;
            if (options.TryGetValue("endpoint", out var endpoint))
                config.EndpointUrl = endpoint;
            if (options.TryGetValue("pathstyle", out var pathStyle))
                config.UsePathStyle = Utils.MetadataSwitch.ParseStrict(pathStyle, config.UsePathStyle, "pathstyle");
        }

        if (string.IsNullOrWhiteSpace(config.BucketName))
            throw new InvalidOperationException($"No storage bucket configured for file folder column '{context.Column.Name}'.");

        return config;
    }

    private static string RenderFolderTemplate(ComputedColumnContext context)
    {
        var options = context.Column.Options;
        var template = options != null && options.TryGetValue("folder", out var folder)
            ? folder
            : context.Column.Name;

        return PlaceholderPattern.Replace(template, match =>
        {
            var key = match.Groups["name"].Value;
            return context.Row.TryGetValue(key, out var value)
                ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
                : "";
        });
    }

    private static bool TryBool(IReadOnlyDictionary<string, string>? options, string key)
        => options != null
           && options.TryGetValue(key, out var raw)
           && Utils.MetadataSwitch.ParseStrict(raw, false, key);
}

public sealed class LocalFileFolderComputedColumnProvider : FileFolderComputedColumnProviderBase
{
    public LocalFileFolderComputedColumnProvider(StorageProviderFactory? providerFactory = null)
        : base(providerFactory)
    {
    }

    public override string Name => FileFolderComputedColumnCollector.LocalProviderName;
    protected override string ProviderType => "local";
}

public sealed class S3FileFolderComputedColumnProvider : FileFolderComputedColumnProviderBase
{
    public S3FileFolderComputedColumnProvider(StorageProviderFactory? providerFactory = null)
        : base(providerFactory)
    {
    }

    public override string Name => FileFolderComputedColumnCollector.S3ProviderName;
    protected override string ProviderType => "s3";
}

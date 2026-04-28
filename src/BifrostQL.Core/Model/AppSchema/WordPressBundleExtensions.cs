using Microsoft.Extensions.DependencyInjection;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.Storage;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Extension methods for registering and configuring the WordPress schema bundle.
/// </summary>
public static class WordPressBundleExtensions
{
    /// <summary>
    /// Adds WordPress schema bundle support to BifrostQL services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddWordPressBundle(
        this IServiceCollection services,
        Action<WordPressBundleConfiguration>? configure = null)
    {
        var config = WordPressBundleConfiguration.Default;
        configure?.Invoke(config);

        services.AddSingleton(config);
        services.AddSingleton<WordPressSchemaBundle>();

        // Register file storage if enabled
        if (config.EnableFileStorage && config.FileStorageConfig != null)
        {
            services.AddSingleton<FileStorageService>();
        }

        return services;
    }

    /// <summary>
    /// Adds WordPress schema bundle support with a pre-built configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The bundle configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddWordPressBundle(
        this IServiceCollection services,
        WordPressBundleConfiguration configuration)
    {
        services.AddSingleton(configuration);
        services.AddSingleton<WordPressSchemaBundle>();

        if (configuration.EnableFileStorage && configuration.FileStorageConfig != null)
        {
            services.AddSingleton<FileStorageService>();
        }

        return services;
    }

    /// <summary>
    /// Creates a WordPress schema bundle from the service provider.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <returns>The WordPress schema bundle.</returns>
    public static WordPressSchemaBundle GetWordPressBundle(this IServiceProvider services)
    {
        return services.GetRequiredService<WordPressSchemaBundle>();
    }

    /// <summary>
    /// Attempts to get the WordPress schema bundle from the service provider.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="bundle">The WordPress schema bundle if available.</param>
    /// <returns>True if the bundle was found, false otherwise.</returns>
    public static bool TryGetWordPressBundle(this IServiceProvider services, out WordPressSchemaBundle? bundle)
    {
        bundle = services.GetService<WordPressSchemaBundle>();
        return bundle != null;
    }

    /// <summary>
    /// Enables EAV flattening for WordPress meta tables using the bundle configuration.
    /// </summary>
    /// <param name="model">The database model.</param>
    /// <param name="dialect">The SQL dialect.</param>
    /// <param name="bundle">The WordPress schema bundle.</param>
    /// <returns>The EAV module integration.</returns>
    public static EavModuleIntegration? CreateEavModuleWithBundle(
        this IDbModel model,
        ISqlDialect dialect,
        WordPressSchemaBundle bundle)
    {
        if (!bundle.Configuration.EnableEavFlattening)
            return null;

        return new EavModuleIntegration(model, dialect);
    }

    /// <summary>
    /// Gets the file storage service configured for WordPress if available.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <returns>The file storage service or null if not configured.</returns>
    public static FileStorageService? GetWordPressFileStorage(this IServiceProvider services)
    {
        var bundle = services.GetService<WordPressSchemaBundle>();
        if (bundle?.Configuration.EnableFileStorage != true)
            return null;

        return services.GetService<FileStorageService>();
    }
}

/// <summary>
/// Extension methods for working with WordPress-specific metadata and configuration.
/// </summary>
public static class WordPressMetadataExtensions
{
    /// <summary>
    /// Checks if the table is a WordPress meta table (postmeta, usermeta, etc.).
    /// </summary>
    /// <param name="table">The database table.</param>
    /// <returns>True if the table is a WordPress meta table.</returns>
    public static bool IsWordPressMetaTable(this IDbTable table)
    {
        var name = table.DbName.ToLowerInvariant();
        return name.EndsWith("postmeta") ||
               name.EndsWith("usermeta") ||
               name.EndsWith("termmeta") ||
               name.EndsWith("commentmeta");
    }

    /// <summary>
    /// Checks if the table is a WordPress core table.
    /// </summary>
    /// <param name="table">The database table.</param>
    /// <returns>True if the table is a WordPress core table.</returns>
    public static bool IsWordPressCoreTable(this IDbTable table)
    {
        var name = table.DbName.ToLowerInvariant();
        var coreTables = new[] { "posts", "users", "options", "comments", "terms", "links" };
        return coreTables.Any(core => name.EndsWith($"_{core}") || name == core);
    }

    /// <summary>
    /// Checks if the column contains PHP serialized data based on metadata.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <returns>True if the column contains PHP serialized data.</returns>
    public static bool IsPhpSerialized(this ColumnDto column)
    {
        if (column.Metadata.TryGetValue("type", out var typeValue))
        {
            return "php_serialized".Equals(typeValue?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Gets the WordPress table type (posts, users, options, etc.) from the table name.
    /// </summary>
    /// <param name="table">The database table.</param>
    /// <returns>The WordPress table type or null if not recognized.</returns>
    public static string? GetWordPressTableType(this IDbTable table)
    {
        var name = table.DbName.ToLowerInvariant();
        var lastUnderscore = name.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            return name[(lastUnderscore + 1)..];
        }
        return name;
    }

    /// <summary>
    /// Checks if the table is an Action Scheduler table.
    /// </summary>
    /// <param name="table">The database table.</param>
    /// <returns>True if the table is an Action Scheduler table.</returns>
    public static bool IsActionSchedulerTable(this IDbTable table)
    {
        var name = table.DbName.ToLowerInvariant();
        return name.Contains("actionscheduler");
    }

    /// <summary>
    /// Gets the table prefix from a WordPress table name.
    /// </summary>
    /// <param name="table">The database table.</param>
    /// <returns>The prefix or empty string if no prefix found.</returns>
    public static string GetWordPressPrefix(this IDbTable table)
    {
        var name = table.DbName;
        var lastUnderscore = name.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            return name[..(lastUnderscore + 1)];
        }
        return "";
    }
}

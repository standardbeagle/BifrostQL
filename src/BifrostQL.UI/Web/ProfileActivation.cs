using System.Text.Json;
using BifrostQL.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// Per-connection profile config, deserialized from the bundled
    /// <c>&lt;schema&gt;.bifrost.json</c>. Each profile carries its own metadata rule
    /// strings and module names — its own API shape over the same database.
    /// </summary>
    public record ProfileDef
    {
        public string Name { get; init; } = "";
        public string? Label { get; init; }
        public string[]? Modules { get; init; }
        public string[]? Metadata { get; init; }
        public string? RequireRole { get; init; }
    }

    /// <summary>Root of a bundled <c>&lt;schema&gt;.bifrost.json</c> profile config.</summary>
    public record SampleConfig
    {
        public ProfileDef[]? Profiles { get; init; }
    }

    /// <summary>
    /// Rebinds the <see cref="BifrostProfileRegistry"/> from a connected database's
    /// bundled <c>&lt;schema&gt;.bifrost.json</c>: each profile carries its own metadata
    /// (rule strings) + modules and becomes a selectable API shape.
    ///
    /// Called after a connection is bound and before <c>ResetSchema</c>, so the next
    /// schema rebuild (fresh <c>ProfileModelCache</c>) sees the current profile set.
    /// An arbitrary DB (vault/direct connect) gets no named profiles — only the
    /// synthesized raw default — so we <c>Clear()</c> there.
    /// </summary>
    public static class ProfileActivation
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public static async Task RebindProfilesAsync(IServiceProvider services, string? schema)
        {
            var registry = services.GetService<BifrostProfileRegistry>();
            if (registry == null)
                return;

            var json = schema != null ? await QuickstartSchemas.LoadSampleConfig(schema) : null;
            if (json == null)
            {
                registry.Clear();
                return;
            }

            var cfg = JsonSerializer.Deserialize<SampleConfig>(json, Options);
            var defs = cfg?.Profiles ?? Array.Empty<ProfileDef>();
            registry.ReplaceAll(defs.Select(d => new BifrostProfile
            {
                Name = d.Name,
                Label = d.Label,
                Modules = d.Modules,
                Metadata = d.Metadata,
                RequireRole = d.RequireRole,
            }));
        }
    }
}

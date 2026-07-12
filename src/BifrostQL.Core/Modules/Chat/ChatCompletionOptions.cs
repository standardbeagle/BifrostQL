using System;
using Microsoft.Extensions.Configuration;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// Construction-time configuration for <see cref="AnthropicChatCompletionService"/>.
    /// Bound from the <c>BifrostQL:Chat</c> configuration section with the standard
    /// <c>ANTHROPIC_API_KEY</c> environment variable as the api-key fallback. A missing
    /// key is NOT tolerated here silently — the service constructor fails fast so a
    /// misconfigured host errors at startup wiring, not on the first user request.
    /// </summary>
    public sealed class ChatCompletionOptions
    {
        /// <summary>The configuration section the options bind from.</summary>
        public const string SectionName = "BifrostQL:Chat";

        /// <summary>The environment variable consulted when the section has no api key.</summary>
        public const string ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY";

        /// <summary>Default model — exact id string, no date suffix.</summary>
        public const string DefaultModel = "claude-opus-4-8";

        /// <summary>
        /// Default max output tokens. Sized for the always-streaming request shape
        /// (streaming has no HTTP-timeout pressure, so give the model room).
        /// </summary>
        public const int DefaultMaxTokens = 64000;

        /// <summary>The Anthropic API key. Required; validated at service construction.</summary>
        public string ApiKey { get; init; } = string.Empty;

        /// <summary>Model id used when a request does not override it.</summary>
        public string Model { get; init; } = DefaultModel;

        /// <summary>Max output tokens used when a request does not override it.</summary>
        public int MaxTokens { get; init; } = DefaultMaxTokens;

        /// <summary>
        /// Binds options from <paramref name="configuration"/>: <c>BifrostQL:Chat:ApiKey</c> /
        /// <c>Model</c> / <c>MaxTokens</c>, with <c>ANTHROPIC_API_KEY</c> as the api-key
        /// fallback and the class defaults for the rest.
        /// </summary>
        public static ChatCompletionOptions FromConfiguration(IConfiguration configuration)
        {
            if (configuration is null)
                throw new ArgumentNullException(nameof(configuration));

            var section = configuration.GetSection(SectionName);
            var apiKey = section["ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);

            var maxTokensRaw = section["MaxTokens"];
            int maxTokens;
            if (string.IsNullOrWhiteSpace(maxTokensRaw))
                maxTokens = DefaultMaxTokens;
            else if (!int.TryParse(maxTokensRaw, out maxTokens) || maxTokens <= 0)
                throw new InvalidOperationException(
                    $"'{SectionName}:MaxTokens' must be a positive integer; got '{maxTokensRaw}'.");

            return new ChatCompletionOptions
            {
                ApiKey = apiKey ?? string.Empty,
                Model = string.IsNullOrWhiteSpace(section["Model"]) ? DefaultModel : section["Model"]!.Trim(),
                MaxTokens = maxTokens,
            };
        }
    }
}

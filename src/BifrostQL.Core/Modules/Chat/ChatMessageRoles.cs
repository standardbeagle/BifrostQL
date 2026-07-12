using System;
using System.Collections.Generic;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// The closed set of message roles the chat store accepts. Roles are part of the
    /// stored conversation contract (consumers branch on them to render/replay a
    /// transcript), so an unknown role is a caller bug rejected fail-fast — never a
    /// value written through to the database.
    /// </summary>
    public static class ChatMessageRoles
    {
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string System = "system";

        /// <summary>Every accepted role, in canonical (lower-case) form.</summary>
        public static readonly IReadOnlySet<string> All =
            new HashSet<string>(StringComparer.Ordinal) { User, Assistant, System };

        /// <summary>
        /// Canonicalizes a caller-supplied role (trim + lower-case) and validates it
        /// against <see cref="All"/>. Throws <see cref="ArgumentException"/> on anything
        /// else so a typo'd role fails the append instead of landing in the table.
        /// </summary>
        public static string Normalize(string role)
        {
            if (string.IsNullOrWhiteSpace(role))
                throw new ArgumentException(
                    $"A chat message role is required; allowed roles are '{User}', '{Assistant}', '{System}'.",
                    nameof(role));

            var normalized = StringNormalizer.NormalizeName(role);
            if (!All.Contains(normalized))
                throw new ArgumentException(
                    $"Unknown chat message role '{role}'; allowed roles are '{User}', '{Assistant}', '{System}'.",
                    nameof(role));

            return normalized;
        }
    }
}

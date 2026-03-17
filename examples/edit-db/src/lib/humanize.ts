/**
 * Converts raw database names into human-readable Title Case labels.
 * Handles snake_case, camelCase, and schema-prefixed names (e.g. dbo.order_details).
 */

function stripSchemaPrefix(name: string): string {
    const dotIndex = name.lastIndexOf(".");
    return dotIndex >= 0 ? name.slice(dotIndex + 1) : name;
}

function splitWords(name: string): string[] {
    const stripped = stripSchemaPrefix(name);
    // Split on underscores, hyphens, or camelCase boundaries
    return stripped
        .replace(/([a-z])([A-Z])/g, "$1 $2")
        .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
        .split(/[_\-\s]+/)
        .filter(Boolean);
}

function titleCase(word: string): string {
    if (word.length === 0) return word;
    return word.charAt(0).toUpperCase() + word.slice(1).toLowerCase();
}

export function humanizeName(name: string): string {
    if (!name) return name;
    return splitWords(name).map(titleCase).join(" ");
}

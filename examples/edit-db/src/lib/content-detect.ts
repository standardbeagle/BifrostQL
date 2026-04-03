/** Detected content type for smart rendering */
export type ContentKind = 'json' | 'xml' | 'php-serialized' | 'binary' | 'longtext' | 'text';

const binaryDbTypes = new Set(['binary', 'varbinary', 'image', 'blob', 'tinyblob', 'mediumblob', 'longblob', 'bytea']);
const longTextDbTypes = new Set(['text', 'ntext', 'xml']);

/** Check if a dbType is a binary/blob type */
export function isBinaryDbType(dbType: string): boolean {
    return binaryDbTypes.has(dbType.toLowerCase().replace(/\(.*\)/, ''));
}

/** Check if a dbType is a long text type (text, ntext, varchar(max), xml) */
export function isLongTextDbType(dbType: string): boolean {
    const normalized = dbType.toLowerCase();
    const base = normalized.replace(/\(.*\)/, '');
    if (longTextDbTypes.has(base)) return true;
    if (normalized.includes('(max)')) return true;
    return false;
}

/** Detect content kind from a string value and optional dbType hint */
export function detectContentKind(value: string, dbType?: string): ContentKind {
    if (!value) return 'text';

    if (dbType && isBinaryDbType(dbType)) return 'binary';

    const trimmed = value.trimStart();

    // JSON detection
    if ((trimmed.startsWith('{') && trimmed.endsWith('}')) ||
        (trimmed.startsWith('[') && trimmed.endsWith(']'))) {
        try {
            JSON.parse(trimmed);
            return 'json';
        } catch { /* not valid JSON */ }
    }

    // XML detection
    if (trimmed.startsWith('<?xml') || (trimmed.startsWith('<') && trimmed.includes('</') && trimmed.endsWith('>'))) {
        return 'xml';
    }

    // PHP serialized detection: a:2:{...} s:5:"hello" i:42; O:8:"stdClass":1:{...}
    if (/^[aOsidb]:\d+[:{;]/.test(trimmed)) {
        return 'php-serialized';
    }

    // Long text heuristic
    if (value.length > 200 || value.includes('\n')) return 'longtext';

    return 'text';
}

/** Max characters to show in a table cell before truncating */
export const CELL_TRUNCATE = 80;

/** Truncate text for table cell display */
export function truncateForCell(value: string): string {
    if (value.length <= CELL_TRUNCATE) return value;
    return value.slice(0, CELL_TRUNCATE) + '...';
}

/** Format binary data (base64 string) for display */
export function formatBinaryPreview(value: string): string {
    const byteLength = Math.ceil(value.length * 3 / 4);
    if (byteLength < 1024) return `${byteLength} bytes`;
    if (byteLength < 1024 * 1024) return `${(byteLength / 1024).toFixed(1)} KB`;
    return `${(byteLength / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * Re-indents JSON text without parsing its values.
 *
 * `JSON.stringify(JSON.parse(text))` is not a formatter: it reads every number
 * into an IEEE-754 double, so `9007199254740993` comes back as
 * `9007199254740992`, `1.50` as `1.5` and `1e3` as `1000`. Seeding an editor
 * with that output and saving writes a different value than the one stored.
 *
 * This walks the text token by token and copies every string and number
 * literal verbatim; only the whitespace between tokens changes.
 */

const WHITESPACE = new Set([' ', '\t', '\n', '\r']);
const LITERAL_END = new Set([',', ':', ']', '}', ' ', '\t', '\n', '\r']);

/**
 * Returns `text` re-indented with `indent` spaces per level (0 = minified), or
 * null when `text` is not valid JSON. Number and string tokens are copied
 * byte-for-byte.
 */
export function reformatJsonText(text: string, indent: number): string | null {
    try {
        JSON.parse(text);
    } catch {
        return null;
    }
    const pad = ' '.repeat(Math.max(0, indent));
    const parts: string[] = [];
    let depth = 0;
    const newline = () => {
        if (pad) parts.push('\n', pad.repeat(depth));
    };
    const n = text.length;
    let i = 0;
    while (i < n) {
        const ch = text[i];
        if (WHITESPACE.has(ch)) {
            i++;
        } else if (ch === '"') {
            let j = i + 1;
            while (text[j] !== '"') j += text[j] === '\\' ? 2 : 1;
            parts.push(text.slice(i, j + 1));
            i = j + 1;
        } else if (ch === '{' || ch === '[') {
            const close = ch === '{' ? '}' : ']';
            let j = i + 1;
            while (WHITESPACE.has(text[j])) j++;
            if (text[j] === close) {
                parts.push(ch, close);
                i = j + 1;
            } else {
                depth++;
                parts.push(ch);
                newline();
                i++;
            }
        } else if (ch === '}' || ch === ']') {
            depth--;
            newline();
            parts.push(ch);
            i++;
        } else if (ch === ',') {
            parts.push(',');
            newline();
            i++;
        } else if (ch === ':') {
            parts.push(pad ? ': ' : ':');
            i++;
        } else {
            // number, true, false, null: copied as written.
            let j = i;
            while (j < n && !LITERAL_END.has(text[j])) j++;
            parts.push(text.slice(i, j));
            i = j;
        }
    }
    return parts.join('');
}

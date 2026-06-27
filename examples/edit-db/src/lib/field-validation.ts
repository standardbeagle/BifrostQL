import type { Column } from '../types/schema';

/**
 * Client-side field validation that mirrors the server's BifrostFormValidator
 * so the two enforce the same rules. Keeping this in one place (consumed by
 * every form field) is what prevents the client from silently diverging from
 * the server — e.g. accepting a substring match the server rejects.
 *
 * Parity notes vs BifrostFormValidator.cs:
 *  - Patterns are anchored exactly as the server does (^(?:...)$ unless the
 *    pattern is already anchored), matching the HTML5 `pattern` attribute.
 *  - inputType email/url are validated (the server checks these too).
 */

const numericParamTypes = new Set(['Int', 'Int!', 'Float', 'Float!', 'Decimal', 'Decimal!']);

function isNumericColumn(column: Column): boolean {
    return numericParamTypes.has(column.paramType);
}

/**
 * Anchors a pattern the same way the HTML5 `pattern` attribute and the server
 * validator do: wrap as ^(?:...)$ unless the author already anchored it with ^.
 */
export function anchorPattern(pattern: string): string {
    return pattern.startsWith('^') ? pattern : `^(?:${pattern})$`;
}

/**
 * Approximates System.Net.Mail.MailAddress with `Address === value`: a single
 * addr-spec, no display name, no surrounding whitespace. Not byte-identical to
 * .NET (impossible in JS) but far closer than the previous no-op.
 */
function isValidEmail(value: string): boolean {
    if (value !== value.trim() || /\s/.test(value)) return false;
    const at = value.lastIndexOf('@');
    if (at <= 0 || at === value.length - 1) return false;
    const domain = value.slice(at + 1);
    return domain.includes('.') && !domain.startsWith('.') && !domain.endsWith('.');
}

/** Mirrors Uri.TryCreate(Absolute) restricted to http/https. */
function isValidUrl(value: string): boolean {
    try {
        const url = new URL(value);
        return url.protocol === 'http:' || url.protocol === 'https:';
    } catch {
        return false;
    }
}

/**
 * Validates a single value against a column's schema constraints. Returns an
 * error message, or undefined when valid. `isRequired` is passed explicitly so
 * callers keep their own required policy (forms use !isNullable).
 */
export function validateFieldValue(
    column: Column,
    value: unknown,
    isRequired: boolean,
): string | undefined {
    const label = column.label;

    if (isRequired && (value === undefined || value === null || value === '')) {
        return `${label} is required`;
    }

    // Empty optional fields skip the remaining checks (matches the server).
    if (value === undefined || value === null || value === '') {
        return undefined;
    }

    if (typeof value === 'string') {
        if (column.pattern) {
            let regex: RegExp;
            try {
                regex = new RegExp(anchorPattern(column.pattern));
            } catch {
                return `${label} has an invalid validation pattern`;
            }
            if (!regex.test(value)) {
                return column.patternMessage || `${label} format is invalid`;
            }
        }

        if (column.minLength && value.length < column.minLength) {
            return `${label} must be at least ${column.minLength} characters`;
        }

        if (column.maxLength && value.length > column.maxLength) {
            return `${label} must be at most ${column.maxLength} characters`;
        }

        if (column.inputType === 'email' && !isValidEmail(value)) {
            return 'Invalid email address';
        }

        if (column.inputType === 'url' && !isValidUrl(value)) {
            return 'Invalid URL';
        }
    }

    if (isNumericColumn(column)) {
        const numValue = Number(value);
        if (!Number.isNaN(numValue)) {
            if (column.min !== undefined && column.min !== null && numValue < column.min) {
                return `${label} must be at least ${column.min}`;
            }
            if (column.max !== undefined && column.max !== null && numValue > column.max) {
                return `${label} must be at most ${column.max}`;
            }
        }
    }

    return undefined;
}

import type { Column } from '../types/schema';
import { baseParamType, isIntegerScalar, isNumericScalar } from './scalar-types';

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

function isNumericColumn(column: Column): boolean {
    return isNumericScalar(column.paramType);
}

/**
 * Int columns reject a fractional value. Mirrors coerceNumericValue's `Int` branch
 * in useTableMutation, so the form refuses what the write path would refuse —
 * without that parity the value passes validation and dies silently at Save.
 */
function isIntegerColumn(column: Column): boolean {
    return isIntegerScalar(column.paramType);
}

/**
 * Whole-number bounds per GraphQL scalar — what the server's input type (and,
 * via the dialect type mapper, the engine) will refuse. BigInt is handled
 * separately as text because its range exceeds a JS number.
 */
const INTEGER_SCALAR_BOUNDS: Record<string, { min: number; max: number }> = {
    Int: { min: -2147483648, max: 2147483647 },
    Short: { min: -32768, max: 32767 },
    Byte: { min: 0, max: 255 },
};

const BIGINT_MIN = -(2n ** 63n);
const BIGINT_MAX = 2n ** 63n - 1n;

/**
 * The temporal family of a column, from its DATABASE type (mirrors the server's
 * ValidationRules.TemporalKindOf) — paramType can't carry this, because
 * temporal mutation inputs are String on the wire.
 */
export function temporalKindOf(column: Column): 'datetime' | 'date' | 'time' | undefined {
    const dbType = (column.dbType ?? '').toLowerCase().trim();
    const base = dbType.includes('(') ? dbType.slice(0, dbType.indexOf('(')).trimEnd() : dbType;
    if (base === 'datetimeoffset' || base === 'datetime' || base === 'datetime2'
        || base === 'smalldatetime' || base.startsWith('timestamp')) return 'datetime';
    if (base === 'date') return 'date';
    if (base.startsWith('time')) return 'time';
    return undefined;
}

/**
 * Counts the digits before the decimal point of a plain decimal text form.
 * Exponent forms are left to the server (the form inputs never produce them).
 */
function integerDigitCount(text: string): number | undefined {
    const match = /^[+-]?(\d*)(?:\.\d*)?$/.exec(text.trim());
    if (!match) return undefined;
    const digits = match[1].replace(/^0+/, '');
    return digits.length;
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

    // Temporal columns travel as String on the wire, so nothing else has proven
    // the text parses — mirror the server's schema-derived check and refuse
    // unparseable dates here, where the field can show the message.
    const temporalKind = temporalKindOf(column);
    if (temporalKind && typeof value === 'string') {
        const parseable = temporalKind === 'time'
            ? /^\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(value.trim())
            : !Number.isNaN(new Date(value).getTime());
        if (!parseable) {
            const noun = temporalKind === 'datetime' ? 'date/time' : temporalKind;
            return `${label} must be a valid ${noun}`;
        }
    }

    // BigInt travels as text to survive above 2^53; bound it as text too.
    if (baseParamType(column.paramType) === 'BigInt' && typeof value === 'string' && value.trim() !== '') {
        const text = value.trim();
        if (!/^[+-]?\d+$/.test(text)) {
            return `${label} must be a whole number`;
        }
        const big = BigInt(text);
        if (big < BIGINT_MIN || big > BIGINT_MAX) {
            return `${label} is out of range for a 64-bit integer`;
        }
        return undefined;
    }

    // Declared decimal precision: the integer part every engine refuses to
    // overflow. Excess fractional digits round on write and are allowed —
    // matching the server's ValidateDecimalPrecision.
    if (column.precision != null && (typeof value === 'string' || typeof value === 'number')) {
        const integerDigits = Math.max(column.precision - (column.scale ?? 0), 0);
        const digits = integerDigitCount(String(value));
        if (digits !== undefined && digits > integerDigits) {
            return `${label} must have at most ${integerDigits} digits before the decimal point`;
        }
    }

    if (isNumericColumn(column)) {
        const numValue = Number(value);
        // Well-formedness first. Previously an ill-formed number just skipped the
        // bound checks and reported nothing, so the value reached the write path
        // where coerceNumericValue threw — and that throw happens before the
        // mutation is ever handed to react-query, so Save silently did nothing.
        // Catching it here turns it into ordinary per-field feedback.
        if (!Number.isFinite(numValue)) {
            return `${label} must be a number`;
        }
        if (isIntegerColumn(column) && !Number.isInteger(numValue)) {
            return `${label} must be a whole number`;
        }
        // Scalar range: what the server's Int!/Short!/Byte! input (and the
        // engine's column type) will refuse — caught here so the field shows
        // the bound instead of Save dying on a wire error.
        const bounds = INTEGER_SCALAR_BOUNDS[baseParamType(column.paramType)];
        if (bounds && (numValue < bounds.min || numValue > bounds.max)) {
            return `${label} must be between ${bounds.min} and ${bounds.max}`;
        }
        if (column.min !== undefined && column.min !== null && numValue < column.min) {
            return `${label} must be at least ${column.min}`;
        }
        if (column.max !== undefined && column.max !== null && numValue > column.max) {
            return `${label} must be at most ${column.max}`;
        }
    }

    return undefined;
}

/**
 * Validates every value PRESENT in a mutation payload against its column's
 * schema rules — the pre-send gate the mutation hooks run so EVERY write path
 * (forms, programmatic callers, future surfaces) is checked, not only fields a
 * form happened to attach validators to. Absent/cleared values are skipped:
 * required-ness is form policy (a NOT NULL column with a DB default is legal to
 * omit on insert), and this gate must never refuse what the server accepts.
 * Returns all failures, or an empty array when the payload is clean.
 */
export function validateRowValues(
    columns: readonly Column[],
    detail: Record<string, unknown>,
): string[] {
    const errors: string[] = [];
    for (const column of columns) {
        const value = detail[column.name];
        if (value === undefined || value === null || value === '') continue;
        const error = validateFieldValue(column, value, false);
        if (error) errors.push(error);
    }
    return errors;
}

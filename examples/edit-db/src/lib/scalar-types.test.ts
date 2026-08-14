import { describe, expect, it } from 'vitest';
import {
    baseParamType,
    isBooleanScalar,
    isExactScalar,
    isIntegerScalar,
    isNumericScalar,
    isStringScalar,
    isTimestampScalar,
    parseScalarNumber,
} from './scalar-types';
import { coerceForGql } from './fk';
import { validateFieldValue } from './field-validation';
import type { Column } from '../types/schema';

// Every scalar the server's type mappers can emit. A type this module fails to
// classify is not a loud failure anywhere — it quietly loses its filter control,
// its numeric validation, and its write coercion — so the whole set is pinned here.
const ALL_SCALARS = [
    'String', 'Int', 'Short', 'Byte', 'BigInt', 'Decimal', 'Float',
    'Boolean', 'DateTime', 'DateTimeOffset', 'JSON',
] as const;

describe('baseParamType', () => {
    it('strips the non-null marker', () => {
        expect(baseParamType('Int!')).toBe('Int');
        expect(baseParamType('Int')).toBe('Int');
        expect(baseParamType(undefined)).toBe('');
    });
});

describe('scalar classification', () => {
    it('claims every scalar a type mapper can emit', () => {
        const unclaimed = ALL_SCALARS.filter((t) =>
            !isNumericScalar(t) && !isStringScalar(t) && !isBooleanScalar(t) && !isTimestampScalar(t) && t !== 'JSON');
        expect(unclaimed).toEqual([]);
    });

    it('counts smallint, tinyint and bigint as whole numbers', () => {
        for (const t of ['Int', 'Short', 'Byte', 'BigInt']) {
            expect(isIntegerScalar(t)).toBe(true);
            expect(isNumericScalar(t)).toBe(true);
        }
        expect(isIntegerScalar('Float')).toBe(false);
        expect(isIntegerScalar('Decimal')).toBe(false);
    });

    it('marks only the scalars a double cannot hold as exact', () => {
        expect(isExactScalar('BigInt')).toBe(true);
        expect(isExactScalar('Decimal!')).toBe(true);
        expect(isExactScalar('Int')).toBe(false);
        expect(isExactScalar('Float')).toBe(false);
    });

    it('treats both timestamp scalars alike', () => {
        expect(isTimestampScalar('DateTime')).toBe(true);
        expect(isTimestampScalar('DateTimeOffset')).toBe(true);
    });

    it('applies the non-null form of every scalar the same way', () => {
        for (const t of ALL_SCALARS) {
            expect(isNumericScalar(`${t}!`)).toBe(isNumericScalar(t));
            expect(isExactScalar(`${t}!`)).toBe(isExactScalar(t));
            expect(isTimestampScalar(`${t}!`)).toBe(isTimestampScalar(t));
        }
    });
});

describe('coerceForGql', () => {
    // Short/Byte are declared as their own scalars, which reject a string the way
    // Int does. They used to fall through to the String branch, so a smallint key
    // was sent as text and the server refused the whole document.
    it('sends every whole-number scalar as a number', () => {
        expect(coerceForGql('6', 'Short')).toBe(6);
        expect(coerceForGql('1', 'Byte')).toBe(1);
        expect(coerceForGql('42', 'Int')).toBe(42);
    });

    // The opposite rule, and for the opposite reason: through a JS number
    // 9007199254740993 becomes ...992 and the key names a different row.
    it('keeps a bigint or decimal value as text', () => {
        expect(coerceForGql('9007199254740993', 'BigInt')).toBe('9007199254740993');
        expect(coerceForGql('19.99', 'Decimal')).toBe('19.99');
    });

    it('still coerces Float and Boolean as before', () => {
        expect(coerceForGql('1.5', 'Float')).toBe(1.5);
        expect(coerceForGql('true', 'Boolean')).toBe(true);
        expect(coerceForGql('false', 'Boolean')).toBe(false);
    });

    it('passes unparseable numeric text through so the server reports the real error', () => {
        expect(coerceForGql('abc', 'Int')).toBe('abc');
    });
});

function numericColumn(paramType: string, extra: Partial<Column> = {}): Column {
    return {
        dbName: 'qty', graphQlName: 'qty', name: 'qty', label: 'Qty',
        paramType, dbType: 'int',
        isPrimaryKey: false, isIdentity: false, isNullable: true, isReadOnly: false,
        metadata: {}, ...extra,
    } as Column;
}

describe('field validation over every numeric scalar', () => {
    it('rejects non-numeric text on smallint, tinyint and bigint columns', () => {
        for (const t of ['Short', 'Byte', 'BigInt', 'Int', 'Float', 'Decimal']) {
            expect(validateFieldValue(numericColumn(t), 'abc', false)).not.toBeUndefined();
        }
    });

    it('rejects a fractional value on every whole-number scalar', () => {
        for (const t of ['Int', 'Short', 'Byte', 'BigInt']) {
            expect(validateFieldValue(numericColumn(t), '1.5', false)).not.toBeUndefined();
        }
        // Fractional scalars must still accept one.
        expect(validateFieldValue(numericColumn('Decimal'), '1.5', false)).toBeUndefined();
        expect(validateFieldValue(numericColumn('Float'), '1.5', false)).toBeUndefined();
    });

    it('enforces min and max on the newly recognized numeric scalars', () => {
        const col = numericColumn('Short', { min: 1, max: 10 });
        expect(validateFieldValue(col, '0', false)).not.toBeUndefined();
        expect(validateFieldValue(col, '11', false)).not.toBeUndefined();
        expect(validateFieldValue(col, '5', false)).toBeUndefined();
    });
});

describe('parseScalarNumber', () => {
    it('keeps exact scalars textual and everything else numeric', () => {
        expect(parseScalarNumber('9007199254740993', 'BigInt')).toBe('9007199254740993');
        expect(parseScalarNumber('42', 'Short')).toBe(42);
        expect(parseScalarNumber('1.5', 'Float')).toBe(1.5);
    });

    it('returns null for input that is not a complete numeric literal', () => {
        expect(parseScalarNumber('', 'Int')).toBeNull();
        expect(parseScalarNumber('-', 'Int')).toBeNull();
        expect(parseScalarNumber('1.5', 'Int')).toBeNull();
    });
});

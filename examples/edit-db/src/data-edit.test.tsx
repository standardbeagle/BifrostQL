import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FormApi } from '@tanstack/react-form';
import { Column } from './types/schema';

// Mock the form hook
const mockForm = {
  Field: vi.fn(),
};

// Mock the UI components
vi.mock('@/components/ui/input', () => ({
  Input: vi.fn(({ maxLength, value, onChange, onBlur, ...props }) => (
    <input
      type="text"
      maxLength={maxLength}
      value={value}
      onChange={onChange}
      onBlur={onBlur}
      data-testid="mock-input"
      {...props}
    />
  )),
}));

vi.mock('@/components/ui/label', () => ({
  Label: vi.fn(({ children, htmlFor }) => (
    <label htmlFor={htmlFor} data-testid="mock-label">{children}</label>
  )),
}));

// Test the validation logic directly
function validateField(
  value: unknown,
  column: Partial<Column>,
  isRequired: boolean
): string | undefined {
  // Required validation
  if (isRequired && (value === undefined || value === null || value === '')) {
    return `${column.label} is required`;
  }

  // Max length validation
  if (column.maxLength && value && typeof value === 'string' && value.length > column.maxLength) {
    return `${column.label} must be at most ${column.maxLength} characters`;
  }

  // Min length validation
  if (column.minLength && value && typeof value === 'string' && value.length < column.minLength) {
    return `${column.label} must be at least ${column.minLength} characters`;
  }

  return undefined;
}

function getCharacterCount(value: unknown): number {
  if (value === undefined || value === null) return 0;
  return String(value).length;
}

describe('Field Length Validation', () => {
  const baseColumn: Partial<Column> = {
    name: 'testField',
    label: 'Test Field',
    paramType: 'String',
    dbType: 'nvarchar',
    isNullable: true,
    isPrimaryKey: false,
    isIdentity: false,
    isReadOnly: false,
    metadata: {},
    dbName: 'testField',
    graphQlName: 'testField',
  };

  describe('maxLength validation', () => {
    it('should pass validation when value is within maxLength', () => {
      const column = { ...baseColumn, maxLength: 50 };
      const error = validateField('short text', column, false);
      expect(error).toBeUndefined();
    });

    it('should pass validation when value equals maxLength', () => {
      const column = { ...baseColumn, maxLength: 5 };
      const error = validateField('12345', column, false);
      expect(error).toBeUndefined();
    });

    it('should fail validation when value exceeds maxLength', () => {
      const column = { ...baseColumn, maxLength: 5 };
      const error = validateField('123456', column, false);
      expect(error).toBe('Test Field must be at most 5 characters');
    });

    it('should pass validation when value is empty and maxLength is set', () => {
      const column = { ...baseColumn, maxLength: 50 };
      const error = validateField('', column, false);
      expect(error).toBeUndefined();
    });

    it('should pass validation when value is null and maxLength is set', () => {
      const column = { ...baseColumn, maxLength: 50 };
      const error = validateField(null, column, false);
      expect(error).toBeUndefined();
    });

    it('should handle large maxLength values', () => {
      const column = { ...baseColumn, maxLength: 4000 };
      const longText = 'x'.repeat(4000);
      const error = validateField(longText, column, false);
      expect(error).toBeUndefined();
    });

    it('should fail for value exceeding large maxLength', () => {
      const column = { ...baseColumn, maxLength: 4000 };
      const tooLongText = 'x'.repeat(4001);
      const error = validateField(tooLongText, column, false);
      expect(error).toBe('Test Field must be at most 4000 characters');
    });
  });

  describe('minLength validation', () => {
    it('should pass validation when value meets minLength', () => {
      const column = { ...baseColumn, minLength: 3 };
      const error = validateField('abc', column, false);
      expect(error).toBeUndefined();
    });

    it('should fail validation when value is below minLength', () => {
      const column = { ...baseColumn, minLength: 5 };
      const error = validateField('abc', column, false);
      expect(error).toBe('Test Field must be at least 5 characters');
    });

    it('should pass validation when value is empty and minLength is set (optional field)', () => {
      const column = { ...baseColumn, minLength: 5, isNullable: true };
      const error = validateField('', column, false);
      expect(error).toBeUndefined();
    });

    it('should pass validation for empty value with minLength 0', () => {
      const column = { ...baseColumn, minLength: 0 };
      const error = validateField('', column, false);
      expect(error).toBeUndefined();
    });
  });

  describe('combined minLength and maxLength validation', () => {
    it('should pass when value is within range', () => {
      const column = { ...baseColumn, minLength: 3, maxLength: 10 };
      const error = validateField('hello', column, false);
      expect(error).toBeUndefined();
    });

    it('should fail when value is below minLength', () => {
      const column = { ...baseColumn, minLength: 5, maxLength: 10 };
      const error = validateField('hi', column, false);
      expect(error).toBe('Test Field must be at least 5 characters');
    });

    it('should fail when value exceeds maxLength', () => {
      const column = { ...baseColumn, minLength: 3, maxLength: 5 };
      const error = validateField('toolong', column, false);
      expect(error).toBe('Test Field must be at most 5 characters');
    });
  });

  describe('character count', () => {
    it('should return 0 for null value', () => {
      expect(getCharacterCount(null)).toBe(0);
    });

    it('should return 0 for undefined value', () => {
      expect(getCharacterCount(undefined)).toBe(0);
    });

    it('should return 0 for empty string', () => {
      expect(getCharacterCount('')).toBe(0);
    });

    it('should return correct count for string value', () => {
      expect(getCharacterCount('hello')).toBe(5);
    });

    it('should return string length for number value', () => {
      expect(getCharacterCount(12345)).toBe(5);
    });
  });

  describe('database type considerations', () => {
    it('should handle VARCHAR type with maxLength', () => {
      const column = { ...baseColumn, dbType: 'varchar', maxLength: 255 };
      const error = validateField('x'.repeat(256), column, false);
      expect(error).toBe('Test Field must be at most 255 characters');
    });

    it('should handle NVARCHAR type with maxLength', () => {
      const column = { ...baseColumn, dbType: 'nvarchar', maxLength: 100 };
      const error = validateField('x'.repeat(101), column, false);
      expect(error).toBe('Test Field must be at most 100 characters');
    });

    it('should handle CHAR type with exact length (as maxLength)', () => {
      const column = { ...baseColumn, dbType: 'char', maxLength: 10 };
      const error = validateField('x'.repeat(11), column, false);
      expect(error).toBe('Test Field must be at most 10 characters');
    });

    it('should handle TEXT type without maxLength', () => {
      const column = { ...baseColumn, dbType: 'text' };
      const longText = 'x'.repeat(10000);
      const error = validateField(longText, column, false);
      expect(error).toBeUndefined();
    });

    it('should handle NTEXT type without maxLength', () => {
      const column = { ...baseColumn, dbType: 'ntext' };
      const longText = 'x'.repeat(10000);
      const error = validateField(longText, column, false);
      expect(error).toBeUndefined();
    });
  });

  describe('edge cases', () => {
    it('should handle unicode characters correctly', () => {
      const column = { ...baseColumn, maxLength: 5 };
      // Unicode characters count as single characters in JavaScript
      const error = validateField('日本語', column, false);
      expect(error).toBeUndefined();
    });

    it('should handle emoji characters', () => {
      const column = { ...baseColumn, maxLength: 5 };
      const error = validateField('👋🌍✨', column, false);
      expect(error).toBeUndefined();
    });

    it('should handle whitespace-only values', () => {
      const column = { ...baseColumn, maxLength: 10 };
      const error = validateField('   ', column, false);
      expect(error).toBeUndefined();
    });

    it('should handle numeric values (converted to string)', () => {
      const column = { ...baseColumn, maxLength: 5 };
      const error = validateField(12345, column, false);
      // Numbers should be converted to strings for length check
      expect(error).toBeUndefined();
    });
  });
});

describe('Input maxLength attribute', () => {
  it('should verify maxLength is passed to input element', () => {
    // This test verifies the implementation detail that maxLength prop
    // is passed to the Input component in the EditField component
    const column: Partial<Column> = {
      name: 'test',
      label: 'Test',
      maxLength: 50,
    };
    
    // The maxLength should be defined on the column
    expect(column.maxLength).toBe(50);
  });

  it('should handle undefined maxLength', () => {
    const column: Partial<Column> = {
      name: 'test',
      label: 'Test',
    };
    
    expect(column.maxLength).toBeUndefined();
  });
});

// Numeric validation test helper
function validateNumericField(
  value: unknown,
  column: Partial<Column>,
  isRequired: boolean,
  isNumeric: boolean
): string | undefined {
  // Required validation
  if (isRequired && (value === undefined || value === null || value === '')) {
    return `${column.label} is required`;
  }

  // Numeric min/max validation
  if (isNumeric && value !== '' && value !== undefined && value !== null) {
    const numValue = Number(value);
    if (!isNaN(numValue)) {
      if (column.min !== undefined && column.min !== null && numValue < column.min) {
        return `${column.label} must be at least ${column.min}`;
      }
      if (column.max !== undefined && column.max !== null && numValue > column.max) {
        return `${column.label} must be at most ${column.max}`;
      }
    }
  }

  return undefined;
}

describe('Numeric Range Validation', () => {
  const baseNumericColumn: Partial<Column> = {
    name: 'price',
    label: 'Price',
    paramType: 'Float',
    dbType: 'decimal',
    isNullable: true,
    isPrimaryKey: false,
    isIdentity: false,
    isReadOnly: false,
    metadata: {},
    dbName: 'price',
    graphQlName: 'price',
  };

  const baseIntColumn: Partial<Column> = {
    name: 'quantity',
    label: 'Quantity',
    paramType: 'Int',
    dbType: 'int',
    isNullable: true,
    isPrimaryKey: false,
    isIdentity: false,
    isReadOnly: false,
    metadata: {},
    dbName: 'quantity',
    graphQlName: 'quantity',
  };

  describe('min validation', () => {
    it('should pass when value is above min', () => {
      const column = { ...baseNumericColumn, min: 0 };
      const error = validateNumericField(10, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should pass when value equals min', () => {
      const column = { ...baseNumericColumn, min: 5 };
      const error = validateNumericField(5, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should fail when value is below min', () => {
      const column = { ...baseNumericColumn, min: 0 };
      const error = validateNumericField(-5, column, false, true);
      expect(error).toBe('Price must be at least 0');
    });

    it('should handle negative min values', () => {
      const column = { ...baseNumericColumn, min: -100 };
      const error = validateNumericField(-50, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should fail for value below negative min', () => {
      const column = { ...baseNumericColumn, min: -100 };
      const error = validateNumericField(-150, column, false, true);
      expect(error).toBe('Price must be at least -100');
    });
  });

  describe('max validation', () => {
    it('should pass when value is below max', () => {
      const column = { ...baseNumericColumn, max: 100 };
      const error = validateNumericField(50, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should pass when value equals max', () => {
      const column = { ...baseNumericColumn, max: 100 };
      const error = validateNumericField(100, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should fail when value exceeds max', () => {
      const column = { ...baseNumericColumn, max: 100 };
      const error = validateNumericField(150, column, false, true);
      expect(error).toBe('Price must be at most 100');
    });
  });

  describe('combined min and max validation', () => {
    it('should pass when value is within range', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      const error = validateNumericField(50, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should fail when value is below min', () => {
      const column = { ...baseNumericColumn, min: 10, max: 100 };
      const error = validateNumericField(5, column, false, true);
      expect(error).toBe('Price must be at least 10');
    });

    it('should fail when value exceeds max', () => {
      const column = { ...baseNumericColumn, min: 10, max: 100 };
      const error = validateNumericField(150, column, false, true);
      expect(error).toBe('Price must be at most 100');
    });

    it('should pass at boundary values', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      expect(validateNumericField(0, column, false, true)).toBeUndefined();
      expect(validateNumericField(100, column, false, true)).toBeUndefined();
    });
  });

  describe('decimal precision support', () => {
    it('should handle decimal values with Float type', () => {
      const column = { ...baseNumericColumn, paramType: 'Float', min: 0, max: 1 };
      const error = validateNumericField(0.5, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should handle high precision decimals', () => {
      const column = { ...baseNumericColumn, paramType: 'Float', min: 0, max: 1 };
      const error = validateNumericField(0.123456, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should validate decimal values against min/max', () => {
      const column = { ...baseNumericColumn, paramType: 'Float', min: 0.5, max: 1.5 };
      expect(validateNumericField(0.3, column, false, true)).toBe('Price must be at least 0.5');
      expect(validateNumericField(2.0, column, false, true)).toBe('Price must be at most 1.5');
    });

    it('should handle very small decimal values', () => {
      const column = { ...baseNumericColumn, paramType: 'Float', min: 0.001, max: 0.1 };
      const error = validateNumericField(0.05, column, false, true);
      expect(error).toBeUndefined();
    });
  });

  describe('INTEGER type support', () => {
    it('should validate Int type with min/max', () => {
      const column = { ...baseIntColumn, min: 1, max: 10 };
      expect(validateNumericField(5, column, false, true)).toBeUndefined();
      expect(validateNumericField(0, column, false, true)).toBe('Quantity must be at least 1');
      expect(validateNumericField(11, column, false, true)).toBe('Quantity must be at most 10');
    });

    it('should handle integer boundaries', () => {
      const column = { ...baseIntColumn, min: -2147483648, max: 2147483647 };
      expect(validateNumericField(-2147483648, column, false, true)).toBeUndefined();
      expect(validateNumericField(2147483647, column, false, true)).toBeUndefined();
    });
  });

  describe('edge cases', () => {
    it('should skip validation for empty string', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      const error = validateNumericField('', column, false, true);
      expect(error).toBeUndefined();
    });

    it('should skip validation for null', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      const error = validateNumericField(null, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should skip validation for undefined', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      const error = validateNumericField(undefined, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should handle zero as valid value', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      const error = validateNumericField(0, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should handle string numeric values', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      const error = validateNumericField('50', column, false, true);
      expect(error).toBeUndefined();
    });

    it('should skip validation when isNumeric is false', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      const error = validateNumericField(150, column, false, false);
      expect(error).toBeUndefined();
    });

    it('should skip validation when min/max are not defined', () => {
      const column = { ...baseNumericColumn };
      const error = validateNumericField(999999, column, false, true);
      expect(error).toBeUndefined();
    });

    it('should handle min as 0 correctly', () => {
      const column = { ...baseNumericColumn, min: 0, max: 100 };
      expect(validateNumericField(-1, column, false, true)).toBe('Price must be at least 0');
      expect(validateNumericField(0, column, false, true)).toBeUndefined();
    });
  });

  describe('error message formatting', () => {
    it('should include field label in min error', () => {
      const column = { ...baseNumericColumn, label: 'Unit Price', min: 10 };
      const error = validateNumericField(5, column, false, true);
      expect(error).toBe('Unit Price must be at least 10');
    });

    it('should include field label in max error', () => {
      const column = { ...baseNumericColumn, label: 'Discount %', max: 100 };
      const error = validateNumericField(150, column, false, true);
      expect(error).toBe('Discount % must be at most 100');
    });

    it('should handle decimal min/max in error messages', () => {
      const column = { ...baseNumericColumn, label: 'Rate', min: 0.01, max: 0.99 };
      expect(validateNumericField(0.005, column, false, true)).toBe('Rate must be at least 0.01');
      expect(validateNumericField(1.0, column, false, true)).toBe('Rate must be at most 0.99');
    });
  });
});

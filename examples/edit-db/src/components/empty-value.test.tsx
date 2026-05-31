import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { EmptyValue, isEmptyValue, renderScalarValue } from './empty-value';

describe('isEmptyValue', () => {
    it('treats null, undefined, and empty string as empty', () => {
        expect(isEmptyValue(null)).toBe(true);
        expect(isEmptyValue(undefined)).toBe(true);
        expect(isEmptyValue('')).toBe(true);
    });

    it('treats 0, false, and non-empty strings as non-empty', () => {
        expect(isEmptyValue(0)).toBe(false);
        expect(isEmptyValue(false)).toBe(false);
        expect(isEmptyValue('x')).toBe(false);
    });
});

describe('EmptyValue', () => {
    it('renders NULL label for null kind', () => {
        render(<EmptyValue kind="null" />);
        expect(screen.getByText('NULL')).toBeInTheDocument();
    });

    it('renders empty label for empty kind', () => {
        render(<EmptyValue kind="empty" />);
        expect(screen.getByText('empty')).toBeInTheDocument();
    });
});

describe('renderScalarValue', () => {
    it('renders NULL placeholder for null/undefined', () => {
        const { rerender } = render(<>{renderScalarValue(null)}</>);
        expect(screen.getByText('NULL')).toBeInTheDocument();
        rerender(<>{renderScalarValue(undefined)}</>);
        expect(screen.getByText('NULL')).toBeInTheDocument();
    });

    it('renders empty placeholder for empty string', () => {
        render(<>{renderScalarValue('')}</>);
        expect(screen.getByText('empty')).toBeInTheDocument();
    });

    it('renders the string form for non-empty values', () => {
        const { rerender } = render(<>{renderScalarValue(0)}</>);
        expect(screen.getByText('0')).toBeInTheDocument();
        rerender(<>{renderScalarValue('hello')}</>);
        expect(screen.getByText('hello')).toBeInTheDocument();
    });
});

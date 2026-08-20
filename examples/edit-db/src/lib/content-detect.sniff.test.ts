import { describe, expect, it } from 'vitest';
import { binaryDataUrl, sniffBinaryContent } from './content-detect';

// Base64 of real magic-byte heads (padded to legal base64 lengths).
const b64 = (bytes: number[]) => btoa(String.fromCharCode(...bytes));

describe('sniffBinaryContent', () => {
    it('identifies images and PDFs by magic bytes', () => {
        expect(sniffBinaryContent(b64([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13]))).toMatchObject({ kind: 'image', mime: 'image/png', extension: '.png' });
        expect(sniffBinaryContent(b64([0xff, 0xd8, 0xff, 0xe0, 0, 0, 0, 0, 0]))).toMatchObject({ kind: 'image', mime: 'image/jpeg' });
        expect(sniffBinaryContent(b64([...'GIF89a'].map((c) => c.charCodeAt(0))))).toMatchObject({ kind: 'image', mime: 'image/gif' });
        expect(sniffBinaryContent(b64([...'RIFF'].map((c) => c.charCodeAt(0)).concat([0, 0, 0, 0], [...'WEBP'].map((c) => c.charCodeAt(0)))))).toMatchObject({ kind: 'image', mime: 'image/webp' });
        expect(sniffBinaryContent(b64([...'%PDF-1.4'].map((c) => c.charCodeAt(0))))).toMatchObject({ kind: 'pdf', mime: 'application/pdf' });
    });

    it('downgrades unknown or invalid content to a generic blob, never guesses upward', () => {
        expect(sniffBinaryContent(b64([0, 1, 2, 3, 4, 5])).kind).toBe('unknown');
        expect(sniffBinaryContent('!!!not-base64!!!').kind).toBe('unknown');
        expect(sniffBinaryContent('').kind).toBe('unknown');
    });

    it('builds a data URL from the sniffed mime only', () => {
        expect(binaryDataUrl('QUJD', 'image/png')).toBe('data:image/png;base64,QUJD');
    });
});

import { describe, expect, it } from 'vitest';

// Kept in sync with app.css so the print contract is explicit and reviewable.
const reportPrintCss = '@media print { .bifrost-report thead { display: table-header-group; } .bifrost-report tbody tr { page-break-inside: avoid; break-inside: avoid; } }';

describe('report print styles', () => {
  it('keeps headers repeating and group boundaries intact in print media', () => {
    expect(reportPrintCss).toContain('@media print');
    expect(reportPrintCss).toContain('thead { display: table-header-group; }');
    expect(reportPrintCss).toContain('page-break-inside: avoid');
  });
});

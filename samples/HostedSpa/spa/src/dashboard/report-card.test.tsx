import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PathProvider } from '@standardbeagle/virtual-router';
import { ReportCard } from './report-card';

/**
 * `ReportCard` is a pure presentational component — label + value, plus an
 * optional link to a matching report screen. These tests cover its three
 * states (value, loading, optional link) in isolation; the data-fetching and
 * permission-gating live in {@link import('./dashboard').Dashboard} and are
 * covered by `dashboard.test.tsx`.
 */
describe('ReportCard', () => {
  it('renders its label and value', () => {
    // Arrange + Act
    render(<ReportCard testId="members-card" label="Members" value={42} />);

    // Assert
    const card = screen.getByTestId('members-card');
    expect(card).toHaveTextContent('Members');
    expect(card).toHaveTextContent('42');
  });

  it('renders a loading placeholder instead of the value when loading', () => {
    // Arrange + Act
    render(
      <ReportCard
        testId="members-card"
        label="Members"
        value={42}
        isLoading
      />,
    );

    // Assert: the value is suppressed while loading, a loading marker shows.
    const card = screen.getByTestId('members-card');
    expect(card).not.toHaveTextContent('42');
    expect(screen.getByTestId('members-card-loading')).toBeInTheDocument();
  });

  it('renders an optional link to the matching report screen', () => {
    // Arrange + Act
    render(
      <PathProvider path="/dashboard">
        <ReportCard
          testId="dues-card"
          label="Unpaid Dues"
          value={3}
          link={{ href: '/reports/unpaid-dues', label: 'View report' }}
        />
      </PathProvider>,
    );

    // Assert
    const anchor = screen.getByRole('link', { name: 'View report' });
    expect(anchor).toHaveAttribute('href', '#/reports/unpaid-dues');
  });

  it('renders no link when none is supplied', () => {
    // Arrange + Act
    render(<ReportCard testId="members-card" label="Members" value={42} />);

    // Assert
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });
});

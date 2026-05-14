import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Onboarding } from './onboarding';

describe('Onboarding', () => {
  it('renders first-run guidance for the seeded admin', () => {
    // Arrange / Act
    render(<Onboarding />);

    // Assert: brief sign-in guidance is shown.
    expect(screen.getByTestId('onboarding')).toBeInTheDocument();
    expect(screen.getByTestId('onboarding')).toHaveTextContent(
      /seeded admin credentials/i,
    );
  });

  it('does not hardcode any credentials in the UI', () => {
    // Arrange / Act
    render(<Onboarding />);

    // Assert: guidance points at the docs rather than embedding a password.
    const text = screen.getByTestId('onboarding').textContent ?? '';
    expect(text).not.toMatch(/password\s*[:=]\s*\S/i);
  });
});

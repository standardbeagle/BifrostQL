// @vitest-environment jsdom
/**
 * The picker greys itself out whenever it holds a single profile. That is the
 * right presentation for a raw-only deployment and the WRONG one for a
 * profiles endpoint that failed to answer — both looked identical, so a broken
 * backend read as "this connection exposes a single profile". The unavailable
 * state has to say so.
 */

import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { ProfileDropdown } from './ProfileDropdown';
import { DEFAULT_PROFILES } from './api-profiles';

afterEach(cleanup);

describe('ProfileDropdown', () => {
  it('reports a genuinely single-profile connection as such', () => {
    render(
      <ProfileDropdown profiles={DEFAULT_PROFILES} activeId="default" onSelect={() => {}} />,
    );

    expect((screen.getByLabelText('Profile') as HTMLSelectElement).disabled).toBe(true);
    expect(screen.queryByTestId('profiles-unavailable')).toBeNull();
  });

  it('distinguishes an unreachable profiles endpoint from a single-profile connection', () => {
    render(
      <ProfileDropdown
        profiles={DEFAULT_PROFILES}
        activeId="default"
        onSelect={() => {}}
        unavailableReason="Profiles endpoint returned 503"
      />,
    );

    const notice = screen.getByTestId('profiles-unavailable');
    expect(notice.textContent).toMatch(/unavailable/i);
    // The reason is reachable, not buried in a console warning.
    expect(notice.title).toContain('503');
    // The app stays usable on the raw default.
    expect((screen.getByLabelText('Profile') as HTMLSelectElement).value).toBe('default');
  });
});

/**
 * First-run onboarding guidance for a freshly self-hosted Membership Manager.
 *
 * On a brand-new install the host seeds a single first-admin account; this
 * panel tells the operator to sign in with those seeded credentials. The
 * credentials themselves are intentionally *not* rendered here — they live in
 * the deployment docs (see the auth sub-task 1/3 notes) so the UI never ships
 * a known password. The panel is purely informational and is composed into the
 * {@link Login} screen.
 */
export function Onboarding() {
  return (
    <aside data-testid="onboarding" className="onboarding">
      <h2>First-run setup</h2>
      <p>
        This is a fresh Membership Manager install. Sign in with the seeded
        admin credentials created during setup to get started.
      </p>
      <p>
        The seeded username and password are listed in your deployment docs —
        once you are signed in you can change them and invite the rest of your
        club&apos;s officers.
      </p>
    </aside>
  );
}

import { Routes, Route, useNavigate } from '@standardbeagle/virtual-router';
import {
  AppShellProvider,
  AppLayout,
  AppNav,
  ProtectedRoute,
} from '@bifrostql/app-shell';
import { MemberList } from './members/member-list';

/** Permission required to view and manage the member roster. */
const MEMBERS_READ = 'main.members.read';

/** Stub member-detail screen. The editable form is delivered in sub-task 3. */
function MemberDetailStub() {
  const navigate = useNavigate();
  return (
    <section data-testid="member-detail-stub">
      <h2>Member detail</h2>
      <p>The member detail form is delivered in a later sub-task.</p>
      <button type="button" onClick={() => navigate('/members')}>
        Back to members
      </button>
    </section>
  );
}

/**
 * Membership Manager SPA root.
 *
 * Composes the app-shell stack: {@link AppShellProvider} wires `BifrostProvider`
 * (same-origin `/graphql`) and `SessionProvider` (same-origin auth session);
 * {@link AppLayout} provides the chrome with a metadata-driven {@link AppNav};
 * {@link ProtectedRoute} gates the member screens on the `main.members.read`
 * permission. Routing uses `@standardbeagle/virtual-router`, matching the
 * router used elsewhere in this repo's examples.
 */
function App() {
  const navigate = useNavigate();

  return (
    <AppShellProvider>
      <AppLayout
        header={<strong>Membership Manager</strong>}
        nav={<AppNav />}
      >
        <Routes>
          <Route path="/">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <MemberList />
            </ProtectedRoute>
          </Route>
          <Route path="/members">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <MemberList />
            </ProtectedRoute>
          </Route>
          <Route path="/members/:id">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <MemberDetailStub />
            </ProtectedRoute>
          </Route>
        </Routes>
      </AppLayout>
    </AppShellProvider>
  );
}

export default App;

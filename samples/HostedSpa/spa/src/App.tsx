import { Routes, Route, useNavigate } from '@standardbeagle/virtual-router';
import {
  AppShellProvider,
  AppLayout,
  AppNav,
  ProtectedRoute,
} from '@bifrostql/app-shell';
import { MemberList } from './members/member-list';
import { MemberForm } from './members/member-form';

/** Permission required to view and manage the member roster. */
const MEMBERS_READ = 'main.members.read';

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
          {/*
            One route covers both detail/edit and create: `MemberForm` reads
            the `:id` param and treats the sentinel value `new` as create mode.
            A separate `/members/new` route is avoided because virtual-router's
            `:id` segment also matches the literal `new`, which would mount the
            form twice.
          */}
          <Route path="/members/:id">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <MemberForm />
            </ProtectedRoute>
          </Route>
        </Routes>
      </AppLayout>
    </AppShellProvider>
  );
}

export default App;

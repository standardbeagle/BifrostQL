import { useContext } from 'react';
import { Routes, Route, useNavigate } from '@standardbeagle/virtual-router';
import {
  AppShellProvider,
  AppLayout,
  AppNav,
  ProtectedRoute,
  useSession,
} from '@bifrostql/app-shell';
import { BifrostContext } from '@bifrostql/react';
import { Login } from './auth/login';
import { MemberList } from './members/member-list';
import { MemberForm } from './members/member-form';
import { HouseholdForm } from './households/household-form';
import { PlanList } from './membership-plans/plan-list';
import { PlanForm } from './membership-plans/plan-form';
import { MemberPlanAssignment } from './membership-plans/member-plan-assignment';
import { RecordPayment } from './membership-plans/record-payment';
import { EventList } from './events/event-list';
import { EventForm } from './events/event-form';
import { EventRsvps } from './events/event-rsvps';
import { EventCheckin } from './events/event-checkin';
import { UnpaidDuesReport } from './reports/unpaid-dues-report';
import { UpcomingRenewalsReport } from './reports/upcoming-renewals-report';
import { ExpiredMembershipsReport } from './reports/expired-memberships-report';
import { AttendanceByEventReport } from './reports/attendance-by-event-report';
import { AttendanceByMemberReport } from './reports/attendance-by-member-report';
import { EmailSegments } from './segments/email-segments';
import { Dashboard } from './dashboard/dashboard';

/** Permission required to view and manage the member roster. */
const MEMBERS_READ = 'main.members.read';

/**
 * The dashboard summary screen, as a nav entry.
 *
 * Like the reports, the dashboard is not an overlay entity, so {@link AppNav}
 * cannot surface it on its own; it is appended to the entity-driven nav via
 * `AppNav`'s `children` render-prop. It leads the appended links because it is
 * the SPA's default landing route (see the `/` route below).
 */
const DASHBOARD_NAV_ITEM = { path: '/dashboard', label: 'Dashboard' };

/**
 * The dues/membership reports, as nav entries.
 *
 * Reports are read-only filtered views over existing overlay entities, not
 * entities themselves, so {@link AppNav} — which is entity-driven — cannot
 * surface them on its own. They are appended to the entity-driven nav via
 * `AppNav`'s `children` render-prop. Routing is gated on the same
 * `main.members.read` permission as the rest of the SPA.
 */
const REPORT_NAV_ITEMS = [
  { path: '/reports/unpaid-dues', label: 'Unpaid Dues' },
  { path: '/reports/upcoming-renewals', label: 'Upcoming Renewals' },
  { path: '/reports/expired-memberships', label: 'Expired Memberships' },
  { path: '/reports/attendance-by-event', label: 'Attendance by Event' },
  { path: '/reports/attendance-by-member', label: 'Attendance by Member' },
  { path: '/reports/email-segments', label: 'Email Segments' },
];

/**
 * Navigation for the SPA: the entity-driven {@link AppNav} entries followed by
 * the dues/membership report links. Mirrors `AppNav`'s default `<nav>` markup
 * so the reports sit in the same list as the entity entries.
 */
function MembershipNav() {
  return (
    <AppNav>
      {(items) => (
        <nav aria-label="Application navigation">
          <ul>
            {items.map((item) => (
              <li key={item.key}>
                <a href={`#/${item.key}`}>{item.label}</a>
              </li>
            ))}
            <li key={DASHBOARD_NAV_ITEM.path}>
              <a href={`#${DASHBOARD_NAV_ITEM.path}`}>
                {DASHBOARD_NAV_ITEM.label}
              </a>
            </li>
            {REPORT_NAV_ITEMS.map((item) => (
              <li key={item.path}>
                <a href={`#${item.path}`}>{item.label}</a>
              </li>
            ))}
          </ul>
        </nav>
      )}
    </AppNav>
  );
}

/** Conventional path of the local-auth logout endpoint, a same-origin sibling
 * of the GraphQL endpoint — mirrors the `/auth/login` and `/auth/session`
 * derivation used by the login screen and app-shell `SessionProvider`. */
const LOGOUT_PATH = '/auth/logout';

/**
 * Header branding plus a session-aware logout control.
 *
 * Rendered as the {@link AppLayout} `header`. When the session is
 * authenticated, a "Log out" button posts to `/auth/logout` (clearing the auth
 * cookie) and then refreshes the session via {@link useSession}, which flips
 * the app's `ProtectedRoute` gates back to the login screen. The logout URL is
 * derived from the configured GraphQL endpoint the same way the login screen
 * derives `/auth/login`.
 */
function AppHeader() {
  const config = useContext(BifrostContext);
  const { isAuthenticated, refresh } = useSession();

  const handleLogout = async () => {
    if (!config) {
      return;
    }
    const url = new URL(config.endpoint);
    url.pathname = LOGOUT_PATH;
    url.search = '';
    url.hash = '';
    await fetch(url.toString(), {
      method: 'POST',
      credentials: 'include',
    }).catch(() => undefined);
    refresh();
  };

  return (
    <>
      <strong>Membership Manager</strong>
      {isAuthenticated ? (
        <button type="button" onClick={handleLogout}>
          Log out
        </button>
      ) : null}
    </>
  );
}

/**
 * Membership Manager SPA root.
 *
 * Composes the app-shell stack: {@link AppShellProvider} wires `BifrostProvider`
 * (same-origin `/graphql`) and `SessionProvider` (same-origin auth session);
 * {@link AppLayout} provides the chrome with a metadata-driven {@link AppNav}
 * and an {@link AppHeader} carrying the logout control; {@link ProtectedRoute}
 * gates the member screens on the `main.members.read` permission, redirecting
 * unauthenticated visitors to the `/login` route, which renders {@link Login}.
 * Routing uses `@standardbeagle/virtual-router`, matching the router used
 * elsewhere in this repo's examples.
 */
function App() {
  const navigate = useNavigate();

  return (
    <AppShellProvider>
      <AppLayout header={<AppHeader />} nav={<MembershipNav />}>
        <Routes>
          {/*
            The login screen. `ProtectedRoute`'s `onUnauthenticated` redirects
            unauthenticated visitors here from every gated route; the screen
            posts to `/auth/login` and, on success, refreshes the session so
            the gates re-open. It is intentionally outside `ProtectedRoute` —
            it is the destination for unauthenticated users.
          */}
          <Route path="/login">
            <Login />
          </Route>
          {/*
            The dashboard is the SPA's default landing route: `/` and the
            explicit `/dashboard` path both render the summary cards, in place
            of the former `MemberList` landing. The member roster keeps its own
            `/members` route below.
          */}
          <Route path="/">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <Dashboard />
            </ProtectedRoute>
          </Route>
          <Route path="/dashboard">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <Dashboard />
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
          {/*
            Households reuse the single-route-with-`new`-sentinel pattern from
            members: `HouseholdForm` reads `:id` and treats `new` as create
            mode. The household nav entry comes from the overlay's
            `navPlacement: main` via `AppNav`, so no nav wiring is needed here.
          */}
          <Route path="/households/:id">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <HouseholdForm />
            </ProtectedRoute>
          </Route>
          {/*
            Membership plans reuse the members pattern: a list route plus a
            single `:id`-with-`new`-sentinel form route. The plan nav entry
            comes from the overlay's `navPlacement: main` via `AppNav`.
          */}
          <Route path="/plans">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <PlanList />
            </ProtectedRoute>
          </Route>
          <Route path="/plans/:id">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <PlanForm />
            </ProtectedRoute>
          </Route>
          {/*
            Member-plan assignment: a single screen listing and creating
            `member_memberships` links. Nav entry comes from the overlay's
            `member_memberships` `navPlacement: main` via `AppNav`.
          */}
          <Route path="/memberships">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <MemberPlanAssignment />
            </ProtectedRoute>
          </Route>
          {/*
            Record-payment: a single screen listing recorded `dues_payments`
            and recording new ones against an open `dues_invoices`, advancing
            the linked `member_memberships` status. Nav entry comes from the
            overlay's `dues_payments` `navPlacement: main` via `AppNav`.
          */}
          <Route path="/payments">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <RecordPayment />
            </ProtectedRoute>
          </Route>
          {/*
            Events reuse the members pattern: a list route plus a single
            `:id`-with-`new`-sentinel form route. The events nav entry comes
            from the overlay's `main.events` `navPlacement: main` via `AppNav`.
          */}
          <Route path="/events">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <EventList />
            </ProtectedRoute>
          </Route>
          {/*
            Per-event RSVP management. Declared before `/events/:id` so the
            three-segment RSVP path is matched ahead of the two-segment form
            route. Reuses the `main.members.read` permission gate; no distinct
            event-manager permission is wired yet.
          */}
          <Route path="/events/:id/rsvps">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <EventRsvps />
            </ProtectedRoute>
          </Route>
          {/*
            Per-event attendance check-in. Declared before `/events/:id` so the
            three-segment check-in path is matched ahead of the two-segment
            form route, the same ordering as the RSVP route above. Reuses the
            `main.members.read` permission gate; no distinct event-manager
            permission is wired yet.
          */}
          <Route path="/events/:id/check-in">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <EventCheckin />
            </ProtectedRoute>
          </Route>
          <Route path="/events/:id">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <EventForm />
            </ProtectedRoute>
          </Route>
          {/*
            Dues/membership reports: read-only filtered `BifrostTable` views
            over the `dues_invoices` / `member_memberships` entities. They are
            not overlay entities, so their nav entries are appended by
            `MembershipNav` rather than coming from `AppNav` directly.
          */}
          <Route path="/reports/unpaid-dues">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <UnpaidDuesReport />
            </ProtectedRoute>
          </Route>
          <Route path="/reports/upcoming-renewals">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <UpcomingRenewalsReport />
            </ProtectedRoute>
          </Route>
          <Route path="/reports/expired-memberships">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <ExpiredMembershipsReport />
            </ProtectedRoute>
          </Route>
          {/*
            Event-attendance reports: read-only `BifrostTable` views over the
            `main.event_attendance` entity, grouped by event or by member via a
            server-side sort. Like the dues reports, they are not overlay
            entities, so their nav entries are appended by `MembershipNav`.
          */}
          <Route path="/reports/attendance-by-event">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <AttendanceByEventReport />
            </ProtectedRoute>
          </Route>
          <Route path="/reports/attendance-by-member">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <AttendanceByMemberReport />
            </ProtectedRoute>
          </Route>
          {/*
            Email segments: lists the overlay's declarative `emailSegments`
            audience definitions and renders the matching audience for a
            selected segment via a filtered `BifrostTable`. Definition-and-
            audience only — no sending infrastructure. Like the other reports
            it is not an overlay entity, so its nav entry is appended by
            `MembershipNav`.
          */}
          <Route path="/reports/email-segments">
            <ProtectedRoute
              requirePermission={MEMBERS_READ}
              onUnauthenticated={() => navigate('/login')}
            >
              <EmailSegments />
            </ProtectedRoute>
          </Route>
        </Routes>
      </AppLayout>
    </AppShellProvider>
  );
}

export default App;

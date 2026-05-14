import { Routes, Route, useNavigate } from '@standardbeagle/virtual-router';
import {
  AppShellProvider,
  AppLayout,
  AppNav,
  ProtectedRoute,
} from '@bifrostql/app-shell';
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
import { UnpaidDuesReport } from './reports/unpaid-dues-report';
import { UpcomingRenewalsReport } from './reports/upcoming-renewals-report';
import { ExpiredMembershipsReport } from './reports/expired-memberships-report';

/** Permission required to view and manage the member roster. */
const MEMBERS_READ = 'main.members.read';

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
        nav={<MembershipNav />}
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
        </Routes>
      </AppLayout>
    </AppShellProvider>
  );
}

export default App;

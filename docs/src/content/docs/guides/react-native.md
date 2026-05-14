---
title: React Native
description: React Native feasibility for @bifrostql/react — what works today, the auth refresh / session-failure model, and known gaps.
---

The `@bifrostql/react` runtime transport is **React Native compatible**. The
query/mutation hooks, the GraphQL transport, the generated types, and the
helper utilities all run unchanged on React Native. This guide records what
works, how authentication failures are handled, and the known gaps to plan
around.

## What works

### Transport

`executeGraphQL` — the function every hook calls to talk to the server — uses
only `fetch`, `AbortSignal`, and `JSON`. All three are part of the React Native
runtime, so there is **no browser-only API on the transport path**: no `window`,
no `localStorage`, no DOM access.

The same applies to `getToken` header injection on `BifrostConfig`. `getToken`
is just an async/sync function you supply; where the token comes from (React
Native's `AsyncStorage`, `expo-secure-store`, an in-memory store, etc.) is your
choice. The transport never touches `localStorage` itself.

### Hooks and helpers

The TanStack Query hooks (`useBifrost`, `useBifrostInfinite`,
`useBifrostMutation`, `useBifrostBatch`, `useBifrostDiff`, `useBifrostTable`)
are pure React and work on React Native. The query/mutation builders, the diff
engine, and the filter/sort serializers have no platform dependencies.

> `useBifrostTable`'s URL-state helpers (`readFromUrl` / `writeToUrl`) read and
> write `window.location`. They are opt-in — only used when you wire URL state —
> and are not on the transport path. On React Native, simply do not use the
> URL-state integration; the rest of `useBifrostTable` works normally.

### Generated types

The codegen output is plain TypeScript type declarations. They are consumed at
build time and emit nothing into the bundle, so they are fully React Native
compatible.

## Auth refresh / session-failure handling

`BifrostConfig` accepts two optional hooks that let the transport recover from
an expired session:

```ts
const config: BifrostConfig = {
  endpoint: 'https://api.example.com/graphql',
  getToken: () => tokenStore.get(),
  // Invoked on a 401 or a GraphQL auth error. Call your session/login
  // endpoints here (e.g. GET /auth/session, /auth/login) to mint a fresh
  // credential. Return the new token, or return nothing after updating the
  // token store out-of-band.
  refreshToken: async () => {
    await tokenStore.refresh();
    return tokenStore.get();
  },
  // Invoked with a typed BifrostAuthError when the failure could not be
  // recovered — no refreshToken hook, the refresh threw, or the retry still
  // failed authentication.
  onSessionExpired: (error) => {
    navigation.navigate('Login');
  },
};
```

Behavior:

- On HTTP `401` or a GraphQL auth error (a message matching
  `unauthorized` / `unauthenticated` / `forbidden`), the transport invokes
  `refreshToken`, then **retries the request exactly once** with the refreshed
  credential.
- If the retry succeeds, the caller never sees the failure.
- If no `refreshToken` hook is configured, the refresh throws, or the retry
  still fails authentication, the transport throws a typed `BifrostAuthError`
  and invokes `onSessionExpired` with it.
- Both hooks are optional. With neither configured, an auth failure simply
  surfaces as a `BifrostAuthError` — the happy-path `getToken` behavior is
  unchanged.

`BifrostAuthError` is exported from `@bifrostql/react`, so consumers can
distinguish an unrecoverable session failure from ordinary request errors with
`error instanceof BifrostAuthError`.

## Known gaps

- **Subscriptions / WebSocket transport.** There is no subscription transport in
  `@bifrostql/react` today. When one is added it will need a WebSocket layer;
  React Native's `WebSocket` exists but the GraphQL-over-WebSocket client must
  be verified against it separately.
- **Codegen CLI is build-time only.** The codegen CLI runs on Node during your
  build. It is not — and is not intended to be — bundled into a React Native
  app. Run codegen as part of your build pipeline; ship only its type output.
- **Bundler / polyfill notes.** Metro (React Native's bundler) resolves the
  package's standard ESM entry. No polyfills are required for the transport
  path because it relies only on `fetch`, `AbortSignal`, and `JSON`, all
  provided by the React Native runtime. If you opt into the `useBifrostTable`
  URL-state helpers you must avoid them on React Native, as noted above.

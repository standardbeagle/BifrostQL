---
title: "Application Schema Detection"
description: "How BifrostQL automatically detects known application schemas like WordPress and applies optimized configurations — explicit foreign keys, hidden internal tables, and EAV flattening."
---

BifrostQL can recognize the database schema of known applications and apply pre-built configurations automatically. When you connect to a WordPress database, BifrostQL detects the `wp_` prefix, injects the correct foreign key relationships, hides internal tables, and enables meta flattening — no manual configuration required.

## How detection works

On database connection, BifrostQL scans table names for patterns that identify known applications. Each detector defines a set of required tables (e.g., `{prefix}users`, `{prefix}posts`, `{prefix}options` for WordPress) and tests whether those tables exist in the database.

When a match is found:

1. **Prefix detection** — identifies the table prefix (e.g., `wp_`, `mysite_`)
2. **Foreign key injection** — adds explicit FK relationships the application relies on but doesn't declare in DDL
3. **Table visibility** — hides internal/infrastructure tables that aren't useful through the API
4. **Labels** — applies friendly display names to tables and columns
5. **EAV configuration** — registers meta tables for automatic key-value flattening

The detected configuration is applied before schema generation, so the GraphQL API reflects the optimized structure from the first request.

## Prefix-aware linking

Most applications use a consistent table prefix. WordPress uses `wp_` by default, but supports custom prefixes. Multisite WordPress installations use multiple prefixes (`wp_`, `wp_2_`, `wp_3_`).

BifrostQL detects these prefixes and scopes auto-linking within prefix groups:

- `wp_users` and `wp_usermeta.user_id` are linked because both belong to the `wp_` group
- `wp_2_posts` and `wp_2_postmeta.post_id` are linked within the `wp_2_` group
- Cross-group auto-links are suppressed to avoid false matches

Explicit foreign keys (injected by the detector or declared in the database) still cross prefix boundaries. This is intentional — WordPress multisite shares the `wp_users` table across all sites, so `wp_2_posts.post_author` correctly links to `wp_users.ID`.

## Configuration

### Global toggle

```
auto-detect-app: enabled
```

Detection is enabled by default. Set to `disabled` to skip all automatic detection:

```
auto-detect-app: disabled
```

### Force a specific app schema

If auto-detection fails (e.g., heavily modified table names), you can force a specific detector:

```
app-schema: wordpress
```

This bypasses the detection scan and applies the WordPress configuration directly.

### Manual prefix groups

For custom applications that aren't auto-detected, you can define prefix groups manually:

```
prefix-groups: myapp_=myapp, v2_=v2
```

This tells BifrostQL to scope auto-linking so that `myapp_users` and `myapp_orders` link to each other, but not to `v2_users`.

### Read-only detection result

After detection runs, the result is available as read-only metadata:

```
detected-app: wordpress
```

This can be inspected for diagnostics but cannot be set manually — use `app-schema` to force a specific detector.

## Supported applications

| Application | Status | Detector tables | Features |
|-------------|--------|----------------|----------|
| **WordPress** | Built-in | `{prefix}users`, `{prefix}posts`, `{prefix}options` | FK injection, table hiding, EAV flattening, PHP deserialization |

The detection framework is extensible. New detectors can be added for Drupal, Laravel, Magento, or any application with a recognizable table naming pattern.

## Disabling detection

To disable detection for a specific database connection, set the metadata on the database:

```
auto-detect-app: disabled
```

This leaves all tables visible with standard auto-linking behavior — no injected FKs, no hidden tables, no EAV flattening.

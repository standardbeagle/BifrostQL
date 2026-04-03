---
title: "WordPress Database Guide"
description: "How to connect BifrostQL to a WordPress MySQL database — auto-detection, foreign key injection, meta flattening, and PHP deserialization."
---

BifrostQL auto-detects WordPress databases and configures the GraphQL API to match WordPress's data model. Connect to the database and the API is ready — no mapping files, no manual FK definitions.

## Quick start

Point BifrostQL at your WordPress database:

```
Server=localhost;Database=wordpress;User Id=wp_user;Password=xxx;
```

BifrostQL detects the WordPress schema, applies the configuration, and publishes the API. Open the GraphQL playground and query immediately.

## What auto-detection does

### Prefix detection

BifrostQL identifies the WordPress prefix by scanning for `{prefix}users`, `{prefix}posts`, and `{prefix}options`. The standard prefix is `wp_`, but any custom prefix is detected automatically.

### Foreign key injection

WordPress doesn't declare foreign keys in its DDL. BifrostQL injects the following relationships:

| Source column | Target table | Target column |
|--------------|-------------|--------------|
| `wp_posts.post_author` | `wp_users` | `ID` |
| `wp_posts.post_parent` | `wp_posts` | `ID` |
| `wp_postmeta.post_id` | `wp_posts` | `ID` |
| `wp_comments.comment_post_ID` | `wp_posts` | `ID` |
| `wp_comments.user_id` | `wp_users` | `ID` |
| `wp_commentmeta.comment_id` | `wp_comments` | `comment_ID` |
| `wp_term_relationships.object_id` | `wp_posts` | `ID` |
| `wp_term_relationships.term_taxonomy_id` | `wp_term_taxonomy` | `term_taxonomy_id` |
| `wp_term_taxonomy.term_id` | `wp_terms` | `term_id` |
| `wp_usermeta.user_id` | `wp_users` | `ID` |

These injected FKs enable join navigation in GraphQL without any manual configuration.

### Hidden tables

Internal WordPress tables that aren't useful through a GraphQL API are hidden automatically:

- Action Scheduler tables (`wp_actionscheduler_*`)
- Other infrastructure tables used by WordPress internals

Hidden tables don't appear in the GraphQL schema but remain accessible in the database.

### Friendly labels

Tables and columns receive human-readable labels. For example, `wp_posts` becomes "Posts", `post_author` becomes "Author".

## Multisite support

WordPress multisite uses a shared `wp_users` table with per-site content tables under distinct prefixes:

- `wp_` — site 1 (and shared tables like `wp_users`)
- `wp_2_` — site 2
- `wp_3_` — site 3

BifrostQL detects each prefix as a separate group. Auto-links are scoped within each group, so `wp_2_posts` links to `wp_2_postmeta` but not to `wp_3_postmeta`.

Cross-group relationships work through the injected FKs. `wp_2_posts.post_author` correctly joins to the shared `wp_users.ID` table because explicit FKs cross prefix boundaries.

## EAV meta flattening

WordPress stores extensible data in Entity-Attribute-Value (EAV) meta tables: `wp_postmeta`, `wp_usermeta`, `wp_commentmeta`, and `wp_termmeta`. Each row is a key-value pair linked to a parent record.

BifrostQL flattens these into a `_meta` field on the parent type. Instead of querying a separate meta table and pivoting the results yourself, you get a JSON object with all meta keys as properties:

```graphql
{
  wp_posts(limit: 5) {
    data {
      ID
      post_title
      _meta
    }
  }
}
```

Returns:

```json
{
  "data": {
    "wp_posts": {
      "data": [
        {
          "ID": 1,
          "post_title": "Hello world!",
          "_meta": {
            "_edit_last": "1",
            "_thumbnail_id": "42",
            "custom_field": "custom value"
          }
        }
      ]
    }
  }
}
```

The `_meta` field is available on posts, users, comments, and terms.

## PHP deserialization

WordPress stores structured data using PHP's `serialize()` format in meta values and options. BifrostQL automatically detects and converts these to JSON:

```graphql
{
  wp_options(filter: { option_name: { _eq: "active_plugins" } }) {
    data {
      option_name
      option_value
    }
  }
}
```

A raw PHP serialized value like:

```
a:2:{i:0;s:19:"akismet/akismet.php";i:1;s:29:"classic-editor/classic-editor.php";}
```

Is returned as JSON:

```json
{
  "option_name": "active_plugins",
  "option_value": ["akismet/akismet.php", "classic-editor/classic-editor.php"]
}
```

This applies to all meta tables and the options table. Values that aren't PHP serialized are returned unchanged.

## Example queries

### Posts with author

```graphql
{
  wp_posts(filter: { post_status: { _eq: "publish" } }, limit: 10) {
    data {
      ID
      post_title
      post_date
      __join {
        wp_users(on: { post_author: "ID" }) {
          data {
            display_name
            user_email
          }
        }
      }
    }
  }
}
```

### Posts with meta and categories

```graphql
{
  wp_posts(filter: { post_type: { _eq: "post" } }, limit: 5) {
    data {
      ID
      post_title
      _meta
      __join {
        wp_term_relationships(on: { ID: "object_id" }) {
          data {
            __join {
              wp_term_taxonomy(on: { term_taxonomy_id: "term_taxonomy_id" }) {
                data {
                  taxonomy
                  __join {
                    wp_terms(on: { term_id: "term_id" }) {
                      data { name slug }
                    }
                  }
                }
              }
            }
          }
        }
      }
    }
  }
}
```

### Users with meta

```graphql
{
  wp_users(limit: 10) {
    data {
      ID
      user_login
      display_name
      _meta
    }
  }
}
```

## Manual override

### Force WordPress detection

If auto-detection doesn't trigger (e.g., tables were renamed), force it:

```
app-schema: wordpress
```

### Disable detection entirely

To use standard BifrostQL behavior without WordPress-specific configuration:

```
auto-detect-app: disabled
```

### Custom prefixes

No special configuration needed. BifrostQL detects any prefix, not just `wp_`. If your WordPress installation uses `mysite_` as the prefix, detection works the same way — it finds `mysite_users`, `mysite_posts`, and `mysite_options`, then applies the configuration with `mysite_` as the prefix.

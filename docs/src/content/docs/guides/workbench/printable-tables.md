---
title: "Tabular Reports"
description: "The report runner renders a saved query or table with group bands, server-computed subtotals and grand totals, print CSS, and CSV export — subtotals always come from a grouped aggregate query, never from summing the visible page."
---

The **report runner** produces Access-style tabular reports: a source query,
group bands with subtotals, grand totals, page headers and footers, and a print
stylesheet. Reports persist as [saved objects](/BifrostQL/concepts/saved-objects/)
(`type: report`).

## Report definition

A report's definition names a **source** (a
[saved query](/BifrostQL/guides/workbench/saved-queries/) or a table + filter),
the **columns** to show, one or more **group bands** (`{ column, sortDir,
totals: [{ column, op }] }`), **grand totals**, and page header/footer text.

## Totals come from the server

The correctness rule: every subtotal and grand total originates from a
**grouped [aggregate](/BifrostQL/guides/aggregate-queries/) query**, not from
summing the rows currently on screen. A two-level grouped report's subtotals and
grand total match an equivalent hand-written `GROUP BY` query exactly. Group
bands request a **stable server ordering** before adjacent-row boundaries are
derived, so interleaved groups cannot split a band.

## Print and export

- **Print CSS** emits a repeating `<thead>` per printed page and
  `page-break-inside: avoid` on band boundaries. Print through `window.print` —
  the report renders in whatever engine hosts the page: a desktop browser, or
  the platform-native webview the Photino desktop shell embeds (WebView2/Chromium
  on Windows, WebKitGTK on Linux, WKWebView/WebKit on macOS).
- **CSV export** flattens the full result set across every page (via paged
  fetch), so the exported row count and cell values match what a full scroll
  would show — see [export](/BifrostQL/guides/workbench/export/).

## Related

- [Data workbench overview](/BifrostQL/guides/workbench/)
- [Aggregate queries](/BifrostQL/guides/aggregate-queries/) — the surface the
  totals ride.

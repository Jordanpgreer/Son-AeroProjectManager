# Quotes dashboard, RFQ periods, and on-time performance — 2026-11-19

## Source and comparison state

- Selected source: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-c05db40d-ed63-41d2-8dd4-8f2e4c6c37e4.png` (1829 × 860).
- Local implementation preview: `http://127.0.0.1:5248/output/dashboard-preview/`.
- Browser evidence in repository `output/playwright/`: `quotes-period-final.png`, `quotes-default-final.png`, `quotes-detail-final.png`, `quotes-mobile-final.png`, `quotes-mobile-detail-final.png`, and `quotes-dark-final.png`.
- Source and final period-menu screenshot were visually inspected together at 1829 × 860, light theme, All quotes, November 2026, with the detail panel closed. The selected month covers the complete calendar month, November 1–30, as the user requested.
- Additional inspection covered the open detail panel at 1829 × 860, mobile at 390 × 844, and dark theme at 1280 × 800. The preview uses the actual dashboard components with synthetic API responses and a clearly marked sample-data heading.

## Findings and refinements

- No remaining P0, P1, or P2 findings within the inspected dashboard states.
- Typography: increased the first implementation's small table and metric text. Final rows use 16 px body text, 17 px quote numbers, and 12 px headers. Existing Arda type and color tokens preserve consistency with the surrounding application rather than reproducing generated lettering exactly.
- Layout: search and the compact period picker precede one performance strip and a full-width seven-column table. The application supplies the page heading; the preview supplies a sample-data heading. Old calculator-workspace and assigned-quote heading/tab sections remain removed.
- Detail panel: closed initially, opened only after selecting a quote, and closable by X or Escape. Widened and condensed the panel so its primary action fits in the desktop view. Estimator and value move into the panel while it is open, leaving more room for the other five table columns.
- Colors and assets: existing steel-blue, pale-blue, dark-theme, and semantic status tokens remain in use. Icons use the existing icon library. No new raster imagery is required.
- Copy and metrics: added On-time %, including an excluded-record count and an explanation of the eligible sample. Average completion stays unavailable when verified timing is incomplete; the preview does not invent a value.

## Measured and behavioral evidence

- Table width at the desktop viewport: 1765 px closed, 1369 px with details open, and 1765 px after closing.
- Document horizontal overflow: zero at desktop, mobile, and dark-theme inspection widths. The narrow-screen results table scrolls inside its own container.
- Mobile detail close control: 44 × 44 px and inside the viewport. The primary action is visible.
- Twenty-two browser assertions passed: live part/quote/customer searches; My/All scope; stable performance while narrowing results; status filtering; pagination; empty and failed results; stale-response protection; preset and inclusive custom dates; undated quotes under All time; mouse and keyboard detail selection; close and focus restoration; and the existing Quote Status link target.
- Synthetic November cohort: All quotes shows 66.7% (2 of 3 eligible), My quotes shows 100% (2 of 2 eligible), and one completed quote is disclosed as excluded. These are test values, not production performance.
- Browser console: zero errors and zero warnings; React development-tool informational messages only.

## Validation and limits

- Backend suite: 694 passed, zero failures or skips.
- Frontend suite: 207 passed, zero failures, one pre-existing native Excel skip.
- Production frontend build and lint passed after the final layout changes.
- Browser interaction testing used the isolated fixture. Backend service and endpoint tests independently verified permissions, RFQ-date filtering, calculations, and read-only behavior. The preview does not exercise the deployed database or navigate the full production Quote Status flow.
- No commit, push, deployment, or IIS command was performed. The existing development database hash remained unchanged during this work.

final result: passed

---

# Design QA: Estimating module cleanup and statistics redesign

## Source visual truth

- `C:\Users\USER\AppData\Local\Temp\codex-clipboard-dc1b5bc6-8b1f-4b8d-903d-962c3ae77111.png` — redundant Estimating Logs intro block, 2249 x 153 px.
- `C:\Users\USER\AppData\Local\Temp\codex-clipboard-45f63f01-96e0-410d-bbc4-290d53e1c7eb.png` — collapsed double-logo defect, 508 x 134 px.
- `C:\Users\USER\AppData\Local\Temp\codex-clipboard-279e1497-ba9c-45b9-b9b3-00328578803b.png` — sidebar identity block, 252 x 258 px.
- `C:\Users\USER\AppData\Local\Temp\codex-clipboard-39436efd-ab77-444e-ad6a-1a4a5c471de2.png` — admin capability notice, 953 x 128 px.
- `C:\Users\USER\AppData\Local\Temp\codex-clipboard-7b524684-9273-4003-9faf-71b834292c02.png` — browser-storage notice, 480 x 84 px.
- The sixth supplied image duplicates the browser-storage notice and was treated as the same target.

## Implementation evidence

- Live implementation URL: `http://localhost:5160/#/history`.
- Implementation screenshot: in-app Browser capture attached to the task comparison input; the Browser surface does not expose a filesystem path for that capture.
- Full-view capture: 1280 x 720 px at a 1280 x 720 CSS viewport, device pixel ratio 1, light theme, expanded navigation.
- Additional states: 1280 x 720 dark theme with collapsed navigation and 520 x 800 dark responsive layout.

## Findings

- No remaining P0, P1, or P2 findings.
- Fonts and typography: existing Arda/Estimating typography is preserved. Removing the repeated intro eliminates duplicate page hierarchy without changing the topbar title.
- Spacing and layout rhythm: Department Statistics is now the first content card. Period controls sit directly beneath its label, with Import Excel occupying the former right-side control area. The 520 px layout keeps all four page-navigation links on one row and stacks Import below the three equal period controls without overflow.
- Colors and visual tokens: primary actions, selected controls, KPI focus/selection, card accents, audit accents, and import-flow accents use the same `--steel-action` blue as New Quote (`rgb(47, 97, 149)`). Semantic error styling remains distinct.
- Image quality and asset fidelity: original Arda raster assets remain unchanged. Collapsed light mode renders only the standard mark and collapsed dark mode renders only the reversed mark, each at 48 x 48 CSS px.
- Copy and content: the repeated Estimating Logs card, account identity, admin capability message, and browser-storage message are absent. Existing functional page titles and navigation labels remain.

## Focused comparison evidence

- Statistics header: no separate selected-period text remains; the selected period is communicated by `aria-pressed` and the blue button state.
- Import control: 139 x 40 px on desktop at the right edge of the statistics card; 447 x 40 px beneath the period controls at 520 px.
- Collapsed brand: exactly one `.brand-mark` is visible in light mode and exactly one is visible in dark mode.
- Responsive navigation: four links measured on one row at 520 px; header/nav gap is 0 px and horizontal overflow is 0 px.
- Removed content: browser checks found no `ProjectTrackerAdmin`, no storage-notice copy, and no admin rate/settings capability copy.

## Comparison history

1. P2 — the source showed both standard and reversed collapsed marks simultaneously. Fixed by making the collapsed theme selectors mutually exclusive. Post-fix evidence shows one visible mark in each theme.
2. P2 — four mobile navigation links used a three-column grid. Fixed with auto-fitting 90 px minimum tracks. Post-fix evidence shows all four links on one row at 520 px.
3. P2 — the source showed redundant page hierarchy and excess vertical space. Removed the intro card and promoted the statistics controls. Post-fix full-view comparison shows Department Statistics as the first content region.

## Interaction and runtime checks

- This month and This week statistics controls changed selection successfully; selected color matched New Quote blue.
- Import Excel opened the import dialog and Close import dismissed it.
- Quotes Dashboard and Rates Reference routes loaded after their notices were removed.
- Browser console: no errors or warnings.
- Horizontal overflow: none at 1280 px or 520 px.

final result: passed

---

# Shared shell separator alignment — 2026-08-26

## Comparison target

- Source visual truth: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-a2c9cc70-2406-4b06-afbb-324a484ad8c7.png`.
- Browser-rendered implementation: `http://127.0.0.1:5180/#/quotes` in the in-app Browser.
- Implementation screenshot: `C:\Users\USER\projects\non project folder\Project Tracker\tmp\design-qa\estimating-separator-hover-real-border-after.png`.
- Combined full-state comparison: `C:\Users\USER\projects\non project folder\Project Tracker\tmp\design-qa\estimating-separator-comparison.png`.
- Focused seam comparison: `C:\Users\USER\projects\non project folder\Project Tracker\tmp\design-qa\estimating-separator-focused-comparison.png`.
- Source pixels: 838 x 346, supplied as a cropped high-density screenshot with unknown CSS viewport and device density.
- Implementation pixels and CSS viewport: 1280 x 720 at device pixel ratio 1.
- State: light theme, expanded navigation, `Back to Arda` link hovered.
- Density normalization: raw pixel thickness was not compared across the unknown-density source and the DPR 1 implementation. The focused visual comparison establishes edge alignment; browser-computed CSS measurements establish exact thickness and datum.

## Findings

- No actionable P0, P1, or P2 findings remain.
- Fonts and typography: unchanged; the existing Estimating hierarchy and hover-label typography remain intact.
- Spacing and layout rhythm: the sidebar identity band and topbar both end at 131 CSS px. Their separators now share the same bottom datum.
- Colors and visual tokens: the sidebar keeps `--arda-red`; the topbar keeps the existing `--steel` gradient. No palette or semantic-token changes were introduced.
- Image quality and asset fidelity: the supplied Arda raster lockup is unchanged and remains sharp at its existing crop and scale.
- Copy and content: unchanged, including the exact `Back to Arda` hover label.

## Comparison evidence

- Full-state comparison: the combined image shows the supplied hover state and the corrected browser-rendered state together. The source red rule sits above the blue rule; the corrected rules meet at one edge.
- Focused region: the seam comparison isolates the sidebar/topbar junction and shows the corrected red and blue top edges aligned without an intervening gray band.
- Browser-computed post-fix measurements: both separators are real solid bottom borders with a shared `3px` thickness token; the brand bottom and topbar bottom are both 131 CSS px; the former blue `::after` painter has `content: none`.

## Comparison history

1. P2 — hovering the brand link changed its real 3 px bottom border to gray and drew a second 3 px red inset shadow above it, visibly lifting the red rule. Fixed by preserving the real red bottom border, removing the inset duplicate, and rendering the blue rule with the same bottom-border primitive. Post-fix browser measurements and the focused comparison show identical 3 px thickness and a shared 131 px datum.

## Interaction and runtime checks

- Hovered `Back to Arda` and confirmed the hover label, red separator, and blue separator render together in the intended state.
- Collapsed dark mode retained the same 131 px datum and two 3 px bottom borders; the preview was restored to expanded light mode afterward.
- Browser console errors and warnings: zero.
- Estimating production build passed.
- Estimating lint passed.
- All 26 Estimating client tests passed.
- Shared canonical rule and all five synced frontend copies contain the corrected declarations; the obsolete inset separator is absent.

final result: passed

---

# Calculator context and live-search refinement — 2026-08-25

## Comparison target

- Live-search visual reference: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-78cd2173-3ab1-404c-8265-d69e831f6660.png`.
- Browser-rendered implementation: `http://localhost:5160/#/history` at a 1280 × 720 CSS viewport in light theme.
- Focused implementation capture: `C:\Users\USER\AppData\Local\Temp\estimating-live-highlight-qa.png`.
- Calculator implementation: `http://localhost:5160/#/calculator` in the same browser and theme.

## Findings

- No actionable P0, P1, or P2 findings remain.
- The search highlight matches the Project Tracker reference: a tight two-pixel yellow mark with dark ink and a two-pixel radius, preserving the record's original capitalization.
- The Estimate Context hierarchy keeps the seven common fields visible and moves NSN, Solicitation number, and RFQ number into one clearly labelled native disclosure. The collapsed row remains discoverable through its Optional or populated-count badge.
- The Facilities card now states the exact calculation effect, uses dollar prefixes, and visually separates its explanation from the quantity-tier inputs.
- The Estimating navigation heading contains no Controlled tag; the existing quote-input classification remains scoped to the Estimate Context card.

## Interaction and runtime checks

- The Additional identifiers disclosure opened through its labelled summary and exposed all three expected inputs.
- A mixed-case `bElL` search returned two queue records and highlighted both `Bell` substrings using computed colors `rgb(243, 196, 79)` and `rgb(23, 32, 42)`.
- Search, filtering, pagination, sorting, and filtered export controls remained present; the live result region reports its busy state accessibly.
- Browser console errors: zero.
- Lint passed, 26 calculator/export tests passed, and the production build passed.

final result: passed

# Arda cross-module design QA

Date: 2026-08-25

## Scope

The review covers the Portal/Arda Hub and Admin Console, Project Tracker, Estimating Dashboard, Engineering Hub, and Quality Assurance in both light and dark themes. It evaluates the shared blue-gray light surfaces, dark identity band, theme-aware Arda artwork, favicon, removal of the grid canvas, and compact layouts.

## Visual truth and implementation evidence

Source references were compared with the implementation in the same visual review input:

- Dark module identity band: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-78ed1fac-b4d5-48f1-b39a-0c11bfe86729.png`
- Dark grid canvas: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-ed775b9e-c911-4727-859b-c6528febf919.png`
- Low-visibility favicon: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-4d5b16fd-7429-4080-b92e-a06af785589b.png`
- Narrow Subassembly controls: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-d8175ca6-5c8d-4f83-9d47-5f4ebd55b6e1.png`

Representative implementation captures:

- `apps/portal/src/Portal.Api/ClientApp/output/design-qa/portal-dark-final.png`
- `apps/project-tracker/src/ProjectTracker.Api/ClientApp/output/design-qa/project-tracker-dark.jpg`
- `apps/project-tracker/src/ProjectTracker.Api/ClientApp/output/design-qa/project-tracker-mobile-dark.png`
- `apps/engineering-hub/src/EngineeringHub.Api/ClientApp/output/design-qa/engineering-dark.jpg`
- `apps/estimating-dashboard/src/EstimatingDashboard.Api/ClientApp/output/design-qa/estimating-dark-final.png`
- `apps/estimating-dashboard/src/EstimatingDashboard.Api/ClientApp/output/design-qa/estimating-subassembly-controls-dark-final.png`
- `apps/estimating-dashboard/src/EstimatingDashboard.Api/ClientApp/output/design-qa/estimating-subassembly-controls-light-final.png`
- `apps/estimating-dashboard/src/EstimatingDashboard.Api/ClientApp/output/design-qa/estimating-subassembly-light-496x483.png`
- `apps/quality-assurance/src/QualityAssurance.Api/ClientApp/output/design-qa/quality-dark.jpg`

The full-screen comparison used the live 1280 x 720 desktop state. Compact checks used 820 x 900 and 520 x 800 across all five modules. The Subassembly regression check used the source width of 496 px with a 496 x 483 viewport. The supplied references are partial crops, so composition outside each crop was evaluated against the live product shell rather than treated as a pixel-match target.

## Fidelity review

### Layout and hierarchy

- The light Arda band is now a deliberate identity surface rather than a white logo tile.
- Dark mode uses a continuous graphite/navy band with reversed white/red artwork; the prior light rectangle is gone.
- The modules preserve their existing navigation and information hierarchy while sharing one branded shell.
- At 820 px the full lockups correctly collapse to the compact mark in dense module shells. At 520 px every module uses the compact mark and keeps persistent controls within the viewport.

### Color and surface system

- The light canvas is `#DDE8F0`; primary content surfaces are `#EAF1F6`, with `#E3EDF4` and `#DDE8F0` for secondary depth.
- The dark identity band is `#111D29` and was verified live as `rgb(17, 29, 41)`.
- Grid background images resolve to `none` on every module canvas in both themes.
- Remaining exact-white backgrounds are intentional control details or semantic foregrounds, not content-surface leaks.

### Typography and component styling

- Existing application typography and density were retained so the change reads as one brand system, not a redesign of each workflow.
- Form boundaries use the dedicated control-line token rather than relying on white contrast.
- Primary steel actions use `#2F6195` in both themes; this prevents semantic light-blue text tokens from becoming action backgrounds in dark mode.

### Imagery and iconography

- Standard and reversed Arda lockups/marks switch as real raster assets; no CSS filters or approximate logo drawings are used.
- The reversed artwork now preserves the standard files' alpha, red pixels, canvas, and visible bounds exactly. Theme switching therefore changes only the navy lettering/mark strokes to off-white; it does not resize or distort the identity.
- The favicon is a dedicated 512 x 512 PNG with a light plate and navy edge for dark browser chrome.
- Canonical assets and all public, build, and deployed copies passed 75/75 SHA-256 comparisons. The final reversed lockup and mark hashes are `A58BB67E...02C1F62` and `70BDCCE3...44DF209`; all live asset URLs returned HTTP 200 with `image/png` for the favicon.

## Interaction and responsive checks

- Theme switches were exercised in Portal, Project Tracker, Estimating, Engineering, and Quality Assurance.
- The Portal catalog and Admin Console were reviewed in light and dark states.
- The Estimating model switch was exercised through Standard and Subassembly states. At 496 px, the Subassembly and Add Row actions remain fully visible with no page-level horizontal overflow.
- All five modules were checked at 820 x 900 and 520 x 800. Each reported zero page-level horizontal overflow, zero visible Son-Aero logo assets, the expected theme-aware Arda asset, and zero browser-console errors.
- A final 1203 x 958 pass after the contrast and asset corrections again reported zero page-level horizontal overflow, zero visible Son-Aero logo assets, the reversed 164 px lockup at its expected 77.5 px rendered height, and zero browser-console errors across the module shells.
- The deployed post-build pass repeated those checks in both themes across all five modules. Every light canvas resolved to `rgb(221, 232, 240)` with `background-image: none`; every dark canvas also reported `background-image: none`.
- Corrected interactive states were exercised live in dark mode: Project Tracker's active My Projects control resolved to `#2F6195` with white text, Engineering's expanded drawing-preview control resolved to `#3C75AA` with white iconography, and Portal's Admin initials resolved to `#2F6195` with white text.
- Project Tracker's local SQLite lock surfaced during repeated QA navigation. Portal was temporarily stopped to release the shared local database, Project Tracker reloaded successfully, and Portal was restarted with its Development configuration. Project Tracker was then rechecked with Portal running and loaded normally with zero console errors. No database file was removed or replaced.

## Accessibility checks

- White action text on `#2F6195`: 6.43:1.
- White action text/iconography on the dark-theme steel hover `#3C75AA`: 4.86:1.
- White action text on `#CF3122` / hover `#A92317`: 5.10:1 / 7.14:1.
- White completion text on `#326B4D` / hover `#28563F`: 6.28:1 / 8.42:1.
- White QA action text on `#89530A` / hover `#6F4106`: 6.35:1 / 8.63:1.
- Reversed band ink `#F3F7FA` on `#111D29`: 15.83:1.
- Light primary ink `#101822` on `#EAF1F6`: 15.66:1.
- Light muted ink `#5F6B78` on `#EAF1F6`: 4.77:1.
- Light faint text `#536271` on the darkest light surface `#DDE8F0`: 5.03:1.
- Dark primary ink `#EDF2F7` on `#18212B`: 14.44:1.
- Dark muted ink `#96A4B3` on `#18212B`: 6.40:1.
- Dark faint text `#96A4B3` on the darkest common dark surface `#223241`: 5.16:1.
- Estimating's light focused input was exercised live: its border resolved to `#2F6195` with a 3 px focus halo, rather than being overwritten by the shared baseline border.

All listed text pairs meet WCAG AA for normal text.

## Iteration history and severity review

1. P1: the Estimating dark-theme New quote action initially inherited the light-blue semantic `--steel-700` token, producing weak white-text contrast. It was moved to the dedicated `--steel-action` token and reverified live at `rgb(47, 97, 149)` with white text.
2. P2: Portal dark mode initially retained its older `rgb(14, 22, 32)` topbar override. Shared shell specificity was corrected and the live band now resolves to the canonical `rgb(17, 29, 41)`.
3. P1: the first shared form-boundary rule could override module focus and invalid states because it loaded last with higher specificity. The complete baseline selector is now wrapped in `:where(...)`, giving it zero specificity; the live Estimating focus check confirms the module state wins.
4. P1: the decorative `--faint` token was being used for real 8.5–10 px text. Text uses now resolve through `--faint-text` (`#536271` light / `#96A4B3` dark); all remaining `--faint` references are icon-only or decorative.
5. P1: filled red, amber, and green controls inherited bright semantic indicator colors with insufficient white-text contrast. Dedicated action and hover tokens now provide 5.10:1–8.63:1 contrast without dulling semantic dots, rails, and charts.
6. P1: the first reversed logo export reduced the visible artwork by roughly 35% at the shared lockup width. The final reversed lockup and mark were regenerated from the exact standard alpha and red geometry; rendered size now remains constant through a theme change.
7. P2: obsolete `--grid-line` and `--estimating-grid-line` declarations remained after the canvas pattern was removed. The unused declarations and documentation example were deleted, and a repository scan now finds no grid-token references.
8. P1: several selected/active steel controls still used the bright semantic `--steel` token in dark mode, and Portal's Admin initials used the light dark-theme `--ink-2` token as a fill. The controls now use `--steel-action`/`--steel-action-hover`, and the Admin avatar uses `--steel-action`; the corrected states were verified live.
9. P1: small error, late-state, audit, date, and status text in Portal, Project Tracker, Estimating, and Engineering still referenced indicator-strength red/risk colors. Text-bearing declarations now use `--red-700` or `--risk-700`, while decorative borders, icons, rails, and dots retain the brighter semantic colors.
10. P0/P1/P2 final review: no unresolved visual, interaction, accessibility, asset-loading, or responsive defects were found in the requested scope.

## Verification

- Production builds: 5/5 passed.
- Linters: 5/5 exited successfully; Project Tracker retains 57 pre-existing non-blocking React refresh/dependency warnings.
- Portal tests: 24/24 passed.
- Project Tracker guide tests: 38/38 passed.
- Project Tracker service-worker tests: 5/5 passed.
- Estimating tests: 26/26 passed, including Subassembly roll-up and populated workbook export coverage.
- Engineering Hub and Quality Assurance do not define frontend test scripts; both production builds and linters passed.
- Estimating retains a non-blocking Vite size warning for the lazy workbook-export chunk (936.90 kB minified / 258.62 kB gzip).
- Live roots, favicons, and reversed lockups: HTTP 200 on ports 5135, 5140, 5150, 5160, and 5170.

## Blue action and accent system follow-up

Date: 2026-08-25

This follow-up supersedes the earlier action-color references above. All filled actions, active navigation/focus treatments, generic accent rules, notification counts/actions, and interactive emphasis now use Arda blue. Destructive and late/error semantics use amber so red is not reintroduced as a workflow color. Red is reserved for the Arda shell separator and the Back-to-Arda label/hover underline; the red orbit within the supplied Arda logo artwork remains unchanged brand artwork.

### Reference comparison

- Source red Quote Workspace rule: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-4325e1eb-e06c-43f5-9433-29aa639fb637.png`
- Final blue Quote Workspace card: `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\estimating-quote-workspace-crop.png`
- Source reserved Back-to-Arda treatment: `C:\Users\USER\AppData\Local\Temp\codex-clipboard-49811348-ff4c-4a31-9fc1-ddf500328b39.png`
- Final Portal shell with reserved separator: `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\portal-dark.png`

The source and implementation images were opened together in the same comparison inputs. The requested change is visibly isolated: the Estimating pipeline top rule changed from red to `#2F6195`, while the shell separator remains red.

### Live module evidence

The in-app browser was used at its 757 x 677 viewport. Final light and dark captures were reviewed for all five modules:

- Portal: `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\portal-light-final.png`, `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\portal-dark.png`
- Project Tracker: `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\project-tracker-light-final.png`, `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\project-tracker-dark-blue-benny.png`
- Engineering Hub: `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\engineering-light-final.png`, `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\engineering-dark.png`
- Estimating Dashboard: `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\estimating-light-final.png`, `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\estimating-dark.png`
- Quality Assurance: `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\quality-light-final.png`, `C:\Users\USER\AppData\Local\Temp\arda-blue-qa\quality-dark.png`

Project Tracker's Benny artwork is hue-shifted at render time so its interactive avatar is blue without altering the source animation geometry or white face details.

### Verification

- Visible computed-color scan, light and dark: the only red CSS surface was the approved Portal shell separator; no non-approved visible red accents were found in the other four live screens.
- Quote Workspace top border: `rgb(47, 97, 149)`, 3 px.
- Project Tracker notification count: `rgb(47, 97, 149)` with white text.
- Browser console errors: 0 across all five live modules in both reviewed themes.
- HTTP health: 200 on ports 5135, 5140, 5150, 5160, and 5170 after production assets were deployed.
- Production builds and linters: passed for all five clients. Project Tracker retains its existing non-blocking Fast Refresh and exhaustive-dependency warnings.
- Estimating tests: 26/26 passed.
- Source red-literal audit: exact red values are limited to the Arda shell separator and Back-to-Arda tokens/hover treatment, plus named constants for those two reserved uses.

final result: passed

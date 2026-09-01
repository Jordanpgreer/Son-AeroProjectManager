# Arda Shell Branding Design QA

## Comparison targets

- Source visual truth:
  - `C:\Users\USER\AppData\Local\Temp\codex-clipboard-e7cc31e2-0650-4225-a576-c281d2be7fb2.png` (Portal header before integration, 494 x 222 PNG)
  - `C:\Users\USER\AppData\Local\Temp\codex-clipboard-6acacebf-2e38-4b16-b66e-3d9df5be19d9.png` (Project Tracker shell before integration, 425 x 290 PNG)
- Exact-size implementation comparisons:
  - `output/design-qa/portal-header-exact-494x222.jpg` (494 x 222 JPEG)
  - `..\..\..\..\project-tracker\src\ProjectTracker.Api\ClientApp\output\design-qa\project-brand-project-detail-exact-425x290.jpg` (425 x 290 JPEG)
- Full-view implementation evidence:
  - `output/design-qa/portal-default.jpg` (1193 x 950 JPEG; default 1203 x 958 CSS viewport with browser scrollbar area excluded)
  - `..\..\..\..\project-tracker\src\ProjectTracker.Api\ClientApp\output\design-qa\project-tracker-expanded.jpg` (1280 x 720 JPEG)
  - `..\..\..\..\estimating-dashboard\src\EstimatingDashboard.Api\ClientApp\output\design-qa\estimating-arda.jpg` (1280 x 720 JPEG)
- Focused and responsive evidence:
  - `output/design-qa/portal-mobile-light.jpg` (380 x 822 JPEG from a 390 x 844 CSS viewport)
  - `output/design-qa/portal-mobile-dark.jpg` (380 x 822 JPEG from a 390 x 844 CSS viewport)
  - `..\..\..\..\project-tracker\src\ProjectTracker.Api\ClientApp\output\design-qa\project-tracker-collapsed.jpg` (260 x 360 focused crop)
  - `..\..\..\..\project-tracker\src\ProjectTracker.Api\ClientApp\output\design-qa\project-tracker-mobile-revised.jpg` (390 x 844 JPEG)
  - `..\..\..\..\engineering-hub\src\EngineeringHub.Api\ClientApp\output\design-qa\engineering-arda.jpg` (520 x 360 focused crop)
  - `..\..\..\..\quality-assurance\src\QualityAssurance.Api\ClientApp\output\design-qa\quality-arda-brand.jpg` (240 x 120 focused crop)

All exact-size comparisons used device scale factor 1. The source and implementation crops were normalized to the same pixel dimensions before comparison. The Portal state was the light application catalog. The Project Tracker state was the light, expanded Project Detail screen for `Test 2`, matching the supplied reference state.

## Findings

- No actionable P0, P1, or P2 design differences remain.
- The supplied transparent Arda artwork is used directly. `arda-lockup.png` is 1825 x 862 `Format32bppArgb` with alpha 0 at the canvas corner; `arda-mark.png` is 1254 x 1254 with the same transparent-corner result. The opaque `arda-lockup-on-white.png` is not used by the application shells.
- The former Portal white logo card is gone. The logo surface computes to transparent background, no border, no radius, and no shadow. The entire 77 px header is the light steel-blue brand surface with a red lower rule.
- The module shells use the same edge-to-edge brand cap above the existing dark navigation. The expanded Project Tracker cap is 231 x 107.45 CSS px and the lockup is 164 x 77.45 CSS px. The collapsed cap is 73 x 92 CSS px and swaps to a centered 48 x 48 mark.
- Desktop, collapsed, and 390 x 844 mobile states had zero document-level horizontal overflow. Portal light and dark themes keep the brand band light so the navy/red artwork remains legible without filters or inversion.

## Required fidelity surfaces

- Fonts and typography: Existing Inter and IBM Plex Mono application typography was preserved. Arda is rendered from the supplied artwork, avoiding a substituted text recreation. The Portal kicker, module headings, and active navigation hierarchy remain aligned and readable with no new wrapping in the compared regions.
- Spacing and layout rhythm: The new Portal band and sidebar caps are structural, edge-to-edge surfaces rather than nested cards. Existing navigation spacing, active states, content grids, and module-specific shell proportions were retained. The matched Project Detail crop preserves the original dark navigation density and red active state.
- Colors and visual tokens: Shared tokens use steel blue `#eaf1f6`, Arda navy `#07325f`, muted navy `#385773`, and red `#e23b2c`. The implementation deliberately changes the old dark logo background to a light identity band, as requested, while retaining the dark module navigation below it.
- Image quality and asset fidelity: Both web assets are the supplied transparent PNGs, copied with matching SHA-256 hashes into all five ClientApps. Natural dimensions load successfully in the browser. No filter, handcrafted SVG, CSS drawing, white backing image, or approximation is used.
- Copy and content: Portal, Admin, Project Tracker, Estimating, Engineering, and Quality application identity, browser titles, favicons, return labels, and Project Tracker notification icons now use Arda. Windows-domain account values, equipment owner data, compatibility identifiers, and corporate/customer report identity remain unchanged because they are company data rather than application-shell branding.
- Icons and interactions: Existing Lucide navigation and action icons were retained. The Arda return link has a descriptive accessible label and decorative nested images use empty alt text. Portal images use `alt="Arda"`. Focus-visible, collapse/expand, active navigation, reduced-motion, and mobile shell behavior remain available.

## Browser and interaction checks

- Portal: desktop header, 390 x 844 mobile header, light/dark theme toggle, transparent lockup-to-mark swap, zero horizontal overflow.
- Project Tracker: expanded sidebar, collapsed sidebar, Project Detail active state, 390 x 844 compact topbar mark, collapse/expand restoration, zero horizontal overflow.
- Estimating: desktop calculator at `#/calculator`, Arda cap, Arda title, and visible Standard/Rubber/Subassembly model selector.
- Engineering and Quality: desktop brand-cap smoke checks, Arda titles, correct asset loading, and zero horizontal overflow.
- Browser consoles: no warnings or errors were present in the final Portal, Project Tracker, or cross-module smoke tabs.
- Local previews: HTTP 200 for each application root and both Arda assets on ports 5135, 5140, 5150, 5160, and 5170.

## Comparison history

1. Initial mobile pass found a P2 containment issue: the shared Arda mark computed to 38 x 38 CSS px inside Project Tracker's inherited 36 x 36 return-link box, creating a one-pixel overflow on each side.
2. Fixed `shared/branding/arda-shell.css` so compact return links are 42 x 42 CSS px with centered alignment and a 38 x 38 mark.
3. Rebuilt and redeployed all five clients. The post-fix 390 x 844 capture confirms the image is fully contained, there is zero horizontal overflow, and the successful application state is visible.
4. Repeated the exact-size Portal and Project Detail comparisons. No P0, P1, or P2 issue remained. Focus styling was normalized by capturing the Project Detail state without an incidental click focus ring.

## Follow-up polish

- Complete: the workstation-installer pipeline uses the sole approved multi-resolution `arda-transparent.ico` asset. Corporate/customer report logos remain a separate branding decision.

final result: passed

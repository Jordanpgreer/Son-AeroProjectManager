# Arda Application Branding

Canonical source for the Arda application identity and design tokens shared across the
internal applications in this repository. Legacy corporate artwork remains available for
controlled reports and other material that still requires company identification.

## Contents

| Path | Purpose |
|---|---|
| `web/arda-lockup.png`, `web/arda-mark.png` | Transparent Arda wordmark and compact application mark for light identity bands |
| `web/arda-lockup-reversed.png`, `web/arda-mark-reversed.png` | High-contrast transparent artwork for dark identity bands |
| `web/arda-favicon.png` | Theme-neutral browser and touch icon with a light visibility plate |
| `arda-transparent.ico` | The sole Windows desktop icon: a multi-resolution icon generated from the official transparent Arda mark, without a background plate or border |
| `SON-AERO_logo-transparent.png`, `SON-AERO_logo-white-07.png`, `SON-AERO_jpg_SON-AERO SQ 2.gif` | Legacy corporate source logos retained for controlled uses |
| `web/` | Web-ready source assets; the sync script allowlists the Arda application assets |
| `brand-tokens.css` | Canonical design tokens (colors, type, radii, elevation) |
| `arda-shell.css` | Shared responsive Arda header/sidebar treatment |
| `brand.ts` | Shared blue action-accent constants, reserved red separator/back-label colors, and the application-registry type |

## Distributing assets to the apps (Windows-safe, no symlinks)

Each frontend serves its logos from its own `public/brand` folder so it can build and run
independently. To refresh those copies from this canonical source, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Sync-Branding.ps1
```

The script copies the light and reversed Arda lockups, compact marks, and favicon into every
app's `ClientApp/public/brand`, then copies the shared tokens and shell styles into each
`ClientApp/src`. It uses plain file copies (not symlinks) so it works for any user on any
Windows checkout.

## Design token usage

`brand-tokens.css` is the single source of truth for shared tokens. Each frontend imports a
synced copy from its `src` directory. Update the canonical file first, then run the branding
sync script to distribute the change consistently.

Arda blue is the shared action, control, notification, and accent color. Red is reserved for
the Arda identity separator and the “Back to Arda” hover label; warning and risk states use
the amber semantic family.

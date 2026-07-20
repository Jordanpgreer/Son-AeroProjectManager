# SON-AERO Branding

Canonical source for SON-AERO brand assets and design tokens shared across the internal
applications in this repository.

## Contents

| Path | Purpose |
|---|---|
| `SON-AERO_logo-transparent.png`, `SON-AERO_logo-white-07.png`, `SON-AERO_jpg_SON-AERO SQ 2.gif` | Original source logos |
| `son-aero.ico` | Red SON-AERO icon used by the desktop shortcut |
| `web/` | Web-ready assets copied into each frontend's `public/brand` |
| `brand-tokens.css` | Canonical design tokens (colors, type, radii, elevation) |
| `brand.ts` | Shared brand color constants and the application-registry type |

## Distributing assets to the apps (Windows-safe, no symlinks)

Each frontend serves its logos from its own `public/brand` folder so it can build and run
independently. To refresh those copies from this canonical source, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Sync-Branding.ps1
```

The script copies `shared/branding/web/*` into every app's `ClientApp/public/brand`. It uses
plain file copies (not symlinks) so it works for any user on any Windows checkout.

## Design token usage

`brand-tokens.css` is the single source of truth for the shared tokens. The app frontends
currently inline the same token set in their own `index.css`; when the brand changes, update
`brand-tokens.css` first, then mirror the change into each app.

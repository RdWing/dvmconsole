# DVM Console NEO artwork

The editable SVG marks in this directory are the source artwork for the DVM
Console NEO application icon and its rendered PNG/ICO/ICNS derivatives. The
README wordmark and current social preview are separate raster artwork,
described below. The 1200 × 630 social-card pair is retained as earlier artwork.

## Inventory

- `dvm-console-neo-mark-color.svg`: primary full-color mark.
- `dvm-console-neo-mark-on-dark.svg`: monochrome template for dark fields.
- `dvm-console-neo-mark-on-light.svg`: monochrome template for light fields.
- `dvm-console-neo-mark-optical-{16,24,32}.svg`: small-size optical sources.
- `dvm-console-neo-mark-{16,24,32,1024}.png`: rendered raster sizes.
- `dvm-console-neo-social-card.svg`: editable project and release social preview.
- `dvm-console-neo-social-card.png`: rendered 1200 x 630 social preview.

The compatibility-facing application assets are generated from these sources
at `src/DvmConsole.Desktop/Assets/DVMConsole.png`,
`src/DvmConsole.Desktop/Assets/DVMConsole.ico`, and
`packaging/macos/DVMConsole.icns`.

## License

Original DVM Console NEO artwork in this directory and its tracked derivatives
is licensed under `AGPL-3.0-only`, the same license as the application. Preserve
the repository license and notices when redistributing or modifying it.

The NEO marks identify this independently maintained downstream project. Their
use must not imply endorsement by DVMProject or suitability for public- or
life-safety operation.

## NEO wordmark direction

The README and new social preview use the large cyan NEO wordmark, with blue
input and green output accents on a dark ink field. The headline is
**Many channels. One console.** Use **DVM Console NEO** in prose and keep the
existing routed-N application icon for small sizes.

- `dvm-console-neo-readme-banner.png`: 1896 × 830 README header.
- `dvm-console-neo-social-preview.png`: 1731 × 909 social artwork, approximately
  1.91:1, including macOS, Windows, and Linux.

These two PNGs are generated raster artwork developed from the existing mark
and the selected NEO wordmark concept. They are not exports from the legacy
social-card SVG. The older `dvm-console-neo-social-card.svg` and matching PNG
remain as previous artwork; use `dvm-console-neo-social-preview.png` for new
social previews. Uploading it as the repository's social image is a separate
GitHub settings action.

Keep the wordmark proportions, leave breathing room around it, and avoid glow,
extra badges, or site-specific radio identifiers. Product screenshots should
come from the public demo and remain unmodified. Supporting copy and links
belong in Markdown so they stay readable on narrow screens and in both themes.
The banner is repository artwork; it does not change in-app operational colors,
controls, or the packaged application icon.

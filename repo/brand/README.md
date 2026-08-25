# DVM Console NEO artwork

The editable SVG marks in this directory are the source artwork for the DVM
Console NEO identity. The rendered PNGs, the application PNG/ICO/ICNS files,
and the 1200 x 630 social card are distribution derivatives.

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

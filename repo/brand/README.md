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

The packaged application icons are generated from these sources and stored at
`src/DvmConsole.Desktop/Assets/DVMConsole.png`,
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

The README and current social preview use the large cyan NEO wordmark, with blue
input and green output accents on a dark background. The headline is
**Many channels. One console.** Use **DVM Console NEO** in prose and keep the
existing routed-N application icon for small sizes.

- `dvm-console-neo-readme-banner.png`: 1896 × 830 README header.
- `dvm-console-neo-social-preview.png`: 1731 × 909 social artwork, approximately
  1.91:1, including macOS, Windows, Linux, iOS, and iPadOS.

These two PNGs combine the existing mark with the NEO wordmark. They were created
separately from the older social-card SVG. The older `dvm-console-neo-social-card.svg` and matching PNG
remain as previous artwork; use `dvm-console-neo-social-preview.png` for new
social previews. Uploading it as the repository's social image is a separate
GitHub settings action.

Keep the wordmark proportions, leave breathing room around it, and avoid glow,
extra badges, or site-specific radio identifiers. Product screenshots should
come from the public demo and remain unmodified. Supporting copy and links
belong in Markdown so they stay readable on narrow screens and in both themes.
The banner is repository artwork; it does not change in-app operational colors,
controls, or the packaged application icon.

The v0.8.0 social preview lists all five platforms. iPhone and iPad builds are
available through the [public TestFlight beta](https://testflight.apple.com/join/KuYtQqja).
Keep availability details in the README and release notes so they can be updated
without changing the artwork. The README banner is platform-neutral.

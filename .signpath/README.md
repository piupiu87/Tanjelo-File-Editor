# Signing with SignPath

Releases are signed through [SignPath Foundation](https://signpath.org), which signs open-source projects for free with
a certificate issued to SignPath Foundation. The README's *Code signing policy* says who does what.

The release workflow (`.github/workflows/build.yml`) signs only tagged releases, and only once these exist in the
repository's settings, under *Secrets and variables → Actions*. Until then it builds and releases unsigned, the same as
before.

| Kind | Name | What |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | The API token of a SignPath CI user that may submit signing requests to the project |
| Secret | `SIGNPATH_ORGANIZATION_ID` | The SignPath organization's ID |
| Variable (optional) | `SIGNPATH_PROJECT_SLUG` | The project's slug on SignPath; `Tanjelo-File-Editor` when not set |
| Variable (optional) | `SIGNPATH_SIGNING_POLICY_SLUG` | The signing policy's slug; `release-signing` when not set |
| Variable | `SIGNPATH_CERTIFICATE_SUBJECT` | The signing certificate's subject, such as `CN=SignPath Foundation, …`, exactly as Windows shows it: the Windows 11 menu entry's package names it as its publisher |

The project on SignPath needs the two artifact configurations in `artifact-configurations/`, with the slugs `app` and
`installer`, and GitHub as its trusted build system.

Each release makes two signing requests, each waiting up to five hours for its approval on SignPath:

1. **app:** `Tanjelo.exe`, `tanj.exe`, Tanjelo's own DLLs and the Windows 11 menu entry (`TanjeloMenu.dll` and its
   package, `Tanjelo.ShellMenu.msix`), before the zip and the installer are made from them.
2. **installer:** `Tanjelo-<version>-Setup.exe`, made by Velopack from the signed files.

The workflow checks each signature before going on, and the winget manifests take the signed installer's SHA-256.

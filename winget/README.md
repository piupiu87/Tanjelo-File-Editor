# winget manifests

The manifests for `winget install Tanjelo` (package `Tanjelo.Tanjelo`), both from `Tanjelo-<version>-Setup.exe`, which
installs silently with `--silent` and puts `tanj` on the PATH:

- **user** scope (the default): `%LOCALAPPDATA%\Programs\Tanjelo`, no administrator needed;
- **machine** scope (`winget install Tanjelo --scope machine`): Program Files, with `--all-users`.

`winget install --location <folder>` passes `--dir`, and Tanjelo goes in a `Tanjelo` folder inside it. The product code
is `Tanjelo`, the name of its Apps entry, as it was for 1.0 and 1.1, so winget sees one product across versions.

The files here are for the version in `Directory.Build.props`, with a placeholder SHA-256. For each release, the
workflow fills in the version, the installer's address and its SHA-256, checks them with `winget validate`, and attaches
them to the release as `winget-manifests.zip`.

To publish a release on winget, submit those files as a pull request to
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), in `manifests/t/Tanjelo/Tanjelo/<version>/`, from
your own GitHub account (or with `wingetcreate submit`). Microsoft's checks install it on a clean machine before the
pull request is merged.

winget reads every file in the folder it's given, this README too, so to check the files here, copy them out first:

```
$dir = New-Item -ItemType Directory -Force "$env:TEMP\tanjelo-winget"
Copy-Item winget\*.yaml $dir
winget validate --manifest $dir.FullName
```

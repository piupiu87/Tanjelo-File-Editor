# winget manifests

The manifests for `winget install Tanjelo` (package `Tanjelo.Tanjelo`): a per-user install from
`Tanjelo-<version>-Setup.exe`, which installs silently with `--silent` and puts `tanj` on the PATH.

The files here are for the version in `Directory.Build.props`, with a placeholder SHA-256. For each release, the
workflow fills in the version, the installer's address and its SHA-256, checks them with `winget validate`, and attaches
them to the release as `winget-manifests.zip`.

To publish a release on winget, submit those files as a pull request to
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), in `manifests/t/Tanjelo/Tanjelo/<version>/`, from
your own GitHub account (or with `wingetcreate submit`). Microsoft's checks install it on a clean machine before the
pull request is merged.

Check the files here with:

```
winget validate --manifest winget
```

# Program Manager 0.3.1 verification

## Settings and connection changes

- Native Settings tabs separate General, Host distribution, and Client connection. Main-page shortcuts open the corresponding tab; connection-code generation and GitHub configuration live inside Host settings.
- Host controls are disabled when the distribution role is off. The generated code is read-only; the received client code is editable.
- Local IPv4 discovery excludes loopback, link-local and non-unicast addresses. Host-code generation checks the advertised endpoint before copying the code.
- Client checks exercise a real local TLS catalog server with the correct certificate pin and authentication token, malformed codes, token rejection, certificate mismatch, unavailable port and cancellation.
- Applying a valid code immediately fills the main catalog and preserves existing programs and GitHub credentials/selections. Invalid codes leave the settings file and catalog unchanged.
- Tray actions cannot overlap the modal Settings window. Applied one-time connection inputs are cleared, and address changes update the host status.
- Update result-file writes and archival reuse the existing bounded Windows sharing-error retry helper.

## Automated verification

`tools/build.ps1` builds and runs Core and Desktop checks for .NET 8 and .NET Framework 4.8, cross-runtime TLS transfer checks, layout checks, and both Inno Setup installers.

Local Core and Desktop checks passed for both runtimes. Layout checks passed at native 250% DPI and simulated 100%, 125%, 150%, 200%, and 250% sizes. Simulation changes only the test process's geometry and fonts. Settings screenshots were inspected; fields, descriptions and action buttons remain readable and reachable when the window is reduced.

The layout runner scrolls the actual nested button into view, rather than its whole containing table. Hosted CI with a smaller desktop skips simulated viewports that exceed its physical screen and still checks the native viewport.

Windows 7 support is checked through the .NET Framework build and cross-runtime tests on current Windows; a physical Windows 7 machine was not exercised in this run.

## Published and installed verification

- Release tag `v0.3.1` points to `df1b06577aea36754565a9f09fd948ff101973e0`. [GitHub Actions run 34445503067](https://github.com/yunhyok/ProgramManager/actions/runs/34445503067) passed both the Windows build and release publication jobs.
- The first hosted check exposed a legacy ComboBox height mismatch at 96 DPI. The address field now uses a native autosizing container; the fix passed the local 96 DPI reproduction, normal local DPI runs, and hosted checks. The failed, unpublished tag was updated before any 0.3.1 release existed.
- Both public installers were downloaded through the actual manager updater and validated against product metadata, asset digests, and the published SHA256SUMS file. A client with its Internet HTTP handler blocked received the update through a pinned local TLS host.
- The published modern installer upgraded the existing installation from 0.3.0 to 0.3.1, backed up prior program files and restarted successfully. All 19 registered programs, nine selected repositories and the settings file were preserved byte-for-byte during installation.
- The previously unresolvable advertised host name was then changed to an active local IPv4 address while the app was stopped. All other settings were preserved. After restart, an authenticated request using that LAN address returned the nine published apps.
- Native accessibility inspection confirmed the installed 0.3.1 title, 19 registered programs, and the settings navigation guidance. Real form rendering and layout checks cover the Settings tabs; this environment's native pointer automation was unavailable.

| Published installer | Size | SHA-256 |
| --- | ---: | --- |
| ProgramManager-Setup-0.3.1.exe | 51,064,288 | `5fff63299d6fd85d29155bde1dd9f5f1fe5cd5391064e9c2715f47a75e8e122e` |
| ProgramManager-Setup-0.3.1-win7.exe | 2,426,037 | `3c4596b3863a299e5f48c19dbd9d4e93132822a15657787ddbf9bd007555743e` |

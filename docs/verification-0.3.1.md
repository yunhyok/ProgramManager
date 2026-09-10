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

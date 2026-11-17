# Display and window diagnostics

This optional Windows developer tool inspects display modes and window geometry.
It does not apply a display mode, move or resize windows, start a workspace,
invoke UU UI Automation or write recovery records.

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project tools/display-probe -c Release
dotnet run --project tools/display-probe -c Release -- --validate-fallbacks
dotnet run --project tools/display-probe -c Release -- --recovery-window-state
dotnet run --project tools/display-probe -c Release -- --restore-constraints
```

- No arguments: list connected display bounds, work areas, scaling, current
  modes and the modes advertised by verified UU virtual displays. This does not
  read the recovery journal.
- `--validate-fallbacks`: check whether the driver advertises and accepts
  1200 × 1920 and its exact width/height swap, 1920 × 1200. Validation uses
  `CDS_TEST`; it does not apply the mode or change persistent display settings.
  Acceptance is not proof that a later real mode switch will succeed.
- `--recovery-window-state`: read the existing recovery journal and compare
  saved window geometry, show state and placement flags with the current state.
  Observed fields are explicitly allowlisted; executable paths, process IDs,
  window handles and process start timestamps are not emitted.
- `--restore-constraints`: read the existing journal and query current window
  geometry, DPI and minimum/maximum size constraints. Despite the option name,
  it does not restore or change the windows.

The normal reports omit personal filesystem paths and window titles. Recovery
diagnostics still include application process names, and all reports reveal some
display or window configuration. Review the complete output before sharing it;
exceptions or future diagnostic changes may require additional redaction. Do not
upload raw recovery journals or unrelated application logs with a report.

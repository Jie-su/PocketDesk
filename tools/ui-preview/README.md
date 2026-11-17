# PocketDesk markup preview

This tool renders the current product `MainWindow.xaml` and `App.xaml` resources
with example data. It loads a plain WPF `Window`, removes all code-behind event
attributes and never constructs `PocketDesk.App` or `PocketDesk.MainWindow`.
It does not run session recovery, controller polling, native services or display
mode changes, and does not read or write user preferences.

From the repository root:

```powershell
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --all
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --width 874 --height 402 --theme dark --page settings --scroll 1
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --width 804 --height 1748 --scale 2 --theme dark --output artifacts/ui-review/scale-contract
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --width 874 --height 402 --theme dark --state confirm-recovery --output artifacts/ui-review/short-confirm
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --all --effective --output artifacts/ui-review/effective
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --all --effective --toolbar --output artifacts/ui-review/toolbar
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --show-toolbar --width 810 --height 402 --theme dark --seconds 180
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --all --frame-fit --output artifacts/ui-review/frame-fit
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --all --frame-supported --output artifacts/ui-review/frame-swapped
.\.tools\dotnet\dotnet.exe run --project tools/ui-preview -c Release -- --all --frame-supported --toolbar --output artifacts/ui-review/frame-swapped/toolbar
```

`--all` writes 20 PNGs and `layout-report.json` to `artifacts/ui-review`: five
logical viewports (402×874, 874×402, 834×1194, 1194×834, 960×720), two palettes,
and two pages. Main-window matrices render each case in an isolated offscreen
preview process to avoid WPF reusing stale render data between detached windows.
`--scroll 1` captures the bottom of the scrollable page instead.
Use `--output <directory>` to keep separate reports. `--show` optionally displays
one inert preview window; Escape closes it. Buttons have no product actions.
`--state confirm`, `recovery` or `confirm-recovery` exposes the resolution
confirmation and/or manual recovery footer with a long status message.
`--scale 2` applies the real WPF layout transform before measuring, so an
804×1748 viewport must choose the 402×874 phone layout. No display DPI is changed.
`--all --effective` uses the four remaining viewports after the configured UU
overlay insets: 402×692, 810×402, 834×1012 and 1130×834. Portrait reserves top
and bottom 91 points; landscape reserves 64 points on the right. These are layout
fixtures, not measurements of the mobile client's actual toolbar.
`--landscape-only` limits a matrix to its two landscape cases, useful after a
landscape-only policy change. The previous bottom-toolbar alternative can still
be rendered explicitly with `--width 874 --height 338`.
`--all --frame-fit` separately checks 402×668, 746×378, 834×976 and 1066×798.
Those reference workspaces assume a 72-physical-pixel Windows taskbar at 150%
Windows DPI; the actual taskbar and work area can differ. This option does not
replace the original matrices or change any display mode.
`--all --frame-supported` checks only 402×619 and 643×378 (eight images): the
reference phone workspaces for exact width/height-swapped 1200×1920 portrait
and 1920×1200 landscape modes, assuming 150% Windows DPI, a 72-physical-pixel taskbar and
proportional content scaling. This is also an offscreen layout fixture, not a
display switch or validation that the current driver accepts those modes.
The earlier 715×375 / 1920×1080 images in `frame-supported` are historical;
`frame-swapped` contains the current exact-swap fixture. Combining `--toolbar`
with this preset renders its real toolbar and unopened operations menu as well.

`--toolbar` constructs the real `ToolbarWindow` with inert callbacks, then renders
its content and its unopened Popup child. It never calls `Show`, opens the popup
or creates a toolbar HWND; it asserts that no source initialization or action
callback occurred. `toolbar-report.json` records button bounds, their 44-point
minimum size and viewport containment. Popup event handling, focus and actual
placement above the strip still require interactive verification.
`--show-toolbar` is the explicit interactive exception: it shows only the real
toolbar fixture, with all product action callbacks replaced by console logging.
It never constructs the product main window, controller/native services, session
or recovery. The fixture closes automatically after 180 seconds (configurable
with `--seconds 10–600`). This mode is intended for a human/CUA to test the actual
popup, Escape dismissal and return button; rendering commands never enable it.

The tool calls the product's pure `WorkspaceLayout.Apply` helper and reads the
product theme palette without constructing its persistent theme service. These
two narrow adapters fail explicitly if their API changes. No hand-built UI copy
is kept here. New custom XAML namespaces are rejected until reviewed.

PNG dimensions are logical coordinates at 96 DPI, so a 402-pixel PNG represents
402 layout units, not the actual device's physical pixel density. The report
lists named-element geometry and warns about horizontal scroll overflow or
navigation outside the surface. It also checks the five presets' expected app
columns (1/2/2/3/3), the first window action's visibility and DPI rounding that
could force an extra WrapPanel row. Exit codes: 0 means no geometry warnings,
2 means warnings were found, and 1 means loading/rendering failed.
The report is diagnostic, not a claim that every text
line fits: vertical scrolling and title ellipsis can be intentional. Product
code-behind events, real app icons, interaction behavior, OS window chrome,
multi-monitor placement and device scaling still require separate verification.

The combined confirmation/recovery scene can report a partially visible first
application row on a short landscape viewport. This is not evidence that a
required operation is blocked: the real product requires Keep/Revert before
opening an application during a resolution trial. Verify those confirmation
buttons and the manual recovery exit separately; do not shrink their touch
targets merely to clear this diagnostic. The preview intentionally does not
execute that product interaction logic.

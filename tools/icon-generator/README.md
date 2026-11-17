# PocketDesk icon source

`Program.cs` contains the shared 256-unit vector scene. It generates the editable SVG,
transparent PNG preview, and all Windows icon sizes without external packages.

From the repository root:

```powershell
.\.tools\dotnet\dotnet.exe run --project tools\icon-generator\icon-generator.csproj -c Release
```

Outputs are `src/PocketDesk/Assets/PocketDesk.svg`, `PocketDesk.ico`, and `preview.png`.
ICO entries are 16, 24, 32, 48, 64, 128, and 256 px. Smaller entries use 32-bit DIB
with alpha and a transparent AND mask; 256 px uses PNG. The generator decodes the
finished ICO with WPF and checks every required size. Edit the `Artwork` scene and
regenerate to keep SVG and ICO synchronized. No display settings or user windows
are changed.

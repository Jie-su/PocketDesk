# Third-party notices and acknowledgements

PocketDesk implements its Windows behavior in C#, WPF and Win32. The projects
and public specifications below informed its interaction design, device data and
implementation decisions. PocketDesk is not an official release of Snug,
FlClash, Microsoft, Apple or NetEase.

## Snug v2.4.4

Project: [zephyr4123/snug](https://github.com/zephyr4123/snug/tree/v2.4.4)

Snug was the reference for the mobile workspace experience, application/window
library, restoration workflow and the use of a screen aspect ratio that leaves
room for UU's client toolbar. PocketDesk's initial portrait allowance of 91
logical points at each end comes from Snug's documented empirical value. The
iPhone 17 Pro profile's 1206 × 2622 pixels and 402-point logical width were
cross-checked against Snug's device data; the physical resolution is also
documented by Apple.

Referenced materials include:

- [PhoneHomeView.swift](https://github.com/zephyr4123/snug/blob/v2.4.4/Sources/Snug/PhoneScreen/PhoneHomeView.swift)
  and [PhoneStyle.swift](https://github.com/zephyr4123/snug/blob/v2.4.4/Sources/Snug/PhoneScreen/PhoneStyle.swift):
  application-library presentation and interaction references.
- [ControllerTable.swift](https://github.com/zephyr4123/snug/blob/v2.4.4/Sources/SnugCore/Session/ControllerTable.swift):
  understanding the need to scope incoming-controller evidence to the local
  controlled-computer panel.
- [phone-models.json](https://github.com/zephyr4123/snug/blob/v2.4.4/Assets/phone-models.json):
  device specifications and logical-width reference.
- [How it works](https://github.com/zephyr4123/snug/blob/v2.4.4/docs/how-it-works.md):
  workspace lifecycle, restoration and toolbar-clearance design.

PocketDesk does not bundle Snug's macOS executable, virtual-display components,
vendored dependencies or reference screenshots. The following copyright and
permission notice is retained for the Snug-derived references and data. It is
reproduced from the [v2.4.4 LICENSE](https://github.com/zephyr4123/snug/blob/v2.4.4/LICENSE),
Git blob `922a82dc111d139570e87b58e7087d7e16cca4cb`.

```text
MIT License

Copyright (c) 2026 AoyamaShou
Copyright (c) 2026 zephyr4123

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## FlClash visual reference

Project: [chen08209/FlClash](https://github.com/chen08209/FlClash)

FlClash's desktop interface was visually consulted for the wide-screen side
navigation, narrow-screen bottom navigation, compact headers and card layout.
These patterns are implemented using PocketDesk's WPF markup. No FlClash source
files, binaries, screenshots, icons or fonts are included in PocketDesk's source
or application package. FlClash's own code is licensed separately under
[GPL-3.0](https://github.com/chen08209/FlClash/blob/main/LICENSE); this
acknowledgement does not relicense it under PocketDesk's MIT license.

## Device specifications and application icons

The small device catalog also cites Apple's public specifications:
[iPhone 17 Pro](https://support.apple.com/en-us/125090) and
[iPad Pro 11-inch, 3rd generation](https://support.apple.com/en-us/111897).
Device and product names remain the names of their respective owners.

The PocketDesk application icon is generated from the vector geometry in
`tools/icon-generator/Program.cs`; its SVG, PNG and ICO outputs are in
`src/PocketDesk/Assets`. Icons shown for other running applications are obtained
at runtime from locally installed applications and are not bundled in this
repository.

## Microsoft .NET and Windows

PocketDesk uses .NET/WPF and Windows platform APIs. Self-contained Windows
release packages include Microsoft runtime components, whose licenses are
separate from PocketDesk's root MIT license. The package preserves the supplied
runtime license and notices at:

- `licenses/dotnet/LICENSE.txt`
- `licenses/dotnet/ThirdPartyNotices.txt`
- `licenses/windowsdesktop/LICENSE.txt`

The release packager takes these files from the matching Microsoft runtime
packages. Framework-dependent source builds use the .NET runtime installed by
the user. Windows system libraries and system fonts are referenced by the
platform and are not copied into the source tree.

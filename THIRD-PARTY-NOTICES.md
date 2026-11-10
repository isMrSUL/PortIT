# Third-party notices

PortIT itself is © 2026 SUL (MrSUL) and released under the [MIT License](LICENSE).
It uses the following third-party components. Each keeps its own license.

## Code from the SvR 2011 PC port

These files in `app/Engine/svr/` started from the tools of the
[WWE SmackDown vs. Raw 2011 PC port](https://github.com/KaikoClanworth1/wwe-svr2011-pc) and were changed for PortIT:
`ch_tool.py`, `gear_port.py`, `jboy.py`, `model_budget.py`, `prop_bones.py`, `prop_bones_stock.json`,
`svr11_build.py`, `svr11_skeleton.json`, `svrfmt.py`, `w13_char.py`. Their license:

```
WWE SmackDown vs. Raw 2011 PC port
Copyright (c) 2026 KaikoClanworth1 and the SvR 2011 PC port contributors

MIT License

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

`svr11_skeleton.json` and `prop_bones_stock.json` hold reference numbers (bone positions and rest rotations of the
stock SvR 2011 skeleton) that the model builder needs; they contain no models, textures or other game assets.

## Libraries used by the application

| Component | License | Where |
|---|---|---|
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Avalonia, Avalonia.Desktop, Avalonia.Headless, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter) | MIT | NuGet packages |
| [Inter font](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | NuGet package |

## Libraries beside the 2K26 archive reader (`app/Engine/cake`)

`BakeIT.Core.dll` is SUL's own archive reader (same license as PortIT). It uses:

| Component | License |
|---|---|
| [CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) | MIT |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://github.com/dotnet/runtime) | MIT |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | MIT |
| [Microsoft.IO.RecyclableMemoryStream](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) | MIT |
| [Pfim](https://github.com/nickbabcock/Pfim) | MIT |
| [Syroot.BinaryData](https://gitlab.com/Syroot/BinaryData) (Core, Memory) | MIT |
| [System.IO.Hashing](https://github.com/dotnet/runtime) | MIT |

## Native helpers

`app/Engine/svr/bpe.exe` is built from `bpe.zig` in this repository with [Zig](https://ziglang.org/) 0.13 (MIT).
`app/Engine/k26/xxh3.py` follows Zig's standard library XXH3 (`lib/std/hash/xxhash.zig`, MIT) and uses its default
secret; `ddshash.zig` (the helper it replaced in 1.0.1) is kept for reference.

## The big-model DLL (`bigmodel_dll`, the optional `xinput1_4.dll`)

SUL's own code (same license as PortIT). Since PortIT 1.1.0 it also ships inside PortIT (`app/Engine/bigmodel`, with
MinHook's license beside it) so the app can install it. It is built with
[MinHook](https://github.com/TsudaKageyu/minhook) 1.3.4 (`bigmodel_dll/minhook`), BSD 2-Clause License,
Copyright (C) 2009-2017 Tsuda Kageyu; its full license is in `bigmodel_dll/minhook/LICENSE.txt`.

## Bundled with the release download (not in this repository)

| Component | License |
|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | MIT |
| [Python 3.12](https://www.python.org/) (Windows embeddable package) | Python Software Foundation License |
| [NumPy](https://numpy.org/) | BSD 3-Clause |
| [Pillow](https://python-pillow.org/) | MIT-CMU (HPND) |

To run PortIT from source you need Python 3.12 with NumPy and Pillow yourself.

## Not included

- **No game files** are included. PortIT reads your own copies of the games.
- **Oodle** (`oo2core_*_win64.dll`, RAD Game Tools / Epic Games) is proprietary and is not part of PortIT. When a
  WWE 2K19 / 2K20 pack is Oodle-packed, PortIT loads the copy that comes with the user's own WWE 2K game install.

## Trademarks

WWE, SmackDown vs. Raw, WWE 2K and AEW Fight Forever are trademarks of their owners. PortIT is a free fan-made
modding tool and is not affiliated with or endorsed by WWE, Yuke's, THQ, 2K, Visual Concepts, AEW or THQ Nordic.

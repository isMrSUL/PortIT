# Third-party notices

PortIT itself is © 2026 SUL (MrSUL) and released under the [MIT License](LICENSE).
It uses the following third-party components. Each keeps its own license.

## Code from the SvR 2011 PC port

These files in `app/Engine/svr/` started from the tools of the
[WWE SmackDown vs. Raw 2011 PC port](https://github.com/KaikoClanworth1/wwe-svr2011-pc) and were changed for PortIT:
`ch_tool.py`, `gear_port.py`, `jboy.py`, `model_budget.py`, `prop_bones.py`, `prop_bones_stock.json`,
`svr11_build.py`, `svr11_skeleton.json`, `svrfmt.py`, `w13_char.py`. Since PortIT 1.2.0 their C# versions in
`app/EngineNet/Build` and `app/EngineNet/Formats` (`SvrBuilder.cs`, `PropBones.cs`, `Strip.cs`, `Jboy.cs`,
`Containers.cs`, `Bpe.cs`) carry the same origin. Their license:

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

PortIT's moving hair (`app/EngineNet/Build/HairChains.cs`) and the 3D view's hair test
(`app/EngineNet/Port/HairSim.cs`) follow the same project's research notes on how the game moves hair (the chains,
the hair settings and the hair solver: `port/docs/HAIR_PHYSICS.md`); the C# code is PortIT's own, under the license
above for what it carries from those notes.

`svr11_skeleton.json` and `prop_bones_stock.json` hold reference numbers (bone positions and rest rotations of the
stock SvR 2011 skeleton) that the model builder needs; they contain no models, textures or other game assets.

## Libraries used by the application

| Component | License | Where |
|---|---|---|
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Avalonia, Avalonia.Desktop, Avalonia.Headless, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter) | MIT | NuGet packages |
| [Inter font](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | NuGet package |
| [SkiaSharp](https://github.com/mono/SkiaSharp) (and the [Skia](https://skia.org/) library it wraps) | MIT (Skia: BSD 3-Clause) | NuGet package (the engine's image files) |
| [Pfim](https://github.com/nickbabcock/Pfim) | MIT | NuGet package (the engine: BC7 textures) |
| [System.IO.Hashing](https://github.com/dotnet/runtime) | MIT | NuGet package (the engine: XXH3 for the HD texture pack names) |

The engine (`app/EngineNet`) was written to give the same results as PortIT's earlier Python engine, which used
[NumPy](https://numpy.org/) (BSD 3-Clause) and [Pillow](https://python-pillow.org/) (MIT-CMU): some of its numeric and
image routines (resampling, texture decoding, NumPy's sums, random numbers and sort, Pillow's polygon fill) follow their
behaviour. No code of theirs is included.

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

## Kraken decoder (lztans)

`app/EngineNet/Readers/Kraken.cs` is a C# translation of the Kraken decoder of
[lztans](https://github.com/patodois/lztans) 0.1.1 (dual-licensed MIT OR Apache-2.0; used here under MIT). It unpacks
the Kraken-compressed blocks of AEW Fight Forever files and WWE 2K19 - 2K22 packs, so no Oodle DLL is needed for them.

```
MIT License

Copyright (c) 2026 patodois

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

## Bundled with the release download (not in this repository)

| Component | License |
|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | MIT |

(PortIT 1.0.0 - 1.1.2 also bundled Python 3.12, NumPy and Pillow for their Python engine. Since 1.2.0 the release has no
Python. The old Python engine in `app/Engine` is kept in the repository for comparing; it needs Python 3.12 with
NumPy and Pillow.)

## Not included

- **No game files** are included. PortIT reads your own copies of the games.
- **Oodle** (`oo2core_*_win64.dll`, RAD Game Tools / Epic Games) is proprietary and is not part of PortIT. Since 1.2.1
  PortIT unpacks Kraken itself (see above). The DLL is still used, from the user's own install, for reading the
  WWE 2K26 game archives, and as a fallback for any pack that uses another Oodle codec.

## Trademarks

WWE, SmackDown vs. Raw, WWE 2K and AEW Fight Forever are trademarks of their owners. PortIT is a free fan-made
modding tool and is not affiliated with or endorsed by WWE, Yuke's, THQ, 2K, Visual Concepts, AEW or THQ Nordic.

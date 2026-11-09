<p align="center">
  <img src="app/Assets/portit_256.png" width="128" alt="PortIT logo">
</p>

<h1 align="center">PortIT</h1>

<p align="center">
  <b>Port wrestlers from WWE 2K26 and AEW Fight Forever into WWE SmackDown vs. Raw 2011 (PC port) —<br>
  full detail, HD textures, working face animation.</b>
</p>

<p align="center">
  <a href="../../releases/latest"><b>Download</b></a> ·
  <a href="docs/USER_GUIDE.md"><b>User guide</b></a> ·
  <a href="bigmodel_dll">Big-model DLL source</a>
</p>

![PortIT main window](docs/images/main_window.png)

## Quick start

1. Download **`PortIT-<version>-win-x64.zip`** from [Releases](../../releases/latest), unzip it anywhere and run
   **`PortIT.exe`** (no install: .NET and Python are inside).
2. For full-detail wrestlers, also download **`PortIT-BigModel-DLL-<version>.zip`** and put its `xinput1_4.dll` next to
   `svr2011.exe` ([how and why](docs/USER_GUIDE.md#the-big-model-dll-for-full-detail)).
3. Choose your SvR 2011 game folder, your WWE 2K26 game folder (or AEW's `paks` folder), click **Load**, pick a
   wrestler, choose **Full detail + HD texture pack** and click **Port to SvR 2011**.
4. **Install to game…** (a new superstar) or **Install as attire…** (a new look for a superstar the game has).

Everything is explained step by step in the **[user guide](docs/USER_GUIDE.md)**.

## What PortIT does

- **Reads the wrestlers straight from your own games**
  - **WWE 2K26**: point it at the WWE 2K26 game folder (all wrestlers are read from its archives) or at a folder of
    2K wrestler mods (`BaseModel\baseModel.mcd` + an attire folder). 2K25-era `.mcd` files work too.
  - **AEW Fight Forever**: the PS4 dump's `...\CUSA34641\elite\content\paks` folder.
- **Preview before porting** — see the wrestler as he or she is, untick clothes, gear or hair to leave them out, and
  draw the preview again with that edit.
- **Full detail + HD texture pack** (game 2.1.6 or newer, with PortIT's big-model DLL) — every vertex of the source
  model, and every texture at its full size in a texture pack. Six such wrestlers have run a 6-man match at 60 fps.
  Without the DLL, or on older games, the model is simplified to fit the game's normal memory.
- **Faces that move** — a stock SvR head's face rig is fitted onto the new face, so the game's own face animations
  (talking, yelling, selling) work. Which skin is upper lip and which is lower comes from the source game's own data
  (2K's FACS jaw-open shape, AEW's lip and jaw bones), the mouth corners stay round, and AEW wrestlers keep their own
  teeth, gums and tongue.
- **No glazed look** — SvR's skin shine is worked out from the source materials instead of copied.
- **Clothes and hair that follow the body** — tight gear moves with the skin under it, the skin under it is hidden,
  hair and beard cards keep their real colour and follow the face.
- **Checks in every report** — face points that would swing too far, gear that would drift from the skin, and the
  game's hard limits (a model that would crash is never handed out).
- **Install** — as a new superstar (`Mods\Superstars`), or as an **attire** of a superstar the game has (a new attire,
  or replacing one; the same rules as the Mod Maker's Attires page). The HD textures go into your enabled texture pack.
- **`.svrmod`** — every port is also saved as a `.svrmod`, so you can open it in the **Mod Maker** and edit the name,
  stats, moves, entrance and so on. The HD textures come in a zip for the `Texture Packs` folder.

## What you need

- **Windows 10 / 11** (64-bit) and the **SvR 2011 PC port** (2.1.6 or newer for full detail; its `pac\ch` stock
  models are the templates).
- Your own copy of **WWE 2K26** and/or the files of your own **AEW Fight Forever** (PS4).
- The release download has everything else inside.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/) and **Python 3.12** with **NumPy** and **Pillow**
(`pip install numpy pillow`). PortIT uses `app\Engine\python\python.exe` when it is there, otherwise `python` from your
PATH.

```
cd app
dotnet build -c Release
bin\Release\net10.0\PortIT.exe
```

The release zip is made with `tools\make_release.ps1` (see the script for its options). The big-model DLL is built
with `bigmodel_dll/build.sh` ([Zig](https://ziglang.org/) 0.13).

## Project layout

| Folder | What it is |
|---|---|
| `app/` | the Avalonia app (C#) |
| `app/Engine/worker.py` | the engine's entry point (the app talks to it through JSON files) |
| `app/Engine/k26/` | the full-detail pipeline: face fit, weights, texture sheets and packs, checks; `aew_bridge.py` feeds AEW into it |
| `app/Engine/portit/` | source readers (2K, AEW), preview, attires, the classic converter |
| `app/Engine/readers/` | file readers: 2K `.mcd`, UE4 IoStore / zen packages, PS4 textures |
| `app/Engine/svr/` | the SvR 2011 model builder and file formats; `bpe.zig` / `ddshash.zig` are the sources of the two small native helpers |
| `app/Engine/cake/` | the 2K26 archive reader (BakeIT.Core) and the libraries it uses |
| `bigmodel_dll/` | the source of the optional big-model DLL (`xinput1_4.dll`) for full-detail wrestlers |
| `docs/` | the [user guide](docs/USER_GUIDE.md) and its pictures |
| `tools/` | `make_release.ps1`: builds the release zips |

## Credits

Made by **SUL (MrSUL)**, with [Claude Code](https://claude.com/claude-code) as the coding assistant.

The SvR 2011 model builder and format code in `app/Engine/svr` started from the tools of the
[WWE SmackDown vs. Raw 2011 PC port](https://github.com/KaikoClanworth1/wwe-svr2011-pc) (MIT, KaikoClanworth1 and
the SvR 2011 PC port contributors). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License and credit

PortIT is free and open source under the [MIT License](LICENSE). You may use it, change it, make your own version of
it and share it - the only condition is to **keep the copyright notice (Copyright (c) 2026 MrSUL) and the license**
in your copy. If you make your own version or use PortIT's code in your project, please **credit SUL (MrSUL)** and
link back to this page. Thank you!

No game files are included; PortIT reads your own copies of the games.

WWE, SmackDown vs. Raw, WWE 2K and AEW Fight Forever are trademarks of their owners. PortIT is a free fan-made
modding tool and is not affiliated with or endorsed by WWE, Yuke's, THQ, 2K, Visual Concepts, AEW or THQ Nordic.

# Builds PortIT's release zips:
#   release\PortIT-<version>-win-x64.zip          a self-contained win-x64 build (no .NET install needed) + a portable
#                                                  Python 3.12 with NumPy and Pillow for the engine
#   release\PortIT-BigModel-DLL-<version>.zip     the optional big-model DLL (bigmodel_dll\out\xinput1_4.dll)
# and a .sha256 file for each.
#   .\tools\make_release.ps1 -Python <portable Python 3.12 folder with numpy + Pillow in Lib\site-packages>
#                            [-Dll <a built xinput1_4.dll; default: bigmodel_dll\out\xinput1_4.dll>]
param([Parameter(Mandatory = $true)][string]$Python, [string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$proj = Get-Content (Join-Path $root 'app\PortIT.csproj')
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "PortIT-$version-win-x64"
$rel = Join-Path $root 'release'
$dist = Join-Path $rel $name
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $rel | Out-Null

if (-not (Test-Path (Join-Path $Python 'python.exe'))) { throw "No python.exe in $Python" }
& (Join-Path $Python 'python.exe') -c "import numpy, PIL; print('python ok:', numpy.__version__, PIL.__version__)"
if ($LASTEXITCODE) { throw 'The portable Python cannot import numpy and Pillow.' }

dotnet publish (Join-Path $root 'app\PortIT.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $dist
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# the engine's Python (PortIT looks for Engine\python\python.exe first), with no caches
Copy-Item $Python (Join-Path $dist 'Engine\python') -Recurse
Get-ChildItem $dist -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force

# never ship proprietary or game files
$bad = Get-ChildItem $dist -Recurse -File | Where-Object {
    $_.Name -like 'oo2core*' -or $_.Extension -in '.pac', '.cak', '.mcd', '.tex', '.utoc', '.ucas', '.uasset', '.uexp', '.pak', '.svrmod' }
if ($bad) { throw "Refusing to package: $($bad.FullName -join ', ')" }

Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md'), (Join-Path $root 'README.md') $dist
New-Item -ItemType Directory -Force (Join-Path $dist 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\*.md') (Join-Path $dist 'docs')
Copy-Item (Join-Path $root 'docs\images') (Join-Path $dist 'docs\images') -Recurse
@"
PortIT $version - Port WWE 2K26 and AEW Fight Forever wrestlers into WWE SmackDown vs. Raw 2011 (PC port)
by SUL (MrSUL) - https://github.com/isMrSUL/PortIT

1. Start PortIT.exe (nothing to install). If Windows says "Windows protected your PC": More info -> Run anyway.
2. Read docs\USER_GUIDE.md - it explains every step and every choice.
3. For full-detail wrestlers you also need the big-model DLL (PortIT-BigModel-DLL-$version.zip on the same
   download page): see "The big-model DLL" in the user guide.

Keep all the files together: PortIT.exe needs the Engine folder next to it.
PortIT is free and open source (MIT License, see LICENSE). If you share a changed version, keep the LICENSE file
and please credit SUL (MrSUL).
"@ | Set-Content (Join-Path $dist 'Read Me First.txt') -Encoding UTF8

function Zip($folder, $zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # (Python's zipfile: '/' in the entry names - Windows PowerShell's Compress-Archive and .NET Framework's ZipFile
    #  write backslashes, which some unzip tools refuse)
    $py = "import os, sys, zipfile`nsrc, dst = sys.argv[1], sys.argv[2]`nbase = os.path.dirname(src)`n" +
          "with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:`n" +
          "    for r, ds, fs in os.walk(src):`n        ds.sort()`n        for f in sorted(fs):`n" +
          "            p = os.path.join(r, f)`n            z.write(p, os.path.relpath(p, base).replace(os.sep, '/'))"
    & (Join-Path $Python 'python.exe') -c $py $folder $zip
    if ($LASTEXITCODE) { throw "zip failed: $zip" }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256"
    "release: $zip"
    "sha256:  $hash"
}
Zip $dist (Join-Path $rel "$name.zip")

# the big-model DLL
if (-not $Dll) { $Dll = Join-Path $root 'bigmodel_dll\out\xinput1_4.dll' }
if (-not (Test-Path $Dll)) { throw "No big-model DLL at $Dll - build it with bigmodel_dll\build.sh first" }
$dname = "PortIT-BigModel-DLL-$version"
$ddist = Join-Path $rel $dname
if (Test-Path $ddist) { Remove-Item $ddist -Recurse -Force }
New-Item -ItemType Directory -Force $ddist | Out-Null
Copy-Item $Dll (Join-Path $ddist 'xinput1_4.dll')
Copy-Item (Join-Path $root 'LICENSE') $ddist
Copy-Item (Join-Path $root 'bigmodel_dll\minhook\LICENSE.txt') (Join-Path $ddist 'MinHook-LICENSE.txt')
@"
PortIT big-model DLL $version - for full-detail PortIT wrestlers in WWE SmackDown vs. Raw 2011 (PC port)
by SUL (MrSUL) - https://github.com/isMrSUL/PortIT

What it does: in a match the game gives each wrestler 768 KB for its model's shape (about 168 PortIT pages).
This DLL raises it to 4 MB (about 1000 pages), so full-detail ports fit. It does nothing outside the game
(svr2011.exe), and it changes nothing if your game's memory table is not the one it expects.
Tested with SvR 2011 PC port 2.1.9.

Install:
 1. Close the game.
 2. In the game folder (next to svr2011.exe): if there is already an xinput1_4.dll, it belongs to another mod -
    rename it (for example to xinput1_4.dll.old). Only one xinput1_4.dll can be used at a time.
 3. Copy xinput1_4.dll from this zip next to svr2011.exe.
 4. Start the game once: bigmodel.log appears next to the DLL.
      "model memory hook on" and "vertex windows ... -> 4096 KB"  = it works
      "big models off (the game is unchanged)"                    = your game version is not supported
 5. In PortIT choose "Full detail + HD texture pack" and port.

Remove: close the game and delete xinput1_4.dll (or rename your old one back).

Source: bigmodel_dll in the PortIT repository. MIT License (LICENSE); built with MinHook (MinHook-LICENSE.txt).
"@ | Set-Content (Join-Path $ddist 'How to install.txt') -Encoding UTF8
Zip $ddist (Join-Path $rel "$dname.zip")

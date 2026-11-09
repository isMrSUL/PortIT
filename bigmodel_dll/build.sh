#!/bin/sh
# PortIT's big-model DLL: the big-model memory alone (bigmodel.c), built as xinput1_4.dll for the SvR 2011 PC port.
#   ZIG=<path to zig.exe 0.13> sh build.sh      -> out/xinput1_4.dll
set -e
cd "$(dirname "$0")"
ZIG="${ZIG:-zig}"
mkdir -p out
"$ZIG" cc -target x86_64-windows-gnu -shared -O2 -DCAMERA_ONLY -DBLOOD_ONLY -o out/xinput1_4.dll xinput_remap.c bigmodel.c xinput_remap.def \
  minhook/src/hook.c minhook/src/buffer.c minhook/src/trampoline.c minhook/src/hde/hde64.c -luser32
rm -f out/*.pdb out/*.lib
ls -la out

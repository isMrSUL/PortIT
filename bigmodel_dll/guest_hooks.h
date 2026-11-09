/* External guest hooks for the ReXGlue PPCFunc ABI.
 * Include after blood.c's GuestFn typedef. Call from the startup worker only.
 * The executable's own guest/host mappings authenticate addresses; host code
 * bytes, RVAs and compiler-generated instruction lengths are never pinned.
 * MinHook relocates instructions and coordinates patching with other threads.
 */
#ifndef SVR_BLOOD_GUEST_HOOKS_H
#define SVR_BLOOD_GUEST_HOOKS_H

#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "minhook/include/MinHook.h"

typedef struct { uintptr_t guest, host; } BloodGuestMapping;
typedef struct { uintptr_t begin, end; } BloodGuestRange;
typedef GuestFn (*BloodGuestResolver)(unsigned int);

static BloodGuestResolver g_gh_resolve;
static BloodGuestMapping* g_gh_maps;
static size_t g_gh_count;
static BloodGuestRange g_gh_code[96];
static unsigned int g_gh_code_count;
static int g_gh_initialized;
static const char* g_gh_error = "guest hooks not initialized";

static const char* GuestHooksError(void) { return g_gh_error; }

static int BloodGuestReadable(const void* pointer, size_t length) {
  uintptr_t cursor = (uintptr_t)pointer;
  const uintptr_t end = cursor + length;
  if (!pointer || end < cursor) return 0;
  while (cursor < end) {
    MEMORY_BASIC_INFORMATION info;
    if (!VirtualQuery((const void*)cursor, &info, sizeof info) ||
        info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return 0;
    const DWORD protection = info.Protect & 0xFFu;
    if (protection != PAGE_READONLY && protection != PAGE_READWRITE && protection != PAGE_WRITECOPY &&
        protection != PAGE_EXECUTE_READ && protection != PAGE_EXECUTE_READWRITE &&
        protection != PAGE_EXECUTE_WRITECOPY) return 0;
    const uintptr_t next = (uintptr_t)info.BaseAddress + info.RegionSize;
    if (next <= cursor) return 0;
    cursor = next;
  }
  return 1;
}

static int BloodGuestInCode(uintptr_t pointer) {
  for (unsigned int i = 0; i < g_gh_code_count; ++i)
    if (pointer >= g_gh_code[i].begin && pointer < g_gh_code[i].end) return 1;
  return 0;
}

static int BloodGuestExecutable(uintptr_t pointer) {
  MEMORY_BASIC_INFORMATION info;
  if (!BloodGuestInCode(pointer) || !VirtualQuery((const void*)pointer, &info, sizeof info) ||
      info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return 0;
  const DWORD protection = info.Protect & 0xFFu;
  return protection == PAGE_EXECUTE || protection == PAGE_EXECUTE_READ ||
         protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY;
}

static int BloodGuestMappingValid(const BloodGuestMapping* mapping) {
  return mapping->guest >= 0x82000000u && mapping->guest < 0x84000000u &&
         !(mapping->guest & 3u) && BloodGuestInCode(mapping->host);
}

static int BloodGuestMappingCompare(const void* left, const void* right) {
  const uintptr_t a = ((const BloodGuestMapping*)left)->guest;
  const uintptr_t b = ((const BloodGuestMapping*)right)->guest;
  return a < b ? -1 : a > b ? 1 : 0;
}

static int BloodGuestIndex(void) {
  const unsigned char* image = (const unsigned char*)GetModuleHandleW(NULL);
  if (!BloodGuestReadable(image, sizeof(IMAGE_DOS_HEADER))) return 0;
  const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*)image;
  if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < (LONG)sizeof *dos || dos->e_lfanew > 65536) return 0;
  const IMAGE_NT_HEADERS64* nt = (const IMAGE_NT_HEADERS64*)(image + dos->e_lfanew);
  if (!BloodGuestReadable(nt, sizeof *nt) || nt->Signature != IMAGE_NT_SIGNATURE ||
      nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
      nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
      nt->FileHeader.SizeOfOptionalHeader < sizeof(IMAGE_OPTIONAL_HEADER64) ||
      !nt->FileHeader.NumberOfSections || nt->FileHeader.NumberOfSections > 96) return 0;
  const IMAGE_SECTION_HEADER* sections = IMAGE_FIRST_SECTION(nt);
  if (!BloodGuestReadable(sections, nt->FileHeader.NumberOfSections * sizeof *sections)) return 0;
  const size_t image_size = nt->OptionalHeader.SizeOfImage;
  g_gh_code_count = 0;
  for (unsigned int i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
    const IMAGE_SECTION_HEADER* section = sections + i;
    const size_t offset = section->VirtualAddress, length = section->Misc.VirtualSize;
    if (!length || offset >= image_size || length > image_size - offset) continue;
    if (section->Characteristics & IMAGE_SCN_MEM_EXECUTE) {
      g_gh_code[g_gh_code_count].begin = (uintptr_t)image + offset;
      g_gh_code[g_gh_code_count++].end = (uintptr_t)image + offset + length;
    }
  }
  if (!g_gh_code_count) return 0;

  /* PPCFuncMappings is a sentinel-terminated array of {size_t guest, PPCFunc*}
   * in initialized image data. Find its largest contiguous run once, then keep
   * our own sorted index so registration and later lookups need no PE scans.
   */
  const unsigned char* best = NULL;
  size_t best_count = 0;
  for (unsigned int i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
    const IMAGE_SECTION_HEADER* section = sections + i;
    const size_t offset = section->VirtualAddress;
    size_t length = section->SizeOfRawData;
    if (!(section->Characteristics & IMAGE_SCN_CNT_INITIALIZED_DATA) ||
        !(section->Characteristics & IMAGE_SCN_MEM_READ) ||
        (section->Characteristics & (IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_DISCARDABLE)) ||
        offset >= image_size) continue;
    if (length > section->Misc.VirtualSize) length = section->Misc.VirtualSize;
    if (length > image_size - offset) continue;
    const unsigned char* data = image + offset;
    if (!BloodGuestReadable(data, length)) continue;
    for (size_t at = 0; at + sizeof(BloodGuestMapping) <= length;) {
      BloodGuestMapping entry;
      memcpy(&entry, data + at, sizeof entry);
      if (!BloodGuestMappingValid(&entry)) { at += sizeof(uintptr_t); continue; }
      size_t count = 1;
      while (at + (count + 1) * sizeof entry <= length) {
        memcpy(&entry, data + at + count * sizeof entry, sizeof entry);
        if (!BloodGuestMappingValid(&entry)) break;
        ++count;
      }
      if (count > best_count) best = data + at, best_count = count;
      at += count * sizeof entry;
    }
  }
  if (best_count < 64 || best_count > 1000000) return 0;
  BloodGuestMapping* maps = (BloodGuestMapping*)malloc(best_count * sizeof *maps);
  if (!maps) return 0;
  memcpy(maps, best, best_count * sizeof *maps);
  qsort(maps, best_count, sizeof *maps, BloodGuestMappingCompare);
  for (size_t i = 1; i < best_count; ++i)
    if (maps[i - 1].guest == maps[i].guest && maps[i - 1].host != maps[i].host) {
      free(maps);
      return 0;
    }
  g_gh_maps = maps;
  g_gh_count = best_count;
  return 1;
}

/* Nonblocking: the caller may retry while rexruntime is loading. No per-call
 * optional-function wait, and no code is called before registration matches.
 */
static int GuestHooksInitialize(void) {
  if (g_gh_initialized) return 1;
  HMODULE runtime = GetModuleHandleW(L"rexruntime.dll");
  if (!runtime) { g_gh_error = "rexruntime.dll not loaded yet"; return 0; }
  g_gh_resolve = (BloodGuestResolver)GetProcAddress(runtime,
      "?ResolveIndirectFunction@runtime@rex@@YAP6AXAEAUPPCContext@@PEAE@ZI@Z");
  if (!g_gh_resolve) { g_gh_error = "runtime guest resolver ABI unavailable"; return 0; }
  if (!g_gh_maps && !BloodGuestIndex()) { g_gh_error = "executable guest mappings unavailable"; return 0; }
  const MH_STATUS status = MH_Initialize();
  if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) {
    g_gh_error = MH_StatusToString(status);
    return 0;
  }
  g_gh_initialized = 1;
  g_gh_error = "OK";
  return 1;
}

static GuestFn GuestHooksResolve(unsigned int guest) {
  if (!g_gh_initialized) { g_gh_error = "guest hooks not initialized"; return NULL; }
  size_t lo = 0, hi = g_gh_count;
  while (lo < hi) {
    const size_t mid = lo + (hi - lo) / 2;
    if (g_gh_maps[mid].guest < guest) lo = mid + 1; else hi = mid;
  }
  if (lo == g_gh_count || g_gh_maps[lo].guest != guest) {
    g_gh_error = "guest absent from executable mappings";
    return NULL;
  }
  const uintptr_t target = g_gh_maps[lo].host;
  GuestFn resolved = g_gh_resolve(guest);
  if ((uintptr_t)resolved != target || !BloodGuestExecutable(target)) {
    g_gh_error = "guest not registered yet or registration differs from executable";
    return NULL;
  }
  g_gh_error = "OK";
  return resolved;
}

static int GuestHooksInstall(unsigned int guest, GuestFn detour, GuestFn* original) {
  if (!detour || !original) { g_gh_error = "invalid guest hook arguments"; return 0; }
  GuestFn target = GuestHooksResolve(guest);
  if (!target) return 0;
  MH_STATUS status = MH_CreateHook((LPVOID)target, (LPVOID)detour, (LPVOID*)original);
  if (status != MH_OK) { g_gh_error = MH_StatusToString(status); return 0; }
  /* CreateHook publishes the trampoline before any game thread can detour. */
  status = MH_EnableHook((LPVOID)target);
  if (status != MH_OK) {
    MH_RemoveHook((LPVOID)target);
    *original = NULL;
    g_gh_error = MH_StatusToString(status);
    return 0;
  }
  g_gh_error = "OK";
  return 1;
}

#endif

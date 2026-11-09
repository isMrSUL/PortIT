/* Bigger wrestler models (2026-10-22, updated 2026-11-07 for port 2.1.6+): past the match's 768 KB per-wrestler
 * vertex memory and 1 MB model heap.
 *
 * In a match each wrestler's model buffers come from its own physical window of 768 KB (192 pages: 1B240000-1B300000,
 * 1B300000-1B3C0000, ...): every vertex / index buffer is one sub_8215A380(size, lo, hi, align, protect) call
 * (MmAllocatePhysicalMemoryEx in [lo, hi]) - logged with SVR2011_TEST_BUFFER_LOG=1. The model's JBOY data (body,
 * hair, lashes, blood overlays) is first copied into the wrestler's model heap (sub_826B6BA8 -> 8269BBA0, lr 826B6C4C).
 *
 * What limits a big model (checked 2026-11-07 on the test copy, port 2.1.6 build fbc38d83 + this DLL):
 *  - the port (since 2.1.6, src/model_mem.cpp + superstar_mods.cpp + caw_limits.h) already: gives a full vertex
 *    window (allocator 128) and a full model heap (lr 826B6C4C) its fallback memory (192 MB cap, shared by the match);
 *    builds Mods\SuperstarOverlay\chNNN.pac from the mod's own ch.pac up to ModelPageLimit() = 1600 pages a wrestler
 *    ("big models: 1600 pages a wrestler" in its log) - over that the mod is left out (stock model, no crash).
 *    So bigmodel_install.py's SuperstarOverlay trick is no longer needed: a big ch.pac goes in the mod folder.
 *  - one model part (body 0x0, hair 0x2710 ...) over 2048 pages crashes the draw (sub_826E6AD0): the tools keep a
 *    part under 1900. A mesh over 65,535 vertices can't be indexed (16-bit indices).
 *  - the old crash (1.97 MB body JBOY, "null 1 MB buffer", 2026-10-22 brockbig1) was the 1 MB model heap on the old
 *    port without the fallback: gone with the port's fallback (this DLL no longer raises the heaps: see HEAP_KB).
 * This DLL: makes the vertex windows bigger (HeapThread) so a big model mostly stays in the game's own memory, and
 * logs every model heap copy of a match (size, address, failed) and any window still full - bigmodel.log next to
 * the DLL. BIGMODEL_TRACE=1: research traces (big physical blocks with the guest call chain, the copy 826B3490).
 */
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>

typedef void (*GuestFn)(void* ctx, unsigned char* base);
#define REG(c, n) (((unsigned long long*)(c))[(n) == 3 ? 0 : (n) == 1 ? 2 : 4 + ((n) - 4)])

static GuestFn g_orig_phys;
static wchar_t g_log[MAX_PATH];
static volatile LONG g_moved, g_failed;
static volatile LONG g_heap_state; /* 0: checking the executable, 1: supported, -1: leave the heaps unchanged */
static CRITICAL_SECTION g_lock;


static void Log(const char* fmt, ...) {
  EnterCriticalSection(&g_lock);
  FILE* f = _wfopen(g_log, L"a");
  if (f) {
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(f, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list a;
    va_start(a, fmt);
    vfprintf(f, fmt, a);
    va_end(a);
    fputc('\n', f);
    fclose(f);
  }
  LeaveCriticalSection(&g_lock);
}

static int g_trace;
static unsigned int G32(unsigned char* b, unsigned int a) {   /* (a read that can't fault: the stack's end) */
  unsigned char p[4];
  SIZE_T got = 0;
  if (!ReadProcessMemory(GetCurrentProcess(), b + a, p, 4, &got) || got != 4) return 0;
  return (unsigned int)p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3];
}
static void PhysHook(void* c, unsigned char* b) {
  const unsigned long long r3 = REG(c, 3), r4 = REG(c, 4), r5 = REG(c, 5), r6 = REG(c, 6), r7 = REG(c, 7);
  if (g_trace && (unsigned int)r3 >= 0x80000) {    /* (research: who asks for big blocks - the guest call chain) */
    char chain[256];
    int n = 0;
    unsigned int sp = (unsigned int)REG(c, 1);
    for (int i = 0; i < 14 && n < 230; ++i) {
      const unsigned int back = G32(b, sp);
      if (back < 0x70000000u || back >= 0x80000000u) break;
      n += sprintf(chain + n, " %08X", G32(b, back - 8));
      sp = back;
    }
    chain[n] = 0;
    Log("TRACE physical %u bytes in %08X-%08X:%s", (unsigned int)r3, (unsigned int)r4, (unsigned int)r5, chain);
  }
  g_orig_phys(c, b);
  const unsigned int lo = (unsigned int)r4, hi = (unsigned int)r5;
  (void)r6, (void)r7;
  /* (a buffer can't go outside its window: placed elsewhere, the layer above returned null and the match crashed -
   *  brockbig10. The windows are made bigger instead (HeapThread); a model still too big is only logged here.) */
  if ((unsigned int)REG(c, 3) != 0 || (unsigned int)r3 == 0 || lo < 0x1B240000u || hi > 0x1C000000u) return;
  const LONG n = InterlockedIncrement(&g_failed);
  if (n <= 20) Log("%u byte model buffer: wrestler window %08X-%08X is full - the model is too big even now", (unsigned int)r3, lo, hi);
}

/* ---- BIGMODEL_TRACE=1 (research): the allocator 8269BBA0 (big or failed requests) and the copy 826B3490 that
 * crashed on a null buffer loading a big model (memcpy 8215A000 to 0x70, 2026-10-22) */
static GuestFn g_orig_alloc, g_orig_b3490;
#define LR(c) (((unsigned long long*)(c))[0x100 / 8])
static void AllocHook(void* c, unsigned char* b) {
  const unsigned int r3 = (unsigned int)REG(c, 3), r4 = (unsigned int)REG(c, 4), r5 = (unsigned int)REG(c, 5);
  const unsigned int lr = (unsigned int)LR(c);
  g_orig_alloc(c, b);
  const unsigned int got = (unsigned int)REG(c, 3);
  if (lr == 0x826B6C4Cu && r3) {  /* a wrestler's model heap: one JBOY of its model copied in (sub_826B6BA8) */
    static volatile LONG n;
    const LONG k = InterlockedIncrement(&n);
    if (k <= 2000)
      Log("model heap copy: %u bytes (%.2f MB) -> %08X%s", r3, r3 / 1048576.0, got,
          got ? "" : "  = 0: NO ROOM (the heap and the port's fallback) - a crash follows");
  }
  if (!g_trace) return;
  static volatile LONG n;
  if ((got == 0 || r3 >= 0x40000) && InterlockedIncrement(&n) < 400)
    Log("TRACE alloc 8269BBA0(r3 %08X r4 %08X r5 %08X) lr %08X -> %08X", r3, r4, r5, lr, got);
}
/* the copy's other buffer source: a function pointer at guest 82ED7568 (else 8269BBA0) - logged when it changes */
static DWORD WINAPI TraceWatch(LPVOID unused) {
  (void)unused;
  const unsigned char* g = (const unsigned char*)0x100000000ull;
  unsigned int last = 0xFFFFFFFFu, last2 = 0xFFFFFFFFu;
  for (int i = 0; i < 3000; ++i, Sleep(20)) {
    const unsigned char* p = g + 0x82ED7568u;
    const unsigned int v = (unsigned int)p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3];
    const unsigned int v2 = (unsigned int)p[4] << 24 | p[5] << 16 | p[6] << 8 | p[7];
    if (v != last || v2 != last2) Log("TRACE 82ED7568 = %08X, 82ED756C = %08X", v, v2), last = v, last2 = v2;
  }
  return 0;
}
static void B3490Hook(void* c, unsigned char* b) {
  static volatile LONG n;
  if (InterlockedIncrement(&n) < 200)
    Log("TRACE 826B3490(r3 %08X r4 %08X r5 %08X r6 %08X) lr %08X", (unsigned int)REG(c, 3), (unsigned int)REG(c, 4),
        (unsigned int)REG(c, 5), (unsigned int)REG(c, 6), (unsigned int)LR(c));
  g_orig_b3490(c, b);
}

/* ---- heap 53 bigger (2026-11-07). The game reserves one physical block for its heaps (sub_8258ED50 ->
 * sub_821581A8(request, out): request +20 = 0x10C00000 (the block ends at 0x1FC00000 - that = 0x0F000000), +24 = the
 * size, 0x0B600000; the call steps down 64 KB while the reservation fails) and splits it top down by the heap table,
 * heap 53 = the rest at the bottom (25 MB). The select screen reads a superstar's ch.pac and unpacks its textures
 * there through sub_8269A8D8, which in port 2.1.9 has no fallback for heap 53: a 4096 x 4096 colour sheet (22,369,824
 * bytes with mips) failed there, a crash on picking him (lr 828CA638, then the unpack sub_826AEF10 wrote to 0).
 * The block's size grows by HEAP53_EXTRA_MB here - all of it goes to heap 53 (the other heaps keep their sizes, heaps
 * 0-52 their addresses). The port grows it for heap 12 the same way (ring_rules.cpp: only when the size is still the
 * game's - its extra, read from heap 12's KB in the table, is added here too, so both apply whichever hook runs
 * first). Off: BIGMODEL_HEAP53_MB=0. */
#define HEAP53_EXTRA_MB 24u
static GuestFn g_orig_block;
static void BlockHook(void* c, unsigned char* b) {
  const unsigned int req = (unsigned int)REG(c, 3), out = (unsigned int)REG(c, 4);
  unsigned int extra = HEAP53_EXTRA_MB << 20;
  wchar_t v[16];
  if (GetEnvironmentVariableW(L"BIGMODEL_HEAP53_MB", v, 16)) extra = (unsigned int)_wtoi(v) << 20;
  const unsigned int game = 0x0B600000u;
  unsigned int size = 0, grown = 0;
  if (extra && G32(b, req + 20) == 0x10C00000u && (size = G32(b, req + 24)) >= game && size < game + (256u << 20)) {
    unsigned int port = 0;   /* (the port's heap-12 extra, when its own hook has not added it yet) */
    if (size == game)   /* (heap 12's pair looked up: the table's index is not the heap id) */
      for (unsigned int e = 0; e < 54; ++e) {
        const unsigned int id = G32(b, 0x82DAC2F8u + e * 8), kb12 = G32(b, 0x82DAC2F8u + e * 8 + 4);
        if (id != 12) continue;
        if (kb12 > 4608 && kb12 < 4608 + (256u << 10)) port = (kb12 - 4608) << 10;
        break;
      }
    grown = size + port + extra;
    unsigned char* p = b + req + 24;
    p[0] = (unsigned char)(grown >> 24), p[1] = (unsigned char)(grown >> 16), p[2] = (unsigned char)(grown >> 8), p[3] = (unsigned char)grown;
  }
  g_orig_block(c, b);
  if (grown)
    Log("heap block: asked %.1f MB (the game's %.1f + %.1f for heap 53 + the port's %.1f) -> %08X, %.1f MB%s",
        grown / 1048576.0, game / 1048576.0, extra / 1048576.0, (grown - extra - game) / 1048576.0,
        G32(b, out), G32(b, out + 4) / 1048576.0, G32(b, out + 4) == grown ? "" : " (stepped down: heap 53 that much smaller)");
}

/* Hooks go through guest_hooks.h (like blood.c): the guest address is checked against the executable's own
 * guest -> host table and MinHook moves whole instructions, so a new port build needs no update here (the guest
 * code and the heap table at 0x82DAC2F8 come from the game disc and don't change between port builds). */
#include "guest_hooks.h"

static DWORD WINAPI HookThread(LPVOID unused) {
  (void)unused;
  int ready = 0;
  for (int tries = 0; tries < 600 && !(ready = GuestHooksInitialize()); ++tries) Sleep(100);
  GuestFn fn = NULL;
  for (int tries = 0; ready && tries < 600 && !(fn = GuestHooksResolve(0x8215A380u)); ++tries) Sleep(100);
  /* (first: the heap block is reserved early in start-up) */
  if (fn) Log("heap 53 bigger (heap block 821581A8): %s", GuestHooksInstall(0x821581A8u, BlockHook, &g_orig_block) ? "hook on" : "NOT hooked");
  if (!fn || !GuestHooksInstall(0x8215A380u, PhysHook, &g_orig_phys)) {
    InterlockedExchange(&g_heap_state, -1);
    Log("big models off (the game is unchanged): %s", GuestHooksError());
    return 0;
  }
  Log("model memory hook on (physical allocator 8215A380): wrestler models may use more than 768 KB");
  Log("model heap copies logged (allocator 8269BBA0 from 826B6C4C): %s",
      GuestHooksInstall(0x8269BBA0u, AllocHook, &g_orig_alloc) ? "on" : "NOT hooked");
  if (GetEnvironmentVariableW(L"BIGMODEL_TRACE", NULL, 0)) {
    g_trace = 1;
    Log("trace: copy 826B3490 %s", GuestHooksInstall(0x826B3490u, B3490Hook, &g_orig_b3490) ? "hooked" : "NOT hooked");
    HANDLE w = CreateThread(NULL, 0, TraceWatch, NULL, 0, NULL);
    if (w) CloseHandle(w);
  }
  InterlockedExchange(&g_heap_state, 1);
  return 0;
}

/* ---- the wrestlers' model heaps: the game splits physical 0x03A00000-0x0F000000 into heaps from a table of
 * (heap id, KB) pairs at 0x82DAC2F8, read once at start-up (sub_8258ED50 -> sub_8269AD08; port arena_mods.cpp).
 * Heaps 17-22 (entries 17-22: 1024 KB each) are the six wrestlers' model heaps: sub_826B6BA8 copies each JBOY of the
 * wrestler's model there (body, hair, lashes, blood overlays) - a full-detail model's body alone was 898 KB, its hair
 * then got no room (null) and the match load crashed (2026-10-22, brockbig9). They are raised to HEAP_KB each from
 * the block's unused end (the table's 172,932 KB end at 0x0E2E1000 of 0x0F000000: 13 MB spare; +9 MB here).
 * Written as soon as the table is in memory, before the game reads it.
 * 2026-11-07: NO LONGER RAISED (HEAP_KB = the game's 1024). Heap 53 is the rest of that block (not in the table), so
 * the +9 MB came out of heap 53 - where the select screen reads a character's file and unpacks its textures: a
 * 4096 x 2048 colour sheet (11,185,024 bytes with mips) found no room there, crash on picking the superstar
 * (log: "heap 53: allocation of 11185024 bytes FAILED (lr 828CA638)"). The port gives a full model heap fallback
 * memory itself (2.1.6+: copy at lr 826B6C4C; 2.1.9: heaps 17-20 and heap 53 in its sub_8269A8D8 hook). */
#define HEAP_KB 1024u
/* The same table's second region (entries 51-98, physical 0x0F000000-0x1FC00000, ends at 0x1DD10000: 31 MB unused):
 * entries 79-84 = heaps 29-34, 768 KB each from 0x1B240000 = the six wrestlers' vertex / index buffer windows (the
 * 192-page budget). Raised to WINDOW_KB each.
 * 2026-11-07: 2048 -> 4096 KB (1024 pages; +19.5 MB of the 31 MB unused). A model over its window is NOT safe even
 * with the port's 2.1.9 "anywhere" placement: a 929-page Rey Mysterio port filled the 2048 KB window on the select
 * screen, the next 1.5 MB vertex buffer went to FD800000 and the draw (sub_826E6AD0, lr 826E6B90) crashed reading a
 * wild address - as brockbig10 did. So a wrestler's model must fit its window: PortIT reads WINDOW_KB from this
 * log ("vertex windows ... -> N KB") and keeps full-detail ports under N / 4 - 24 pages. */
#define WINDOW_KB 4096u
/* the heaps as the game made them (table at *0x82EC5F48, 36 bytes each: start, end) - once, when heap 53 is set */
static unsigned int GuestRd32(unsigned int a) {
  const unsigned char* p = (const unsigned char*)0x100000000ull + a;
  return (unsigned int)p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3];
}
static void LogHeaps(void) {
  for (int i = 0; i < 1200; ++i, Sleep(50)) {
    const unsigned int table = GuestRd32(0x82EC5F48u);
    if (!table || !GuestRd32(table + 53 * 36 + 4)) continue;
    static const int show[] = {12, 16, 17, 18, 19, 20, 21, 22, 53};
    for (int k = 0; k < (int)(sizeof show / sizeof show[0]); ++k) {
      const unsigned int a = GuestRd32(table + show[k] * 36), b = GuestRd32(table + show[k] * 36 + 4);
      Log("heap %d: %08X-%08X (%.1f MB)%s", show[k], a, b, (b - a) / 1048576.0,
          show[k] == 53 ? " - the rest: the select screen reads / unpacks a superstar's files here" : "");
    }
    return;
  }
  Log("heap list: the game's heaps not made within 60 s");
}

static DWORD WINAPI HeapThread(LPVOID unused) {
  (void)unused;
  for (int i = 0; i < 600 && InterlockedCompareExchange(&g_heap_state, 0, 0) == 0; ++i) Sleep(100);
  if (InterlockedCompareExchange(&g_heap_state, 0, 0) != 1) return 0;
  unsigned char* t = (unsigned char*)0x100000000ull + 0x82DAC2F8u;
  for (int i = 0; i < 20000; ++i) {          /* (up to 20 s, every 1 ms: the image is loaded early, read a bit later) */
    MEMORY_BASIC_INFORMATION mi;
    if (VirtualQuery(t, &mi, sizeof mi) && mi.State == MEM_COMMIT && !(mi.Protect & (PAGE_NOACCESS | PAGE_GUARD))) {
      unsigned int ok = 1;
      for (unsigned int e = 17; e <= 22 && ok; ++e) {
        const unsigned char* p = t + 8 * e;
        const unsigned int id = (unsigned int)p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3];
        const unsigned int kb = (unsigned int)p[4] << 24 | p[5] << 16 | p[6] << 8 | p[7];
        ok = id == e && kb == 1024;
      }
      for (unsigned int e = 79; e <= 84 && ok; ++e) {
        const unsigned char* p = t + 8 * e;
        const unsigned int id = (unsigned int)p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3];
        const unsigned int kb = (unsigned int)p[4] << 24 | p[5] << 16 | p[6] << 8 | p[7];
        ok = id == e - 50 && kb == 768;
      }
      if (ok) {
        DWORD old;
        if (!VirtualProtect(t, 0x400, PAGE_READWRITE, &old)) { Log("heap table: can't write it"); return 0; }
        for (unsigned int e = 79; e <= 84; ++e) {    /* (17-22 left at the game's: HEAP_KB above) */
          const unsigned int v = WINDOW_KB;
          unsigned char* p = t + 8 * e + 4;
          p[0] = (unsigned char)(v >> 24), p[1] = (unsigned char)(v >> 16), p[2] = (unsigned char)(v >> 8), p[3] = (unsigned char)v;
        }
        VirtualProtect(t, 0x400, old, &old);
        {   /* (the second region's end now: its pairs 51-98 summed from 0x0F000000 - must stay under 0x1FC00000) */
          unsigned long long end = 0x0F000000ull;
          for (unsigned int e = 51; e <= 98; ++e) {
            const unsigned char* q = t + 8 * e + 4;
            end += (unsigned long long)((unsigned int)q[0] << 24 | q[1] << 16 | q[2] << 8 | q[3]) << 10;
          }
          Log("second heap region: 0F000000-%08llX (%s 1FC00000, %.1f MB to spare)", end, end <= 0x1FC00000ull ? "within" : "PAST",
              ((double)0x1FC00000ull - (double)end) / 1048576.0);
        }
        Log("wrestler model heaps 17-22: %u KB (the game's; the port's fallback when full), vertex windows 29-34: 768 -> %u KB"
            " each (after %d ms)", HEAP_KB, WINDOW_KB, i);
        LogHeaps();
        return 0;
      }
    }
    Sleep(1);
  }
  Log("heap table not found as expected - model heaps left at 1 MB");
  return 0;
}

void BigModelStart(HMODULE self) {
  InitializeCriticalSection(&g_lock);
  GetModuleFileNameW(self, g_log, MAX_PATH);
  wchar_t* s = wcsrchr(g_log, L'\\');
  wcscpy(s ? s + 1 : g_log, L"bigmodel.log");
  DeleteFileW(g_log);
  HANDLE t = CreateThread(NULL, 0, HookThread, NULL, 0, NULL);
  if (t) CloseHandle(t);
  t = CreateThread(NULL, 0, HeapThread, NULL, 0, NULL);
  if (t) CloseHandle(t);
}

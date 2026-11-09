/* xinput1_4.dll proxy for the SvR 2011 PC port: controller button remap and
 * an L3 (TARGET) press hold.
 *
 * The port loads XInput by name (rexruntime.dll: LoadLibrary "xinput1_4.dll"),
 * so this DLL next to svr2011.exe is used instead of Windows' own; every call
 * goes on to System32\xinput1_4.dll. Only the buttons the game sees change -
 * the pad, Windows and other programs are untouched. Delete the DLL to undo.
 *
 * xinput_remap.ini (next to the DLL), "PHYSICAL=GAME" lines:
 *   LT=LB      the LT trigger does what LB did (RUN)
 *   LB=A       LB does what A did (ACTION)
 *   A=NONE     A does nothing
 *   L3_HOLD_MS=120
 *   MATCH_ONLY=1   remap only while a match is being played (menus keep A)
 * Buttons: A B X Y LB RB LT RT START BACK L3 R3 UP DOWN LEFT RIGHT, NONE.
 * Unlisted buttons keep their own job.
 *
 * L3: the port runs the 30 fps game's extra updates on the previous pad
 * reading (frame_rate.cpp), so when it shows 30 fps the pad is read about
 * every 33 ms and a quick stick click can begin and end between two reads -
 * the target never changes. Each L3 press is held for at least L3_HOLD_MS so
 * a read always sees it; it is still one press (one target change).
 */
#include <windows.h>
#include <stdio.h>
#include <string.h>

typedef struct {
  WORD wButtons;
  BYTE bLeftTrigger, bRightTrigger;
  SHORT sThumbLX, sThumbLY, sThumbRX, sThumbRY;
} PAD;
typedef struct {
  DWORD dwPacketNumber;
  PAD Gamepad;
} STATE;

enum { B_UP, B_DOWN, B_LEFT, B_RIGHT, B_START, B_BACK, B_L3, B_R3, B_LB, B_RB, B_A, B_B, B_X, B_Y, B_LT, B_RT, NB, B_NONE = -1 };
static const char* kNames[NB] = {"UP", "DOWN", "LEFT", "RIGHT", "START", "BACK", "L3", "R3",
                                 "LB", "RB", "A", "B", "X", "Y", "LT", "RT"};
static const WORD kBits[NB] = {0x0001, 0x0002, 0x0004, 0x0008, 0x0010, 0x0020, 0x0040, 0x0080,
                               0x0100, 0x0200, 0x1000, 0x2000, 0x4000, 0x8000, 0, 0};
#define TRIGGER_ON 30 /* XINPUT_GAMEPAD_TRIGGER_THRESHOLD */

static int g_map[NB];
static DWORD g_l3_hold_ms = 120;
static int g_match_only = 1;
/* The port's guest memory and the match clock it uses itself
 * (port src/frame_rate.cpp kMatchFrames: the match's frame count, 60 a
 * second while a match plays; it stops in menus and while paused). */
static unsigned long long g_guest_base = 0x100000000ull;
#define MATCH_FRAMES 0x82E3CD0Cu
/* wrestler 1's object: the port's match types test "a match running" as this
 * set and the clock above 0 (port src/slobber_knocker.cpp, three_stages.cpp) */
#define MATCH_CHARS 0x82E3CC50u
static int g_was_in_match = -1;
static DWORD g_last_frames, g_frames_moved_at;
static int g_clock_ok = -1; /* -1 not checked, 0 unreadable, 1 ok */
static wchar_t g_log[MAX_PATH];
static HMODULE g_real;
static INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;

typedef DWORD(WINAPI* GetStateFn)(DWORD, STATE*);
#ifdef CAW_RESEARCH_ONLY
DWORD CawResearchPad(DWORD user, void* state, DWORD original); /* isolated test copy only */
#endif
typedef DWORD(WINAPI* Fn1)(void*);
typedef DWORD(WINAPI* Fn4)(void*, void*, void*, void*);
static GetStateFn p_GetState, p_GetStateEx;
static FARPROC p_SetState, p_GetCapabilities, p_Enable, p_GetBattery, p_GetKeystroke, p_GetAudioIds;
static FARPROC p_101, p_102, p_103, p_104, p_108, p_109;

static struct {
  DWORD hold_until;
  int l3_was_down;
  STATE last_out;
  DWORD packet;
  int have_last;
} g_user[4];

static int ByName(const char* s) {
  if (!_stricmp(s, "NONE")) return B_NONE;
  for (int i = 0; i < NB; ++i)
    if (!_stricmp(s, kNames[i])) return i;
  return -2;
}

static void Trim(char* s) {
  char* e = s + strlen(s);
  while (e > s && (e[-1] == ' ' || e[-1] == '\t' || e[-1] == '\r' || e[-1] == '\n')) *--e = 0;
  char* b = s;
  while (*b == ' ' || *b == '\t') ++b;
  memmove(s, b, strlen(b) + 1);
}

static BOOL CALLBACK Init(PINIT_ONCE once, PVOID param, PVOID* ctx) {
  (void)once, (void)param, (void)ctx;
  for (int i = 0; i < NB; ++i) g_map[i] = i;
  wchar_t sys[MAX_PATH];
  GetSystemDirectoryW(sys, MAX_PATH);
  wcscat_s(sys, MAX_PATH, L"\\xinput1_4.dll");
  g_real = LoadLibraryW(sys);
  if (g_real) {
    p_GetState = (GetStateFn)GetProcAddress(g_real, "XInputGetState");
    p_GetStateEx = (GetStateFn)GetProcAddress(g_real, MAKEINTRESOURCEA(100));
    p_SetState = GetProcAddress(g_real, "XInputSetState");
    p_GetCapabilities = GetProcAddress(g_real, "XInputGetCapabilities");
    p_Enable = GetProcAddress(g_real, "XInputEnable");
    p_GetBattery = GetProcAddress(g_real, "XInputGetBatteryInformation");
    p_GetKeystroke = GetProcAddress(g_real, "XInputGetKeystroke");
    p_GetAudioIds = GetProcAddress(g_real, "XInputGetAudioDeviceIds");
    p_101 = GetProcAddress(g_real, MAKEINTRESOURCEA(101));
    p_102 = GetProcAddress(g_real, MAKEINTRESOURCEA(102));
    p_103 = GetProcAddress(g_real, MAKEINTRESOURCEA(103));
    p_104 = GetProcAddress(g_real, MAKEINTRESOURCEA(104));
    p_108 = GetProcAddress(g_real, MAKEINTRESOURCEA(108));
    p_109 = GetProcAddress(g_real, MAKEINTRESOURCEA(109));
  }
#ifndef BLOOD_ONLY /* (the blood mod alone: buttons passed through as they are, no settings, no log) */
  /* settings and a log next to this DLL */
  wchar_t path[MAX_PATH], log[MAX_PATH];
  HMODULE self = NULL;
  GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                     (LPCWSTR)&Init, &self);
  GetModuleFileNameW(self, path, MAX_PATH);
  wchar_t* slash = wcsrchr(path, L'\\');
  if (slash) slash[1] = 0;
  wcscpy_s(log, MAX_PATH, path);
  wcscat_s(path, MAX_PATH, L"xinput_remap.ini");
  wcscat_s(log, MAX_PATH, L"xinput_remap.log");
  wcscpy_s(g_log, MAX_PATH, log);
  FILE* L = _wfopen(log, L"w");
  if (L) fprintf(L, "xinput_remap loaded; System32 xinput1_4.dll %s\n", g_real ? "found" : "MISSING");
  FILE* f = _wfopen(path, L"r");
  if (f) {
    char line[256];
    while (fgets(line, sizeof line, f)) {
      char* hash = strchr(line, '#');
      if (hash) *hash = 0;
      char* eq = strchr(line, '=');
      if (!eq) continue;
      *eq = 0;
      char* k = line;
      char* v = eq + 1;
      Trim(k), Trim(v);
      if (!_stricmp(k, "MATCH_ONLY")) {
        g_match_only = atoi(v) != 0;
        continue;
      }
      if (!_stricmp(k, "GUEST_BASE")) {
        g_guest_base = _strtoui64(v, NULL, 0);
        continue;
      }
      if (!_stricmp(k, "L3_HOLD_MS")) {
        g_l3_hold_ms = (DWORD)strtoul(v, NULL, 10);
        if (g_l3_hold_ms > 500) g_l3_hold_ms = 500;
        continue;
      }
      int from = ByName(k), to = ByName(v);
      if (from >= 0 && to >= -1) g_map[from] = to;
      else if (L) fprintf(L, "ignored line: %s=%s\n", k, v);
    }
    fclose(f);
  } else if (L) {
    fprintf(L, "no xinput_remap.ini: buttons unchanged\n");
  }
  if (L) {
    for (int i = 0; i < NB; ++i)
      if (g_map[i] != i) fprintf(L, "  %s -> %s\n", kNames[i], g_map[i] < 0 ? "NONE" : kNames[g_map[i]]);
    fprintf(L, "  L3 hold %lu ms\n  remap %s\n", (unsigned long)g_l3_hold_ms,
            g_match_only ? "only while a match is being played (menus unchanged)" : "everywhere");
    fclose(L);
  }
#endif
  return TRUE;
}
static void Ready(void) { InitOnceExecuteOnce(&g_once, Init, NULL, NULL); }

/* True while the match clock has moved in the last quarter second. If it
 * can't be read, nothing is remapped (menus must keep working). */
static int ReadBe32(DWORD addr, DWORD* v) {
  unsigned char b[4];
  SIZE_T got = 0;
  if (!ReadProcessMemory(GetCurrentProcess(), (LPCVOID)(ULONG_PTR)(g_guest_base + addr), b, 4, &got) || got != 4)
    return 0;
  *v = (DWORD)b[0] << 24 | (DWORD)b[1] << 16 | (DWORD)b[2] << 8 | b[3];
  return 1;
}

static int InMatch(void) {
  DWORD frames = 0, chars = 0;
  if (!ReadBe32(MATCH_FRAMES, &frames) || !ReadBe32(MATCH_CHARS, &chars)) {
    if (g_clock_ok != 0) {
      g_clock_ok = 0;
      FILE* L = _wfopen(g_log, L"a");
      if (L) fprintf(L, "match clock unreadable: buttons left unchanged\n"), fclose(L);
    }
    return 0;
  }
  if (g_clock_ok != 1) {
    g_clock_ok = 1;
    FILE* L = _wfopen(g_log, L"a");
    if (L) fprintf(L, "match clock found\n"), fclose(L);
  }
  const DWORD now = GetTickCount();
  if (frames != g_last_frames) g_last_frames = frames, g_frames_moved_at = now;
  const int in = chars != 0 && (LONG)frames > 0 && now - g_frames_moved_at < 250;
  if (in != g_was_in_match) {  /* each switch, for checking what the game was doing */
    g_was_in_match = in;
    FILE* L = _wfopen(g_log, L"a");
    if (L) {
      SYSTEMTIME t;
      GetLocalTime(&t);
      fprintf(L, "%02d:%02d:%02d %s (clock %lu, wrestler 1 %08lX)\n", t.wHour, t.wMinute, t.wSecond,
              in ? "MATCH: remapped buttons" : "menus/paused: normal buttons", (unsigned long)frames,
              (unsigned long)chars);
      fclose(L);
    }
  }
  return in;
}

void CameraPad(DWORD user, const void* physical, void* to_game); /* camera.c */

static DWORD Remap(DWORD user, DWORD rc, STATE* s) {
  if (rc != ERROR_SUCCESS || !s) return rc;
  const int remap = !g_match_only || InMatch();
  const PAD in = s->Gamepad;
  PAD out = in;
  out.wButtons = 0;
  out.bLeftTrigger = out.bRightTrigger = 0;
  for (int i = 0; i < NB; ++i) {
    const int down = i == B_LT ? in.bLeftTrigger > TRIGGER_ON : i == B_RT ? in.bRightTrigger > TRIGGER_ON
                                                                         : (in.wButtons & kBits[i]) != 0;
    const int to = remap ? g_map[i] : i;
    if (to < 0) continue;
    if (to == B_LT || to == B_RT) {
      /* a trigger kept as itself stays analog; a button sent to a trigger pulls it fully */
      BYTE v = i == B_LT ? in.bLeftTrigger : i == B_RT ? in.bRightTrigger : (BYTE)(down ? 255 : 0);
      BYTE* t = to == B_LT ? &out.bLeftTrigger : &out.bRightTrigger;
      if (v > *t) *t = v;
    } else if (down) {
      out.wButtons |= kBits[to];
    }
  }
#ifndef BLOOD_ONLY
  CameraPad(user, &in, &out); /* camera.c: the orbit button and right stick */
#endif
  if (user < 4) {
    const DWORD now = GetTickCount();
    const int l3 = (out.wButtons & kBits[B_L3]) != 0;
    if (l3 && !g_user[user].l3_was_down) g_user[user].hold_until = now + g_l3_hold_ms;
    g_user[user].l3_was_down = l3;
    if (!l3 && (LONG)(g_user[user].hold_until - now) > 0) out.wButtons |= kBits[B_L3];
    /* the packet number changes whenever what the game sees changes */
    if (!g_user[user].have_last || memcmp(&g_user[user].last_out.Gamepad, &out, sizeof out) ||
        g_user[user].last_out.dwPacketNumber != s->dwPacketNumber)
      ++g_user[user].packet;
    g_user[user].last_out.dwPacketNumber = s->dwPacketNumber;
    g_user[user].last_out.Gamepad = out;
    g_user[user].have_last = 1;
    s->dwPacketNumber = g_user[user].packet;
  }
  s->Gamepad = out;
  return rc;
}

DWORD WINAPI Proxy_XInputGetState(DWORD user, STATE* s) {
  Ready();
  if (!p_GetState) return ERROR_DEVICE_NOT_CONNECTED;
#ifdef CAW_RESEARCH_ONLY
  return CawResearchPad(user, s, p_GetState(user, s));
#elif defined(BLOOD_ONLY)
  return p_GetState(user, s);
#else
  return Remap(user, p_GetState(user, s), s);
#endif
}
DWORD WINAPI Proxy_XInputGetStateEx(DWORD user, STATE* s) {
  Ready();
  GetStateFn f = p_GetStateEx ? p_GetStateEx : p_GetState;
  if (!f) return ERROR_DEVICE_NOT_CONNECTED;
#ifdef CAW_RESEARCH_ONLY
  return CawResearchPad(user, s, f(user, s));
#elif defined(BLOOD_ONLY)
  return f(user, s);
#else
  return Remap(user, f(user, s), s);
#endif
}

#define PASS4(name, ptr, fail)                                  \
  DWORD WINAPI name(void* a, void* b, void* c, void* d) {      \
    Ready();                                                    \
    return ptr ? ((Fn4)ptr)(a, b, c, d) : (DWORD)(fail);        \
  }
PASS4(Proxy_XInputSetState, p_SetState, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_XInputGetCapabilities, p_GetCapabilities, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_XInputGetBatteryInformation, p_GetBattery, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_XInputGetKeystroke, p_GetKeystroke, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_XInputGetAudioDeviceIds, p_GetAudioIds, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_Ord101, p_101, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_Ord102, p_102, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_Ord103, p_103, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_Ord104, p_104, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_Ord108, p_108, ERROR_DEVICE_NOT_CONNECTED)
PASS4(Proxy_Ord109, p_109, ERROR_DEVICE_NOT_CONNECTED)

void WINAPI Proxy_XInputEnable(BOOL enable) {
  Ready();
  if (p_Enable) ((void(WINAPI*)(BOOL))p_Enable)(enable);
}

#ifndef CAMERA_ONLY
void RingMovesStart(HMODULE self); /* ring_moves.c */
void BuriedAliveStart(HMODULE self); /* buried_alive.c */
#endif
void CameraStart(HMODULE self); /* camera.c */
void ModMenuStart(HMODULE self); /* modmenu.c (only with modmenu.ini) */
#ifdef CAW_RESEARCH_ONLY
void CawProbeStart(HMODULE self); /* caw_probe.c: read-only CAW function inventory */
#endif
void RagdollStart(HMODULE self); /* ragdoll.c (only with ragdoll.ini) */
void BigModelStart(HMODULE self); /* bigmodel.c: wrestler models past the 768 KB match budget */
#if !defined(CAMERA_ONLY) || defined(WITH_BLOOD)
void BloodStart(HMODULE self); /* blood.c (only with blood.ini) */
#endif

/* Only inside the game: this DLL sits next to svr2011.exe as xinput1_4.dll, so any other program in that folder that
 * loads XInput gets it too - the Mod Maker 2.2.0 does (LoadLibrary "xinput1_4.dll", its own folder first), and the
 * mods below then patched it as if it were the game: an access violation at start-up, 2026-11-09. Outside
 * svr2011.exe no mod starts; the XInput calls are still passed on to Windows' own. */
static int InGame(void) {
  wchar_t path[MAX_PATH];
  DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
  if (!n || n >= MAX_PATH) return 0;
  const wchar_t* name = path + n;
  while (name > path && name[-1] != L'\\' && name[-1] != L'/') --name;
  return lstrcmpiW(name, L"svr2011.exe") == 0;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r) {
  (void)r;
  if (reason != DLL_PROCESS_ATTACH) return TRUE;
  DisableThreadLibraryCalls(h);
  if (!InGame()) return TRUE;
#ifdef CAW_RESEARCH_ONLY
  CawProbeStart(h);
#else
#ifndef NO_BIGMODEL /* (the shared packages leave it out: -DNO_BIGMODEL) */
  BigModelStart(h);
#endif
#ifndef CAMERA_ONLY /* (the shared third person camera package: build_camera_package.sh) */
  RingMovesStart(h), BuriedAliveStart(h), RagdollStart(h);
#endif
#ifndef BLOOD_ONLY /* (the blood mod alone: build_blood_only.sh - no camera, no mod menu) */
  CameraStart(h);
#endif
#if !defined(CAMERA_ONLY) || defined(WITH_BLOOD)
  BloodStart(h);
#endif
#ifndef BLOOD_ONLY
  ModMenuStart(h);
#endif
#endif /* CAW_RESEARCH_ONLY */
  return TRUE;
}

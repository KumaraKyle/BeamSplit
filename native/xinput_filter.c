// xinput_filter - a per-folder XInput proxy that exposes exactly ONE physical
// controller to the process that loads it.
//
// Why this exists: BeamNG keeps a single binding set for the whole XInput
// device type, so two pads always drive every instance. The only place to
// separate them is before the game sees them. Windows resolves xinput1_4.dll
// from the EXE's own directory first, and every BeamSplit instance has its own
// Bin64, so each instance can load a differently-configured copy of this.
//
// It forwards everything to the real system DLL, but:
//   - reports ONLY the configured physical pad, remapped to user index 0
//   - reports every other index as not connected
//
// Config: xinput_filter.ini next to this DLL:
//     [filter]
//     pad=0            ; zero-based physical XInput index to expose
//
// Build (x64):
//   gcc -O2 -shared -o xinput1_4.dll xinput_filter.c -Wl,--kill-at -lkernel32

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>

#define ERROR_DEVICE_NOT_CONNECTED 1167L

typedef struct { WORD wButtons; BYTE bLeftTrigger, bRightTrigger;
                 SHORT sThumbLX, sThumbLY, sThumbRX, sThumbRY; } XI_GAMEPAD;
typedef struct { DWORD dwPacketNumber; XI_GAMEPAD Gamepad; } XI_STATE;
typedef struct { WORD wLeftMotorSpeed, wRightMotorSpeed; } XI_VIBRATION;
typedef struct { BYTE Type, SubType; WORD Flags; XI_GAMEPAD Gamepad; XI_VIBRATION Vibration; } XI_CAPABILITIES;
typedef struct { BYTE BatteryType, BatteryLevel; } XI_BATTERY;
typedef struct { WORD VirtualKey; WCHAR Unicode; WORD Flags; BYTE UserIndex, HidCode; } XI_KEYSTROKE;

static HMODULE g_real = NULL;
static int     g_pad  = 0;      // physical index we expose
static int     g_init = 0;

typedef DWORD (WINAPI *fnGetState)(DWORD, XI_STATE*);
typedef DWORD (WINAPI *fnSetState)(DWORD, XI_VIBRATION*);
typedef DWORD (WINAPI *fnGetCaps)(DWORD, DWORD, XI_CAPABILITIES*);
typedef void  (WINAPI *fnEnable)(BOOL);
typedef DWORD (WINAPI *fnGetBattery)(DWORD, BYTE, XI_BATTERY*);
typedef DWORD (WINAPI *fnGetKeystroke)(DWORD, DWORD, XI_KEYSTROKE*);
typedef DWORD (WINAPI *fnGetAudioIds)(DWORD, LPWSTR, UINT*, LPWSTR, UINT*);
typedef DWORD (WINAPI *fnGetDSound)(DWORD, GUID*, GUID*);

static fnGetState     p_GetState;
static fnSetState     p_SetState;
static fnGetCaps      p_GetCaps;
static fnEnable       p_Enable;
static fnGetBattery   p_GetBattery;
static fnGetKeystroke p_GetKeystroke;
static fnGetAudioIds  p_GetAudioIds;
static fnGetDSound    p_GetDSound;

static char  g_log[MAX_PATH] = {0};
static char  g_ini[MAX_PATH] = {0};   // declared here: init() uses it below
static DWORD g_lastCheck = 0;
// Raw Win32 only - the CRT is not safe to use from DllMain, and an fopen that
// silently fails there looks identical to "our code never ran".
static void logf_(const char *fmt, ...) {
    if (!g_log[0]) return;
    char buf[512];
    va_list ap; va_start(ap, fmt);
    int n = wvsprintfA(buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    buf[n] = '\n';
    HANDLE h = CreateFileA(g_log, FILE_APPEND_DATA, FILE_SHARE_READ|FILE_SHARE_WRITE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD w; WriteFile(h, buf, (DWORD)(n + 1), &w, NULL);
    CloseHandle(h);
}

static void init(void) {
    if (g_init) return;
    g_init = 1;

    char dir[MAX_PATH], ini[MAX_PATH], sys[MAX_PATH];

    // read pad index from xinput_filter.ini beside this DLL
    HMODULE self = NULL;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCSTR)&init, &self);
    GetModuleFileNameA(self, dir, MAX_PATH);
    char *slash = strrchr(dir, '\\');
    if (slash) *(slash + 1) = 0;
    snprintf(ini, MAX_PATH, "%sxinput_filter.ini", dir);
    lstrcpynA(g_ini, ini, MAX_PATH);   // kept for hot-reload
    g_pad = GetPrivateProfileIntA("filter", "pad", 0, ini);
    snprintf(g_log, MAX_PATH, "%sxinput_filter.log", dir);
    logf_("[init] pad=%d ini=%s", g_pad, ini);

    // always forward to the REAL system dll, never to ourselves
    GetSystemDirectoryA(sys, MAX_PATH);
    strcat(sys, "\\xinput1_4.dll");
    g_real = LoadLibraryA(sys);
    if (!g_real) return;

    p_GetState     = (fnGetState)    GetProcAddress(g_real, "XInputGetState");
    p_SetState     = (fnSetState)    GetProcAddress(g_real, "XInputSetState");
    p_GetCaps      = (fnGetCaps)     GetProcAddress(g_real, "XInputGetCapabilities");
    p_Enable       = (fnEnable)      GetProcAddress(g_real, "XInputEnable");
    p_GetBattery   = (fnGetBattery)  GetProcAddress(g_real, "XInputGetBatteryInformation");
    p_GetKeystroke = (fnGetKeystroke)GetProcAddress(g_real, "XInputGetKeystroke");
    p_GetAudioIds  = (fnGetAudioIds) GetProcAddress(g_real, "XInputGetAudioDeviceIds");
    p_GetDSound    = (fnGetDSound)   GetProcAddress(g_real, "XInputGetDSoundAudioDeviceGuids");
}

// Re-read the ini periodically so a pad can be reassigned WITHOUT restarting
// the game. BeamSplit rewrites xinput_filter.ini live; this picks it up within
// a second. (The DirectInput side, devreorder, still only reads its config at
// startup - but BeamNG takes pad STATE through XInput, so live remapping here
// is what actually moves control between players.)
static void refresh(void) {
    DWORD now = GetTickCount();
    if (now - g_lastCheck < 1000) return;
    g_lastCheck = now;
    if (!g_ini[0]) return;
    int p = GetPrivateProfileIntA("filter", "pad", -1, g_ini);
    if (p >= 0 && p != g_pad) {
        logf_("[reload] pad %d -> %d", g_pad, p);
        g_pad = p;
    }
}

// index 0 -> our physical pad; everything else -> not connected.
// pad < 0 means this instance is a KEYBOARD/MOUSE player: report no controllers at
// all, so a stray pad can never drive it.
static int map(DWORD i, DWORD *out) {
    init();
    refresh();
    static int seen = 0;
    if (seen < 12) {
        seen++;
        logf_("[map] index %d -> %s", (int)i,
              (g_pad < 0 ? "NONE (keyboard player)" : (i == 0 ? "OURS" : "NOTCONNECTED")));
    }
    if (g_pad < 0) return 0;
    if (i != 0) return 0;
    *out = (DWORD)g_pad;
    return 1;
}

__declspec(dllexport) DWORD WINAPI XInputGetState(DWORD i, XI_STATE *s) {
    DWORD r; if (!map(i, &r) || !p_GetState) return ERROR_DEVICE_NOT_CONNECTED;
    return p_GetState(r, s);
}
__declspec(dllexport) DWORD WINAPI XInputSetState(DWORD i, XI_VIBRATION *v) {
    DWORD r; if (!map(i, &r) || !p_SetState) return ERROR_DEVICE_NOT_CONNECTED;
    return p_SetState(r, v);
}
__declspec(dllexport) DWORD WINAPI XInputGetCapabilities(DWORD i, DWORD f, XI_CAPABILITIES *c) {
    DWORD r; if (!map(i, &r) || !p_GetCaps) return ERROR_DEVICE_NOT_CONNECTED;
    return p_GetCaps(r, f, c);
}
__declspec(dllexport) void WINAPI XInputEnable(BOOL e) {
    init(); if (p_Enable) p_Enable(e);
}
__declspec(dllexport) DWORD WINAPI XInputGetBatteryInformation(DWORD i, BYTE t, XI_BATTERY *b) {
    DWORD r; if (!map(i, &r) || !p_GetBattery) return ERROR_DEVICE_NOT_CONNECTED;
    return p_GetBattery(r, t, b);
}
__declspec(dllexport) DWORD WINAPI XInputGetKeystroke(DWORD i, DWORD res, XI_KEYSTROKE *k) {
    DWORD r; if (!map(i, &r) || !p_GetKeystroke) return ERROR_DEVICE_NOT_CONNECTED;
    return p_GetKeystroke(r, res, k);
}
// 1_4 only - missing this is exactly what made XInputPlus crash the game
__declspec(dllexport) DWORD WINAPI XInputGetAudioDeviceIds(DWORD i, LPWSTR rid, UINT *rc, LPWSTR cid, UINT *cc) {
    DWORD r; if (!map(i, &r) || !p_GetAudioIds) return ERROR_DEVICE_NOT_CONNECTED;
    return p_GetAudioIds(r, rid, rc, cid, cc);
}
// 9_1_0 / 1_3 era, kept so one binary can serve every name the game asks for
__declspec(dllexport) DWORD WINAPI XInputGetDSoundAudioDeviceGuids(DWORD i, GUID *rg, GUID *cg) {
    DWORD r; if (!map(i, &r) || !p_GetDSound) return ERROR_DEVICE_NOT_CONNECTED;
    return p_GetDSound(r, rg, cg);
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved) {
    (void)h; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) init();
    return TRUE;
}

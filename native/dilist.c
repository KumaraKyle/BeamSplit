// dilist - prints the DirectInput game controllers and their instance GUIDs.
//
// devreorder hides devices by GUID, and identical controllers share a name
// ("Controller (Xbox One For Windows)" twice), so GUIDs are the only way to
// tell them apart. Shipping this avoids depending on SharpDX just to list
// devices.
//
// Output, one device per line:   <index>\t<guid>\t<name>
//
// Build: gcc -O2 -o dilist.exe dilist.c -ldinput8 -ldxguid -lole32 -luuid

#define INITGUID
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <dinput.h>
#include <stdio.h>

static int g_index = 0;

static BOOL CALLBACK enumCb(LPCDIDEVICEINSTANCEA inst, LPVOID ctx) {
    (void)ctx;
    const GUID *g = &inst->guidInstance;
    printf("%d\t{%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x}\t%s\n",
           g_index++,
           (unsigned long)g->Data1, g->Data2, g->Data3,
           g->Data4[0], g->Data4[1], g->Data4[2], g->Data4[3],
           g->Data4[4], g->Data4[5], g->Data4[6], g->Data4[7],
           inst->tszInstanceName);
    return DIENUM_CONTINUE;
}

int main(void) {
    LPDIRECTINPUT8A di = NULL;
    HRESULT hr = DirectInput8Create(GetModuleHandle(NULL), DIRECTINPUT_VERSION,
                                    &IID_IDirectInput8A, (LPVOID*)&di, NULL);
    if (FAILED(hr)) { fprintf(stderr, "DirectInput8Create failed: 0x%08lx\n", (unsigned long)hr); return 1; }

    hr = IDirectInput8_EnumDevices(di, DI8DEVCLASS_GAMECTRL, enumCb, NULL, DIEDFL_ALLDEVICES);
    if (FAILED(hr)) { fprintf(stderr, "EnumDevices failed: 0x%08lx\n", (unsigned long)hr); return 1; }

    IDirectInput8_Release(di);
    return 0;
}

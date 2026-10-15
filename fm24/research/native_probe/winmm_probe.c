/* FM24/Proton load probe. Only FM24's two static WINMM imports are forwarded. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

typedef unsigned int MMRESULT;
typedef MMRESULT (WINAPI *timer_function)(UINT);
#define TIMERR_NOCANDO 97

static HMODULE real_winmm;
static timer_function real_begin;
static timer_function real_end;

static BOOL load_real_winmm(void)
{
    WCHAR path[MAX_PATH];
    UINT length = GetSystemDirectoryW(path, MAX_PATH);
    static const WCHAR suffix[] = L"\\winmm.dll";
    if (!length || length + (sizeof(suffix) / sizeof(suffix[0])) > MAX_PATH)
        return FALSE;
    lstrcatW(path, suffix);
    real_winmm = LoadLibraryW(path);
    if (!real_winmm)
        return FALSE;
    union { FARPROC proc; timer_function timer; } symbol;
    symbol.proc = GetProcAddress(real_winmm, "timeBeginPeriod");
    real_begin = symbol.timer;
    symbol.proc = GetProcAddress(real_winmm, "timeEndPeriod");
    real_end = symbol.timer;
    return real_begin && real_end;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance;
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
        OutputDebugStringA("FMFACE_FM24_WINMM_PROBE_LOADED");
    return TRUE;
}

MMRESULT WINAPI timeBeginPeriod(UINT period)
{
    if (!real_begin && !load_real_winmm())
        return TIMERR_NOCANDO;
    OutputDebugStringA("FMFACE_FM24_WINMM_PROBE_TIMER_BEGIN");
    return real_begin(period);
}

MMRESULT WINAPI timeEndPeriod(UINT period)
{
    if (!real_end && !load_real_winmm())
        return TIMERR_NOCANDO;
    return real_end(period);
}

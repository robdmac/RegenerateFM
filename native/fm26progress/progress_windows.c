/* SPDX-License-Identifier: GPL-3.0-or-later (see NOTICE.md).
   regeneratefm-progress.exe --pid PID --log LOG --name USER_DATA_NAME
   regeneratefm-progress.exe --demo LANGUAGE FRACTION STAGE

   FM26's progress window on Windows (and Proton) while BepInEx prepares the
   game: RegenerateFM.Start.dll starts it from inside FM26 (process PID)
   before BepInEx. It reads BepInEx/LogOutput.log (LOG) as BepInEx writes it,
   and shows the window only if BepInEx downloads Unity's libraries or makes
   FM26's interop assemblies; on any other start it exits once BepInEx has
   started its chainloader. It closes when RegenerateFM's plugin has loaded,
   when FM26 exits, or if BepInEx reports it failed. The text is in FM26's
   language, from current_language.xml in its user-data folder
   (USER_DATA_NAME, "Football Manager 26" or "... Demo"). --demo shows the
   window at a fixed point, for checking its layout. progress_macos.swift is
   the same window on the Mac. */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include "progress_strings.h"

enum { HEADING, NOTE, STAGE_DOWNLOAD, STAGE_READ, STAGE_BUILD, STAGE_START };
enum { ID_TIMER = 1, ID_CLOSE = 2 };

/* BepInEx's log lines in the order a first start writes them, with how far
   along each is and the stage it belongs to. The fractions follow how long
   each part took on a Mac and in a Windows VM (writing the assemblies is the
   longest). */
static const struct { const char *text; double done; int stage; } MILESTONES[] = {
    {"Downloading unity base libraries", 0.02, STAGE_DOWNLOAD},
    {"Detected outdated interop assemblies", 0.08, STAGE_READ},
    {"Extracting unity base libraries", 0.08, STAGE_READ},
    {"Running Cpp2IL", 0.10, STAGE_READ},
    {"Cpp2IL finished", 0.38, STAGE_BUILD},
    {"Generating interop assemblies", 0.40, STAGE_BUILD},
    {"Writing assemblies...", 0.70, STAGE_BUILD},
    {"Writing method pointer map", 0.88, STAGE_BUILD},
    {"Il2CppInteropGen] Done!", 0.90, STAGE_BUILD},
    {"Chainloader initialized", 0.93, STAGE_START},
};
/* "Done in" lines between "Generating interop assemblies" and "Writing
   assemblies...": the generator's steps, which move the bar from 0.40 to 0.70. */
#define GENERATOR_STEPS 28

static const wchar_t *const *texts = ENGLISH;
static BOOL right_to_left;
static HWND window, heading, note, bar, stage_label;
static HANDLE game;
static const wchar_t *log_path;
static FILETIME started;
static DWORD started_ticks, milestone_ticks, closing_ticks;
static BOOL shown, demo;
static double shown_fraction, last_done = -1;
static int last_stage = -1;

/* FM26's interface language from its preferences, lower case, or "". */
static void read_language(const wchar_t *user_data, char *language, size_t size)
{
    language[0] = 0;
    wchar_t pattern[MAX_PATH], path[MAX_PATH], local[MAX_PATH];
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH)) return;
    _snwprintf(pattern, MAX_PATH, L"%ls\\Sports Interactive\\%ls\\Preferences\\version *", local, user_data);
    WIN32_FIND_DATAW found;
    HANDLE search = FindFirstFileW(pattern, &found);
    if (search == INVALID_HANDLE_VALUE) return;
    FILETIME newest = {0};
    char text[512];
    do {
        if (!(found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        _snwprintf(path, MAX_PATH, L"%ls\\Sports Interactive\\%ls\\Preferences\\%ls\\current_language.xml", local, user_data, found.cFileName);
        HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
        if (file == INVALID_HANDLE_VALUE) continue;
        FILETIME written;
        DWORD read = 0;
        if (GetFileTime(file, NULL, NULL, &written) && CompareFileTime(&written, &newest) > 0 &&
            ReadFile(file, text, sizeof(text) - 1, &read, NULL)) {
            text[read] = 0;
            const char *value = strstr(text, "value=\"");
            const char *end = value ? strchr(value + 7, '"') : NULL;
            if (end && (size_t)(end - value - 7) < size) {
                memcpy(language, value + 7, end - value - 7);
                language[end - value - 7] = 0;
                for (char *c = language; *c; c++) if (*c >= 'A' && *c <= 'Z') *c += 32;
                newest = written;
            }
        }
        CloseHandle(file);
    } while (FindNextFileW(search, &found));
    FindClose(search);
}

/* The texts for FM's language name: its own, else its base language's
   ("spanish (mexico)" uses spanish's), else English. */
static void choose_texts(const char *language)
{
    char base[64];
    snprintf(base, sizeof(base), "%s", language);
    char *regional = strstr(base, " (");
    if (regional) *regional = 0;
    for (int pass = 0; pass < 2; pass++)
        for (size_t i = 0; i < sizeof(TRANSLATED) / sizeof(TRANSLATED[0]); i++)
            if (!strcmp(TRANSLATED[i].language, pass ? base : language)) {
                texts = TRANSLATED[i].texts;
                right_to_left = !strcmp(base, "arabic");
                return;
            }
}

/* The log if BepInEx has written it since this started, else NULL. */
static char *read_log(void)
{
    HANDLE file = CreateFileW(log_path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return NULL;
    FILETIME written;
    LARGE_INTEGER size;
    char *text = NULL;
    if (GetFileTime(file, NULL, NULL, &written) && CompareFileTime(&written, &started) >= 0 &&
        GetFileSizeEx(file, &size) && size.QuadPart < (16 << 20) && (text = malloc((size_t)size.QuadPart + 1))) {
        DWORD read = 0;
        ReadFile(file, text, (DWORD)size.QuadPart, &read, NULL);
        text[read] = 0;
    }
    CloseHandle(file);
    return text;
}

static HFONT font(int points, int weight)
{
    HDC screen = GetDC(NULL);
    int height = -MulDiv(points, GetDeviceCaps(screen, LOGPIXELSY), 72);
    ReleaseDC(NULL, screen);
    return CreateFontW(height, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
}

static void show_window(void)
{
    if (shown) return;
    shown = TRUE;
    ShowWindow(window, SW_SHOWNORMAL);
    SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
}

static void set_progress(double fraction, int stage)
{
    if (fraction > shown_fraction) {
        shown_fraction = fraction;
        SendMessageW(bar, PBM_SETPOS, (WPARAM)(fraction * 1000), 0);
    }
    if (stage != last_stage) {
        last_stage = stage;
        SetWindowTextW(stage_label, texts[stage]);
    }
}

/* Reads the log and moves the bar, and decides whether to show, close or
   keep watching. Between milestones the bar creeps towards the next one, so
   a long step doesn't look stuck. */
static void check(void)
{
    DWORD now = GetTickCount();
    if (closing_ticks) {
        if (now - closing_ticks > 2500) DestroyWindow(window);
        return;
    }
    if (game && WaitForSingleObject(game, 0) == WAIT_OBJECT_0) { DestroyWindow(window); return; }
    char *text = read_log();
    if (!text) {
        /* No log from this start yet: BepInEx may not have begun. */
        if (now - started_ticks > 180000) DestroyWindow(window);
        return;
    }
    if (strstr(text, "Failed to download Unity base libraries") || strstr(text, "Failed to generate Il2Cpp interop")) {
        free(text);
        DestroyWindow(window);
        return;
    }
    BOOL preparing = FALSE;
    double done = 0, next = MILESTONES[0].done;
    int stage = STAGE_DOWNLOAD;
    size_t count = sizeof(MILESTONES) / sizeof(MILESTONES[0]);
    for (size_t i = 0; i < count; i++) {
        if (!strstr(text, MILESTONES[i].text)) continue;
        if (MILESTONES[i].done >= done) {
            done = MILESTONES[i].done;
            stage = MILESTONES[i].stage;
            next = i + 1 < count ? MILESTONES[i + 1].done : 1;
        }
        if (MILESTONES[i].stage != STAGE_START) preparing = TRUE;
    }
    const char *generating = strstr(text, "Generating interop assemblies");
    if (generating && !strstr(text, "Writing assemblies...")) {
        int steps = 0;
        for (const char *at = generating; (at = strstr(at, "Il2CppInteropGen] Done in")); at++) steps++;
        done = 0.40 + 0.30 * (steps < GENERATOR_STEPS ? steps : GENERATOR_STEPS) / GENERATOR_STEPS;
        next = done + 0.30 / GENERATOR_STEPS;
    }
    BOOL complete = strstr(text, "Chainloader startup complete") != NULL;
    BOOL chainloader = strstr(text, "Chainloader initialized") != NULL;
    free(text);
    if (!preparing) {
        /* An ordinary start: nothing to show. */
        if (chainloader || now - started_ticks > 600000) DestroyWindow(window);
        return;
    }
    show_window();
    if (complete) {
        set_progress(1, STAGE_START);
        closing_ticks = now;
        return;
    }
    if (done != last_done) { last_done = done; milestone_ticks = now; }
    double seconds = (now - milestone_ticks) / 1000.0;
    set_progress(done + (next - done) * 0.85 * (1 - exp(-seconds / 30)), stage);
    if (now - started_ticks > 3600000) DestroyWindow(window);
}

static LRESULT CALLBACK procedure(HWND handle, UINT message, WPARAM wparam, LPARAM lparam)
{
    switch (message) {
    case WM_TIMER: if (!demo) check(); return 0;
    case WM_CTLCOLORSTATIC: SetBkMode((HDC)wparam, TRANSPARENT); return (LRESULT)GetSysColorBrush(COLOR_WINDOW);
    case WM_CLOSE: DestroyWindow(handle); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(handle, message, wparam, lparam);
}

static void create_window(HINSTANCE instance)
{
    INITCOMMONCONTROLSEX controls = {sizeof(controls), ICC_PROGRESS_CLASS};
    InitCommonControlsEx(&controls);
    WNDCLASSW kind = {0};
    kind.lpfnWndProc = procedure;
    kind.hInstance = instance;
    kind.hCursor = LoadCursorW(NULL, (LPCWSTR)IDC_ARROW);
    kind.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
    kind.hIcon = LoadIconW(NULL, (LPCWSTR)IDI_INFORMATION);
    kind.lpszClassName = L"RegenerateFMProgress";
    RegisterClassW(&kind);
    HDC screen = GetDC(NULL);
    int dpi = GetDeviceCaps(screen, LOGPIXELSY);
    ReleaseDC(NULL, screen);
#define S(value) MulDiv(value, dpi, 96)
    RECT frame = {0, 0, S(500), S(176)};
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU, extended = right_to_left ? WS_EX_LAYOUTRTL : 0;
    AdjustWindowRectEx(&frame, style, FALSE, extended);
    int width = frame.right - frame.left, height = frame.bottom - frame.top;
    window = CreateWindowExW(extended, kind.lpszClassName, L"RegenerateFM", style,
                             (GetSystemMetrics(SM_CXSCREEN) - width) / 2, (GetSystemMetrics(SM_CYSCREEN) - height) / 3,
                             width, height, NULL, NULL, instance, NULL);
    heading = CreateWindowExW(0, L"STATIC", texts[HEADING], WS_CHILD | WS_VISIBLE | SS_LEFT, S(22), S(18), S(456), S(28), window, NULL, instance, NULL);
    note = CreateWindowExW(0, L"STATIC", texts[NOTE], WS_CHILD | WS_VISIBLE | SS_LEFT, S(22), S(52), S(456), S(54), window, NULL, instance, NULL);
    bar = CreateWindowExW(0, PROGRESS_CLASSW, NULL, WS_CHILD | WS_VISIBLE | PBS_SMOOTH, S(22), S(112), S(456), S(16), window, NULL, instance, NULL);
    stage_label = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_LEFT, S(22), S(136), S(456), S(24), window, NULL, instance, NULL);
#undef S
    SendMessageW(heading, WM_SETFONT, (WPARAM)font(13, FW_SEMIBOLD), TRUE);
    HFONT body = font(10, FW_NORMAL);
    SendMessageW(note, WM_SETFONT, (WPARAM)body, TRUE);
    SendMessageW(stage_label, WM_SETFONT, (WPARAM)body, TRUE);
    SendMessageW(bar, PBM_SETRANGE32, 0, 1000);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command_line, int show)
{
    (void)previous; (void)command_line; (void)show;
    SetProcessDPIAware();
    int count;
    wchar_t **args = CommandLineToArgvW(GetCommandLineW(), &count);
    const wchar_t *user_data = L"Football Manager 26";
    char language[64] = "";
    double demo_fraction = 0;
    int demo_stage = STAGE_BUILD;
    DWORD pid = 0;
    for (int i = 1; i < count; i++) {
        if (!wcscmp(args[i], L"--pid") && i + 1 < count) pid = wcstoul(args[++i], NULL, 10);
        else if (!wcscmp(args[i], L"--log") && i + 1 < count) log_path = args[++i];
        else if (!wcscmp(args[i], L"--name") && i + 1 < count) user_data = args[++i];
        else if (!wcscmp(args[i], L"--demo") && i + 3 < count) {
            demo = TRUE;
            WideCharToMultiByte(CP_UTF8, 0, args[++i], -1, language, sizeof(language), NULL, NULL);
            demo_fraction = wcstod(args[++i], NULL);
            demo_stage = STAGE_DOWNLOAD + _wtoi(args[++i]);
        }
    }
    if (!demo && !log_path) return 2;
    if (!demo) read_language(user_data, language, sizeof(language));
    choose_texts(language);
    if (pid) game = OpenProcess(SYNCHRONIZE, FALSE, pid);
    GetSystemTimeAsFileTime(&started);
    started_ticks = GetTickCount();
    create_window(instance);
    if (demo) {
        show_window();
        set_progress(demo_fraction, demo_stage < STAGE_DOWNLOAD || demo_stage > STAGE_START ? STAGE_BUILD : demo_stage);
    }
    SetTimer(window, ID_TIMER, 300, NULL);
    MSG message;
    while (GetMessageW(&message, NULL, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return 0;
}

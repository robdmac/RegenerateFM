/* MIT. Experimental FM24 24.4.2 native UI event bridge for Windows/Proton. */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <bcrypt.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "supported_build.h"

#define PROFILE_EVENT 0x464d4647u /* FMFG */
#define SQUAD_EVENT   0x464d4653u /* FMFS */
#define IDLE_EVENT    0x49444c45u /* IDLE */
#define SKIN_EVENT    0x47436853u /* GChS: change skin */
#define STAGE_EVENT   0x6368736bu /* chsk: one queued stage of a skin change */

extern void *fmface_exports[];
extern const char *fmface_export_names[];
extern const unsigned short fmface_export_ordinals[];
extern const unsigned int fmface_export_count;

typedef int (*event_handler)(void *, unsigned int, void *);
static HMODULE self_module, real_winmm;
static INIT_ONCE initialized = INIT_ONCE_STATIC_INIT;
static event_handler original_event;
static HANDLE event_ready;
static volatile LONG sequence;
/* One pending click, written by the UI thread and taken by the worker. */
static SRWLOCK pending_lock = SRWLOCK_INIT;
static struct { unsigned int event; LONG64 click_ms; LONG person; LONG read_ms; } pending;
static unsigned short bridge_port;
static char bridge_token[129];
static WCHAR log_path[MAX_PATH];
static unsigned char *game_base;

static void log_line(const char *text)
{
    char line[160];
    SYSTEMTIME now;
    GetLocalTime(&now);
    int count = snprintf(line, sizeof(line), "%04u-%02u-%02u %02u:%02u:%02u %s\r\n",
                         now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, text);
    if (count < 0 || count >= (int)sizeof(line)) return;
    OutputDebugStringA(line);
    HANDLE file = CreateFileW(log_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE) {
        DWORD written;
        WriteFile(file, line, (DWORD)count, &written, NULL);
        CloseHandle(file);
    }
}

static void show_message(const WCHAR *text)
{
    MessageBoxW(NULL, text, L"FM Face", MB_OK | MB_ICONINFORMATION | MB_TOPMOST | MB_SETFOREGROUND);
}

static BOOL sibling_path(WCHAR *path, const WCHAR *name)
{
    DWORD size = GetModuleFileNameW(self_module, path, MAX_PATH);
    if (!size || size >= MAX_PATH) return FALSE;
    WCHAR *slash = wcsrchr(path, L'\\');
    if (!slash || (size_t)(slash - path + 1) + wcslen(name) >= MAX_PATH) return FALSE;
    wcscpy(slash + 1, name);
    return TRUE;
}

static BOOL supported_executable(void)
{
    static const unsigned char expected[32] = FM24_HASH_BYTES;
    WCHAR path[MAX_PATH];
    unsigned char digest[32], buffer[65536];
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    HANDLE file = INVALID_HANDLE_VALUE;
    BOOL ok = FALSE;
    DWORD read;
    DWORD size = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (!size || size >= MAX_PATH) goto done;
    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) goto done;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) < 0) goto done;
    if (BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) < 0) goto done;
    for (;;) {
        if (!ReadFile(file, buffer, sizeof(buffer), &read, NULL)) goto done;
        if (!read) break;
        if (BCryptHashData(hash, buffer, read, 0) < 0) goto done;
    }
    if (BCryptFinishHash(hash, digest, sizeof(digest), 0) < 0) goto done;
    ok = memcmp(expected, digest, sizeof(expected)) == 0;
done:
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    return ok;
}

/* Wall-clock milliseconds since the Unix epoch, comparable with the
   companion's clock on the same machine. */
static LONG64 unix_ms(void)
{
    FILETIME now;
    GetSystemTimePreciseAsFileTime(&now);
    ULONGLONG ticks = ((ULONGLONG)now.dwHighDateTime << 32) | now.dwLowDateTime;
    return (LONG64)((ticks - 116444736000000000ULL) / 10000ULL);
}

/* Copy game memory without faulting on a stale or invalid pointer. */
static BOOL read_game(const void *address, void *buffer, SIZE_T size)
{
    SIZE_T copied = 0;
    return address && ReadProcessMemory(GetCurrentProcess(), address, buffer, size, &copied) &&
           copied == size;
}

/* Widget layout of the pinned FM24 build, from live inspection. Container
   objects embed a widget part (+0x38) holding the UI manager, the parent
   object, and the skin ID; children are a vector of object pointers at +0x100.
   The header's subtitle ("Role - Club (ID:n)" with screen IDs enabled) is the
   text of the 'subs' widget. */
#define EVENT_SOURCE      0x20
#define WIDGET_PART       0x38
#define WIDGET_MANAGER    (WIDGET_PART + 0x10)
#define WIDGET_PARENT     (WIDGET_PART + 0x18)
#define WIDGET_ID         (WIDGET_PART + 0x24)
#define WIDGET_CHILDREN   0x100
#define WIDGET_TEXT       (WIDGET_PART + 0x158)
#define SKIN_ID(a, b, c, d) (((unsigned int)(a) << 24) | ((unsigned int)(b) << 16) | ((c) << 8) | (d))
#define MAX_CHILDREN      4096
#define MAX_SEARCH        4096

static BOOL read_pointer(const unsigned char *address, unsigned char **value)
{
    return read_game(address, value, sizeof(*value));
}

/* True if the object is a widget of this UI manager; *id gets its skin ID. */
static BOOL widget_id(const unsigned char *object, const void *manager, unsigned int *id)
{
    unsigned char *owner;
    return read_pointer(object + WIDGET_MANAGER, &owner) && owner == manager &&
           read_game(object + WIDGET_ID, id, sizeof(*id));
}

/* Copies up to max child pointers; returns the count, or -1 if invalid. */
static int widget_children(const unsigned char *object, unsigned char **children, int max)
{
    unsigned char *range[2];
    if (!read_game(object + WIDGET_CHILDREN, range, sizeof(range)) || range[1] < range[0] ||
        (range[1] - range[0]) % sizeof(void *))
        return -1;
    size_t count = (size_t)(range[1] - range[0]) / sizeof(void *);
    if (count > (size_t)max) return -1;
    if (count && !read_game(range[0], children, count * sizeof(void *))) return -1;
    return (int)count;
}

static unsigned char *search_children[MAX_CHILDREN];
static unsigned char *search_stack[MAX_SEARCH];

/* Reads the person ID shown in the open screen's header, starting from the
   FM Face menu that was clicked. Runs on FM24's UI thread inside the menu
   callback, so the header cannot change to another screen while it is read.
   Every read is guarded, and any surprise (wrong source widget, no unique
   header or subtitle, no "(ID:n)") returns 0, which the companion refuses. */
static LONG clicked_person_id(void *manager, void *event)
{
    unsigned char *source, *object, *head = NULL, *subs = NULL;
    if (!event || !read_pointer((unsigned char *)event + EVENT_SOURCE, &source)) return 0;
    unsigned int skin_id;
    if (!widget_id(source, manager, &skin_id) ||
        (skin_id != SKIN_ID('f','m','n','P') && skin_id != SKIN_ID('f','m','n','S')))
        return 0;
    object = source;
    for (int depth = 0; depth < 64 && !head && widget_id(object, manager, &skin_id); ++depth) {
        int count = widget_children(object, search_children, MAX_CHILDREN);
        for (int i = 0; i < count; ++i) {
            if (!widget_id(search_children[i], manager, &skin_id) || skin_id != SKIN_ID('h','e','a','d'))
                continue;
            if (head) return 0;  /* More than one header: refuse. */
            head = search_children[i];
        }
        if (!read_pointer(object + WIDGET_PARENT, &object)) return 0;
    }
    if (!head) return 0;
    int top = 0, visited = 0;
    search_stack[top++] = head;
    while (top) {
        unsigned char *node = search_stack[--top];
        if (++visited > MAX_SEARCH) return 0;
        if (widget_id(node, manager, &skin_id) && skin_id == SKIN_ID('s','u','b','s')) {
            if (subs) return 0;  /* More than one subtitle: refuse. */
            subs = node;
        }
        int count = widget_children(node, search_children, MAX_CHILDREN);
        for (int i = 0; i < count; ++i) {
            if (top == MAX_SEARCH) return 0;
            if (widget_id(search_children[i], manager, &skin_id)) search_stack[top++] = search_children[i];
        }
    }
    unsigned char *text;
    unsigned int length;
    char title[256];
    if (!subs || !read_pointer(subs + WIDGET_TEXT, &text) ||
        !read_game(text, &length, sizeof(length)) || length < 7 || length >= sizeof(title) ||
        !read_game(text + 4, title, length))
        return 0;
    title[length] = 0;
    /* The subtitle must end in "(ID:<1-10 digits, no leading zero>)". */
    char *open = strrchr(title, '(');
    if (!open || strncmp(open, "(ID:", 4) || title[length - 1] != ')') return 0;
    char *digits = open + 4;
    size_t count = (size_t)(title + length - 1 - digits);
    if (count < 1 || count > 10 || digits[0] == '0') return 0;
    unsigned long long value = 0;
    for (size_t i = 0; i < count; ++i) {
        if (digits[i] < '0' || digits[i] > '9') return 0;
        value = value * 10 + (unsigned)(digits[i] - '0');
    }
    return value <= 0x7fffffffu ? (LONG)value : 0;
}

/* In-process skin reload. The companion asks over loopback; the UI thread
   repeats FM24's own "reload the current skin" command (a queued GChS event
   naming the current skin) on its next IDLE event. FM24 then runs the change
   as queued chsk stages that count manager+0x292 down from 5 to -1; the
   reload is complete when a chsk stage leaves it at -1. Between changes the
   counter rests at -1, or at 5 after the game's initial, unqueued skin load;
   0-4 means a change is still running. */
enum { RELOAD_NONE, RELOAD_REQUESTED, RELOAD_RUNNING, RELOAD_DONE, RELOAD_FAILED };
static volatile LONG reload_state;
static ULONGLONG reload_posted;
static BOOL reload_started;

typedef void (*value_function)(void *);
typedef void (*pair_function)(void *, void *);
typedef void *(*top_function)(void *);
typedef void (*init_function)(void *, unsigned int, void *);
typedef void (*set_function)(void *, unsigned int, void *);
typedef void (*bool_function)(void *, char);
#define GAME_FUNCTION(type, rva) ((type)(game_base + (rva)))

static BOOL reload_stage(void *manager, signed char *stage)
{
    return read_game((unsigned char *)manager + RELOAD_STAGE_OFFSET, stage, sizeof(*stage));
}

static void set_flag(void *event, unsigned int key, char value)
{
    unsigned char flag[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(bool_function, RELOAD_VALUE_BOOL_RVA)(flag, value);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_RVA)(event, key, flag);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(flag);
}

/* UI thread only. Mirrors the validated build's own reload command. */
static BOOL post_skin_reload(void *manager)
{
    void *global;
    signed char stage;
    if (!read_game(game_base + RELOAD_MANAGER_GLOBAL_RVA, &global, sizeof(global)) || !global ||
        GAME_FUNCTION(top_function, RELOAD_TOP_MANAGER_RVA)(global) != manager ||
        !reload_stage(manager, &stage) || (stage != -1 && stage != 5))
        return FALSE;
    unsigned char skin[64] __attribute__((aligned(16))) = {0};
    unsigned char event[256] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(value_function, RELOAD_VALUE_INIT_RVA)(skin);
    GAME_FUNCTION(pair_function, RELOAD_CURRENT_SKIN_RVA)(manager, skin);
    GAME_FUNCTION(init_function, RELOAD_EVENT_INIT_RVA)(event, SKIN_EVENT, NULL);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_COPY_RVA)(event, SKIN_ID('S','k','N','M'), skin);
    set_flag(event, SKIN_ID('d','l','o','g'), 1);
    set_flag(event, SKIN_ID('d','e','l','a'), 1);
    set_flag(event, SKIN_ID('c','h','e','v'), 0);
    GAME_FUNCTION(pair_function, RELOAD_POST_EVENT_RVA)(manager, event);
    GAME_FUNCTION(value_function, RELOAD_EVENT_FREE_RVA)(event);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(skin);
    return TRUE;
}

static void finish_reload(LONG state, const char *line)
{
    InterlockedExchange(&reload_state, state);
    log_line(line);
}

/* Called on the UI thread after FM24 has handled each manager event. */
static void reload_tick(void *manager, unsigned int id)
{
    LONG state = reload_state;
    signed char stage;
    if (state == RELOAD_REQUESTED && id == IDLE_EVENT) {
        reload_started = FALSE;
        reload_posted = GetTickCount64();
        /* FM24 can handle the posted GChS before the post returns. */
        InterlockedExchange(&reload_state, RELOAD_RUNNING);
        if (post_skin_reload(manager))
            log_line("FMFACE24_RELOAD_POSTED");
        else
            finish_reload(RELOAD_FAILED, "FMFACE24_RELOAD_NOT_POSTED");
    } else if (state == RELOAD_RUNNING) {
        if (id == SKIN_EVENT) reload_started = TRUE;
        if (id == STAGE_EVENT && reload_started && reload_stage(manager, &stage) && stage == -1)
            finish_reload(RELOAD_DONE, "FMFACE24_RELOAD_DONE");
        else if (GetTickCount64() - reload_posted > (reload_started ? 60000 : 10000))
            finish_reload(RELOAD_FAILED, "FMFACE24_RELOAD_TIMEOUT");
    }
}

static void queue_click(unsigned int id, LONG64 click_ms, LONG person, LONG read_ms)
{
    AcquireSRWLockExclusive(&pending_lock);
    pending.event = id;
    pending.click_ms = click_ms;
    pending.person = person;
    pending.read_ms = read_ms;
    ReleaseSRWLockExclusive(&pending_lock);
    SetEvent(event_ready);
}

static int native_event(void *object, unsigned int id, void *event)
{
    if (id != PROFILE_EVENT && id != SQUAD_EVENT) {
        int result = original_event(object, id, event);
        if (id == IDLE_EVENT || id == SKIN_EVENT || id == STAGE_EVENT) reload_tick(object, id);
        return result;
    }
    /* No blocking I/O or generation on the UI thread; only guarded memory
       reads. The command carries the person shown in the clicked screen's
       header and the click time. The companion refuses it unless the open
       profile is still that person, and refuses a command that arrives late. */
    LONG64 click_ms = unix_ms();
    ULONGLONG started = GetTickCount64();
    LONG person = id == PROFILE_EVENT ? clicked_person_id(object, event) : 0;
    queue_click(id, click_ms, person, (LONG)(GetTickCount64() - started));
    return 1;
}

/* Squad screen "Players" menu. FM24 builds it from provider objects; the
   provider that adds "Export Team for Versus Competition" (its last item) is
   extended to append "Generate missing squad faces…", and its command
   handler receives that item's FMFS command when it is chosen. */
typedef void (*menu_add_function)(void *, void **, void *);
typedef char (*menu_event_function)(void *, unsigned int, void *, void *);
typedef char (*record_check_function)(void *);
typedef void *(*item_new_function)(void *, void **, void *, unsigned int, unsigned int, unsigned int);
typedef void (*string_init_function)(void *, const char *, int);
typedef void (*fourcc_function)(void *, unsigned int);
typedef void (*release_function)(void *, int);
static menu_add_function original_menu_add;
static menu_event_function original_menu_event;

static void squad_menu_add(void *provider, void **builder, void *context)
{
    original_menu_add(provider, builder, context);
    if (!builder || !*builder || !GAME_FUNCTION(record_check_function, SQUAD_MENU_IS_RECORD_RVA)(context))
        return;
    unsigned char text[16] __attribute__((aligned(16))) = {0};
    unsigned char command[64] __attribute__((aligned(16))) = {0};
    void *item = NULL;
    GAME_FUNCTION(string_init_function, SQUAD_MENU_STRING_INIT_RVA)(text, "Generate missing squad faces\xe2\x80\xa6", -1);
    GAME_FUNCTION(item_new_function, SQUAD_MENU_ITEM_NEW_RVA)(*builder, &item, text, SQUAD_EVENT,
                                                             SKIN_ID('E','N','O','N'), SKIN_ID('N','U','L','L'));
    GAME_FUNCTION(value_function, SQUAD_MENU_STRING_FREE_RVA)(text);
    if (!item) return;
    GAME_FUNCTION(fourcc_function, SQUAD_MENU_VALUE_FOURCC_RVA)(command, SQUAD_EVENT);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_RVA)((unsigned char *)item + 8, SKIN_ID('e','v','i','d'), command);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(command);
    GAME_FUNCTION(pair_function, SQUAD_MENU_ITEM_APPEND_RVA)(provider, &item);
    /* Drop this function's reference; the menu holds its own. */
    if (!InterlockedDecrement((volatile LONG *)((unsigned char *)item + 0x20)))
        (*(release_function *)*(void **)item)(item, 1);
}

static char squad_menu_event(void *provider, unsigned int id, void *event, void *extra)
{
    if (id != SQUAD_EVENT) return original_menu_event(provider, id, event, extra);
    queue_click(SQUAD_EVENT, unix_ms(), 0, 0);
    return 1;
}

static SOCKET open_bridge(void)
{
    SOCKET socket_fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_fd == INVALID_SOCKET) return INVALID_SOCKET;
    struct sockaddr_in destination;
    memset(&destination, 0, sizeof(destination));
    destination.sin_family = AF_INET;
    destination.sin_port = htons(bridge_port);
    destination.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(socket_fd, (struct sockaddr *)&destination, sizeof(destination)) == SOCKET_ERROR) {
        closesocket(socket_fd);
        return INVALID_SOCKET;
    }
    return socket_fd;
}

enum { REPLY_NONE, REPLY_ACCEPTED, REPLY_BUSY, REPLY_STALE, REPLY_SOCKET_ERROR };

/* Send one click and wait for the ACK carrying this exact sequence. The
   companion answers a retransmission with its cached reply, so a lost packet
   or reply is retried without starting a second request. Replies to earlier
   sequences, which can arrive late, are discarded. */
static int exchange(SOCKET socket_fd, const char *packet, int count, LONG serial)
{
    char accepted[64], busy[64], stale[64];
    snprintf(accepted, sizeof(accepted), "FMFACE24 ACK %ld accepted", (long)serial);
    snprintf(busy, sizeof(busy), "FMFACE24 ACK %ld busy", (long)serial);
    snprintf(stale, sizeof(stale), "FMFACE24 ACK %ld stale", (long)serial);
    for (int attempt = 0; attempt < 3; ++attempt) {
        if (send(socket_fd, packet, count, 0) != count) return REPLY_SOCKET_ERROR;
        ULONGLONG deadline = GetTickCount64() + 700;
        for (;;) {
            ULONGLONG now = GetTickCount64();
            if (now >= deadline) break;
            fd_set readable;
            FD_ZERO(&readable);
            FD_SET(socket_fd, &readable);
            struct timeval wait = {0, (long)(deadline - now) * 1000};
            int ready = select(0, &readable, NULL, NULL, &wait);
            if (ready == SOCKET_ERROR) return REPLY_SOCKET_ERROR;
            if (!ready) break;
            char reply[100];
            int received = recv(socket_fd, reply, sizeof(reply) - 1, 0);
            if (received <= 0) {
                /* ICMP port unreachable: no companion is listening yet. */
                if (WSAGetLastError() == WSAECONNRESET) { Sleep(150); break; }
                return REPLY_SOCKET_ERROR;
            }
            reply[received] = 0;
            if (!strcmp(reply, accepted)) return REPLY_ACCEPTED;
            if (!strcmp(reply, busy)) return REPLY_BUSY;
            if (!strcmp(reply, stale)) return REPLY_STALE;
        }
    }
    return REPLY_NONE;
}

static DWORD WINAPI bridge_worker(LPVOID unused)
{
    (void)unused;
    WSADATA data;
    BOOL network = WSAStartup(MAKEWORD(2,2), &data) == 0;
    SOCKET socket_fd = INVALID_SOCKET;
    log_line(network ? "FMFACE24_BRIDGE_READY" : "FMFACE24_BRIDGE_WSA_FAILED");
    while (WaitForSingleObject(event_ready, INFINITE) == WAIT_OBJECT_0) {
        AcquireSRWLockExclusive(&pending_lock);
        unsigned int id = pending.event;
        LONG64 click_ms = pending.click_ms;
        LONG person = pending.person;
        LONG read_ms = pending.read_ms;
        pending.event = 0;
        ReleaseSRWLockExclusive(&pending_lock);
        if (!id) continue;
        if (id == PROFILE_EVENT) {
            char line[96];
            snprintf(line, sizeof(line), "FMFACE24_PROFILE_CLICK person=%ld read_ms=%ld", (long)person, (long)read_ms);
            log_line(line);
        } else {
            log_line("FMFACE24_SQUAD_CLICK");
        }
        if (!network) {
            show_message(L"FM Face could not start its network bridge. Restart FM24 and check fmface24.log.");
            continue;
        }
        if (socket_fd == INVALID_SOCKET) socket_fd = open_bridge();
        if (socket_fd == INVALID_SOCKET) {
            log_line("FMFACE24_BRIDGE_SOCKET_FAILED");
            show_message(L"FM Face could not open its local connection. Restart FM24 and check fmface24.log.");
            continue;
        }
        LONG serial = InterlockedIncrement(&sequence);
        char packet[256];
        int count = snprintf(packet, sizeof(packet), "FMFACE24 5 %lu %ld %s %lld %ld %s",
                             (unsigned long)GetCurrentProcessId(), (long)serial,
                             id == PROFILE_EVENT ? "profile" : "squad",
                             (long long)click_ms, (long)person, bridge_token);
        int result = exchange(socket_fd, packet, count, serial);
        if (result == REPLY_ACCEPTED) { log_line("FMFACE24_CLICK_ACCEPTED"); continue; }
        if (result == REPLY_BUSY) {
            log_line("FMFACE24_CLICK_BUSY");
            show_message(L"FM Face is already processing a request. Please wait for it to finish.");
            continue;
        }
        if (result == REPLY_STALE) {
            log_line("FMFACE24_CLICK_STALE");
            show_message(L"FM Face received this click too late to be sure which screen was open, "
                         L"so nothing was generated. Choose the menu item again.");
            continue;
        }
        if (result == REPLY_SOCKET_ERROR) {
            /* Recreate the socket for the next click instead of failing forever. */
            log_line("FMFACE24_BRIDGE_SOCKET_ERROR");
            closesocket(socket_fd);
            socket_fd = INVALID_SOCKET;
        }
        log_line("FMFACE24_COMPANION_UNAVAILABLE");
        show_message(L"The FM24 Face companion is unavailable. Start it with --native-menu and try again.");
    }
    if (socket_fd != INVALID_SOCKET) closesocket(socket_fd);
    if (network) WSACleanup();
    return 0;
}

/* Loopback reload requests: "FMFACE24R 1 <serial> <token>". The companion
   repeats the same serial to poll; the reply is "FMFACE24R <serial> <status>"
   with status queued, busy, done or failed. Only the skin reload is offered. */
static DWORD WINAPI reload_listener(LPVOID unused)
{
    (void)unused;
    WSADATA data;
    if (WSAStartup(MAKEWORD(2,2), &data)) { log_line("FMFACE24_RELOAD_WSA_FAILED"); return 0; }
    SOCKET socket_fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    struct sockaddr_in local;
    memset(&local, 0, sizeof(local));
    local.sin_family = AF_INET;
    local.sin_port = htons((unsigned short)(bridge_port + 1));
    local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (socket_fd == INVALID_SOCKET || bind(socket_fd, (struct sockaddr *)&local, sizeof(local))) {
        log_line("FMFACE24_RELOAD_PORT_FAILED");
        if (socket_fd != INVALID_SOCKET) closesocket(socket_fd);
        WSACleanup();
        return 0;
    }
    log_line("FMFACE24_RELOAD_READY");
    long long last = 0;
    for (;;) {
        char packet[256], token[160], extra;
        long long serial;
        struct sockaddr_in peer;
        int size = sizeof(peer);
        int received = recvfrom(socket_fd, packet, sizeof(packet) - 1, 0, (struct sockaddr *)&peer, &size);
        if (received <= 0) continue;
        packet[received] = 0;
        if (peer.sin_addr.s_addr != htonl(INADDR_LOOPBACK) ||
            sscanf(packet, "FMFACE24R 1 %lld %159s %c", &serial, token, &extra) != 2 ||
            serial <= 0 || strcmp(token, bridge_token))
            continue;
        const char *status;
        LONG state = reload_state;
        if (serial > last) {
            if (state == RELOAD_REQUESTED || state == RELOAD_RUNNING) {
                status = "busy";
            } else {
                last = serial;
                InterlockedExchange(&reload_state, RELOAD_REQUESTED);
                log_line("FMFACE24_RELOAD_REQUESTED");
                status = "queued";
            }
        } else if (serial == last) {
            status = state == RELOAD_DONE ? "done" : state == RELOAD_FAILED ? "failed" : "queued";
        } else {
            continue;
        }
        char reply[64];
        int count = snprintf(reply, sizeof(reply), "FMFACE24R %lld %s", serial, status);
        sendto(socket_fd, reply, count, 0, (struct sockaddr *)&peer, sizeof(peer));
    }
}

/* Replace one validated vtable pointer; refuses unless it holds the original. */
static BOOL replace_slot(void **slot, void *expected, void *replacement)
{
    DWORD previous, ignored;
    if (*slot != expected || !VirtualProtect(slot, sizeof(*slot), PAGE_READWRITE, &previous)) return FALSE;
    InterlockedExchangePointer((PVOID volatile *)slot, replacement);
    VirtualProtect(slot, sizeof(*slot), previous, &ignored);
    return TRUE;
}

static void install_squad_menu(unsigned char *base)
{
    original_menu_add = (menu_add_function)(base + SQUAD_MENU_ADD_ORIGINAL_RVA);
    original_menu_event = (menu_event_function)(base + SQUAD_MENU_EVENT_ORIGINAL_RVA);
    void **add_slot = (void **)(base + SQUAD_MENU_ADD_SLOT_RVA);
    void **event_slot = (void **)(base + SQUAD_MENU_EVENT_SLOT_RVA);
    if (*add_slot != (void *)original_menu_add || *event_slot != (void *)original_menu_event) {
        log_line("FMFACE24_SQUAD_MENU_MISMATCH"); return;
    }
    /* Command handler first, so the item never exists without its handler. */
    if (!replace_slot(event_slot, (void *)original_menu_event, (void *)squad_menu_event) ||
        !replace_slot(add_slot, (void *)original_menu_add, (void *)squad_menu_add)) {
        log_line("FMFACE24_SQUAD_MENU_PROTECT_FAILED"); return;
    }
    log_line("FMFACE24_SQUAD_MENU_INSTALLED");
}

static void install_event_hook(void)
{
    WCHAR config[MAX_PATH], token[129];
    if (!sibling_path(log_path, L"fmface24.log") || !sibling_path(config, L"fmface24.ini")) return;
    if (!GetPrivateProfileIntW(L"fmface", L"enabled", 0, config)) return;
    int port = GetPrivateProfileIntW(L"fmface", L"port", 0, config);
    DWORD length = GetPrivateProfileStringW(L"fmface", L"token", L"", token, 129, config);
    if (port < 1024 || port > 65535 || length < 40 || length > 128) {
        log_line("FMFACE24_INVALID_CONFIG"); return;
    }
    for (DWORD i = 0; i < length; ++i) {
        WCHAR c = token[i];
        if (!((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') ||
              (c >= L'0' && c <= L'9') || c == L'_' || c == L'-')) {
            log_line("FMFACE24_INVALID_TOKEN"); return;
        }
        bridge_token[i] = (char)c;
    }
    bridge_token[length] = 0;
    bridge_port = (unsigned short)port;
    if (!supported_executable()) { log_line("FMFACE24_UNSUPPORTED_EXECUTABLE"); return; }
    unsigned char *base = game_base = (unsigned char *)GetModuleHandleW(NULL);
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    if (nt->FileHeader.TimeDateStamp != FM24_TIMESTAMP || nt->OptionalHeader.SizeOfImage != FM24_IMAGE_SIZE) {
        log_line("FMFACE24_UNSUPPORTED_IMAGE"); return;
    }
    static const unsigned char prologue[] = FM24_HANDLER_BYTES;
    event_handler *slot = (event_handler *)(base + VTABLE_SLOT_RVA);
    original_event = (event_handler)(base + HANDLER_RVA);
    if (*slot != original_event || memcmp(base + HANDLER_RVA, prologue, sizeof(prologue))) {
        log_line("FMFACE24_HANDLER_MISMATCH"); return;
    }
    event_ready = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!event_ready) return;
    HANDLE thread = CreateThread(NULL, 0, bridge_worker, NULL, 0, NULL);
    if (!thread) { CloseHandle(event_ready); event_ready = NULL; return; }
    CloseHandle(thread);
    DWORD previous;
    if (!VirtualProtect(slot, sizeof(*slot), PAGE_READWRITE, &previous)) {
        log_line("FMFACE24_VTABLE_PROTECT_FAILED"); return;
    }
    InterlockedExchangePointer((PVOID volatile *)slot, (PVOID)native_event);
    DWORD ignored;
    VirtualProtect(slot, sizeof(*slot), previous, &ignored);
    log_line("FMFACE24_NATIVE_MENU_HOOK_INSTALLED");
    install_squad_menu(base);
    if (bridge_port < 65535 && (thread = CreateThread(NULL, 0, reload_listener, NULL, 0, NULL)))
        CloseHandle(thread);
}

static uintptr_t missing_export(void)
{
    SetLastError(ERROR_PROC_NOT_FOUND);
    return 0;
}

static BOOL CALLBACK initialize(PINIT_ONCE once, PVOID parameter, PVOID *context)
{
    (void)once; (void)parameter; (void)context;
    WCHAR path[MAX_PATH];
    UINT length = GetSystemDirectoryW(path, MAX_PATH);
    if (!length || length + 11 >= MAX_PATH) return FALSE;
    wcscat(path, L"\\winmm.dll");
    real_winmm = LoadLibraryW(path);
    if (!real_winmm || real_winmm == self_module) return FALSE;
    for (unsigned int i = 0; i < fmface_export_count; ++i) {
        FARPROC function = GetProcAddress(real_winmm, fmface_export_names[i] ? fmface_export_names[i] :
                                          (LPCSTR)(uintptr_t)fmface_export_ordinals[i]);
        fmface_exports[i] = function ? (void *)function : (void *)missing_export;
    }
    install_event_hook();
    return TRUE;
}

void *fmface_resolve_export(unsigned int index)
{
    if (!InitOnceExecuteOnce(&initialized, initialize, NULL, NULL))
        fmface_exports[index] = (void *)missing_export;
    return fmface_exports[index];
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        self_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

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
#define UPDATE_ITEM   0x464d4655u /* FMFU: the portrait popup's "Update face" row */
#define IDLE_EVENT    0x49444c45u /* IDLE */
#define SKIN_EVENT    0x47436853u /* GChS: change skin */
#define STAGE_EVENT   0x6368736bu /* chsk: one queued stage of a skin change */
#define DIALOG_YES    0x464d4459u /* FMDY: FM Face dialog answered Yes or OK */
#define DIALOG_NO     0x464d444eu /* FMDN: FM Face dialog answered No */
#define MENU_OPEN     0x6d656f70u /* meop: a popup menu opened; posted to the popup button's parent */

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
static struct { unsigned int event, item; LONG64 click_ms; LONG person; LONG read_ms; } pending;
static unsigned short bridge_port;
/* Names this game run in click packets (the start time in milliseconds).
   Wine reuses Windows process IDs, so after a restart a process ID alone
   made the companion take serial 1 for a replay of the previous run's. */
static LONG64 bridge_instance;
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

static BOOL queue_dialog(int kind, const char *title, const char *text);

/* Worker threads only. FM24 shows the message in its own dialog when the UI
   thread can take it; otherwise (another dialog pending, or the dialog
   functions unusable) a Windows message box is used. */
static void show_message(const WCHAR *text)
{
    char utf8[1024];
    int count = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, sizeof(utf8), NULL, NULL);
    if (count > 0 && queue_dialog(2, "FM Face", utf8)) return;
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
   object, and the skin ID; children are a vector of object pointers at +0x100. */
#define EVENT_SOURCE      0x20
#define WIDGET_PART       0x38
#define WIDGET_MANAGER    (WIDGET_PART + 0x10)
#define WIDGET_PARENT     (WIDGET_PART + 0x18)
#define WIDGET_ID         (WIDGET_PART + 0x24)
#define WIDGET_CHILDREN   0x100
#define SKIN_ID(a, b, c, d) (((unsigned int)(a) << 24) | ((unsigned int)(b) << 16) | ((c) << 8) | (d))
#define MAX_CHILDREN      4096
#define MAX_SEARCH        4096

/* The sidebar ('side', a class of its own) keeps the object of the screen FM24
   has navigated to at +0x280: 0x01000000 | the person's handle for a person.
   It is a child of an ancestor of every widget on the screen, and a skin
   reload sets it again. FM24's database object, a global in fm.exe, lists
   every person at their handle; a person keeps its handle at +8 and its
   canonical record at +12, which starts with the person ID twice. */
#define SIDEBAR_VTABLE_RVA     0x57E3B48
#define SIDEBAR_OBJECT         0x280
#define DATABASE_GLOBAL_RVA    0x6426D90
#define DATABASE_PEOPLE        0x920   /* the people's slot seen first; see object_person_id */
#define DATABASE_SCAN          0x2000
#define PERSON_OBJECT          0x01000000u
#define PERSON_HANDLE          8
#define MAX_DEPTH              64

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

/* The person ID at this handle in the database's vector at slot, or 0. */
static LONG slot_person_id(unsigned char *database, size_t slot, unsigned int handle)
{
    unsigned char *range[2], *person;
    struct { unsigned int handle, id, again, zero; unsigned char marker[4]; } record;
    if (!read_game(database + slot, range, sizeof(range)) || !range[0] || range[1] <= range[0] ||
        (range[1] - range[0]) % sizeof(void *) || handle >= (size_t)(range[1] - range[0]) / sizeof(void *) ||
        !read_pointer(range[0] + (size_t)handle * sizeof(void *), &person) || !person ||
        !read_game(person + PERSON_HANDLE, &record, sizeof(record)))
        return 0;
    /* As in the companion: the ID twice, then a marker of 00 xx 58. */
    if (record.handle != handle || record.id != record.again || record.marker[0] || record.marker[2] != 0x58 ||
        record.id < 1 || record.id > 0x7fffffffu)
        return 0;
    return (LONG)record.id;
}

/* The person ID of a person object ID, through FM24's person table, or 0.
   The database keeps one vector per kind of object, indexed by handle, and
   which slot holds people changes from one run of FM24 to the next: the last
   slot found is tried first, then every slot in the first DATABASE_SCAN
   bytes, which must all agree on the person. UI thread only. */
static size_t people_slot = DATABASE_PEOPLE;

static LONG object_person_id(unsigned int object_id)
{
    unsigned char *database;
    unsigned int handle = object_id & 0xFFFFFFu;
    LONG id, found = 0;
    size_t found_slot = 0;
    if ((object_id & 0xFF000000u) != PERSON_OBJECT || !read_pointer(game_base + DATABASE_GLOBAL_RVA, &database))
        return 0;
    if ((id = slot_person_id(database, people_slot, handle))) return id;
    for (size_t slot = 0; slot < DATABASE_SCAN; slot += sizeof(void *)) {
        if (slot == people_slot || !(id = slot_person_id(database, slot, handle))) continue;
        if (found && id != found) return 0;
        if (!found) { found = id; found_slot = slot; }
    }
    if (found) people_slot = found_slot;
    return found;
}

/* Reads the person the open screen shows, starting from FM Face's portrait
   popup (the clicked menu's source widget). Runs on FM24's UI thread inside
   the menu callback, so the screen cannot change while it is read. Every read
   is guarded, and any surprise (wrong source widget, no single sidebar, not a
   person) returns 0, which the companion refuses. */
static LONG popup_person_id(void *manager, unsigned char *popup)
{
    unsigned char *object = popup, *sidebar = NULL, *vtable;
    unsigned int skin_id, object_id;
    if (!widget_id(popup, manager, &skin_id) ||
        (skin_id != SKIN_ID('f','m','n','P') && skin_id != SKIN_ID('f','m','n','S') &&
         skin_id != SKIN_ID('P','p','r','f')))
        return 0;
    for (int depth = 0; depth < MAX_DEPTH && !sidebar && widget_id(object, manager, &skin_id); ++depth) {
        int count = widget_children(object, search_children, MAX_CHILDREN);
        for (int i = 0; i < count; ++i) {
            if (!widget_id(search_children[i], manager, &skin_id) || skin_id != SKIN_ID('s','i','d','e') ||
                !read_pointer(search_children[i], &vtable) || vtable != game_base + SIDEBAR_VTABLE_RVA)
                continue;
            if (sidebar) return 0;  /* More than one sidebar: refuse. */
            sidebar = search_children[i];
        }
        if (!read_pointer(object + WIDGET_PARENT, &object)) return 0;
    }
    if (!sidebar || !read_game(sidebar + SIDEBAR_OBJECT, &object_id, sizeof(object_id))) return 0;
    return object_person_id(object_id);
}

static LONG clicked_person_id(void *manager, void *event)
{
    unsigned char *source;
    if (!event || !read_pointer((unsigned char *)event + EVENT_SOURCE, &source)) return 0;
    return popup_person_id(manager, source);
}

/* The row that was chosen: FMFG (Generate new face) or FMFU (Update face).
   The portrait popup posts its select_event, FMFG, for every row and names
   the row in the event's 'slct' property; a row FM Face adds to the Overview
   tab menu sends its own ID as the event, with the same 'slct'. An event
   keeps its properties as a vector of {fourcc key, value pointer} at +0; a
   fourcc value holds its code at +8. The 'slct' value must have the same type
   as the event's own 'evid' value. Anything else returns 0. */
#define MAX_PROPERTIES 32
static unsigned int selected_item(void *event)
{
    unsigned char *range[2];
    struct { unsigned long long key; unsigned char *value; } properties[MAX_PROPERTIES];
    struct { void *type; unsigned int code, unused; } evid, slct;
    unsigned char *evid_value = NULL, *slct_value = NULL;
    if (!event || !read_game(event, range, sizeof(range)) || range[1] <= range[0] ||
        (size_t)(range[1] - range[0]) > sizeof(properties) ||
        (range[1] - range[0]) % sizeof(properties[0]) ||
        !read_game(range[0], properties, (SIZE_T)(range[1] - range[0])))
        return 0;
    int count = (int)((range[1] - range[0]) / sizeof(properties[0]));
    for (int i = 0; i < count; ++i) {
        unsigned int key = (unsigned int)properties[i].key;
        if (key == SKIN_ID('e','v','i','d')) evid_value = properties[i].value;
        else if (key == SKIN_ID('s','l','c','t')) slct_value = properties[i].value;
    }
    if (!read_game(evid_value, &evid, sizeof(evid)) || !read_game(slct_value, &slct, sizeof(slct)) ||
        (evid.code != PROFILE_EVENT && evid.code != UPDATE_ITEM) || slct.type != evid.type)
        return 0;
    /* A row of the Overview tab menu sends its own ID as the event. */
    if (evid.code == UPDATE_ITEM && slct.code != UPDATE_ITEM) return 0;
    return slct.code == PROFILE_EVENT || slct.code == UPDATE_ITEM ? slct.code : 0;
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

enum { DIALOG_NONE, DIALOG_REQUESTED, DIALOG_SHOWN, DIALOG_ANSWER_YES, DIALOG_ANSWER_NO, DIALOG_FAILED,
       DIALOG_CLOSED };
static volatile LONG dialog_state;

static void finish_reload(LONG state, const char *line)
{
    /* Reloading the skin destroys any open FM Face dialog (DIALOG_SHOWN). */
    InterlockedCompareExchange(&dialog_state, DIALOG_CLOSED, DIALOG_SHOWN);
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

/* FM24's own modal dialogs. The validated build opens its "Quit game and
   return to start screen?" query by building a property record (titl, text,
   acte = the event posted for Yes, noet = the event ID posted for No, widt,
   heig) and passing it to a dialog function with the screen manager as owner
   and parent. FM Face does the same on the UI thread's next IDLE event, with
   its own FMDY/FMDN events, which the manager hook below receives. Kind 1 is
   a Yes/No question. Kind 2 is an OK message: the same dialog, after which
   its "no  " button is hidden (skin property hidn) and its "yes " button is
   relabelled OK; neither FM24's OK/Cancel panel dialog nor a dbtn value
   gave an OK-only text dialog. Kind 3 is squad progress: the same, with the
   one button labelled "Stop after this face". A skin reload destroys open
   dialogs without an event, so a finished reload marks the dialog closed.
   Only an unanswered question blocks a new FM Face dialog. */
static SRWLOCK dialog_lock = SRWLOCK_INIT;
static int dialog_kind;
static char dialog_title[256], dialog_text[1024];
static ULONGLONG dialog_shown_at;
static void *dialog_object;

typedef void *(*dialog_function)(void *, void *, void *);
typedef void (*string_value_function)(void *, void *);
typedef void (*float_value_function)(void *, float);
typedef void (*string_init_function)(void *, const char *, int);
typedef void (*fourcc_function)(void *, unsigned int);

static BOOL queue_dialog(int kind, const char *title, const char *text)
{
    if (!game_base) return FALSE;
    LONG state = dialog_state;
    /* A dialog closed without an answer (for example by a skin reload) would
       otherwise block every later one; after ten minutes it is presumed gone. */
    if (state == DIALOG_REQUESTED ||
        (state == DIALOG_SHOWN && dialog_kind == 1 && GetTickCount64() - dialog_shown_at < 600000))
        return FALSE;
    AcquireSRWLockExclusive(&dialog_lock);
    dialog_kind = kind;
    snprintf(dialog_title, sizeof(dialog_title), "%s", title);
    snprintf(dialog_text, sizeof(dialog_text), "%s", text);
    ReleaseSRWLockExclusive(&dialog_lock);
    InterlockedExchange(&dialog_state, DIALOG_REQUESTED);
    return TRUE;
}

static void set_text(void *record, unsigned int key, const char *text)
{
    unsigned char string[16] __attribute__((aligned(16))) = {0};
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(string_init_function, SQUAD_MENU_STRING_INIT_RVA)(string, text, -1);
    GAME_FUNCTION(string_value_function, DIALOG_VALUE_STRING_RVA)(value, string);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_RVA)(record, key, value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(value);
    GAME_FUNCTION(value_function, SQUAD_MENU_STRING_FREE_RVA)(string);
}

static void set_number(void *record, unsigned int key, float number)
{
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(float_value_function, DIALOG_VALUE_FLOAT_RVA)(value, number);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_RVA)(record, key, value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(value);
}

/* UI thread only. */
static BOOL show_dialog(void *manager)
{
    void *global;
    if (!read_game(game_base + RELOAD_MANAGER_GLOBAL_RVA, &global, sizeof(global)) || !global ||
        GAME_FUNCTION(top_function, RELOAD_TOP_MANAGER_RVA)(global) != manager)
        return FALSE;
    unsigned char record[256] __attribute__((aligned(16))) = {0};
    unsigned char event[256] __attribute__((aligned(16))) = {0};
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    AcquireSRWLockShared(&dialog_lock);
    GAME_FUNCTION(value_function, DIALOG_RECORD_INIT_RVA)(record);
    set_text(record, SKIN_ID('t','i','t','l'), dialog_title);
    set_text(record, SKIN_ID('t','e','x','t'), dialog_text);
    ReleaseSRWLockShared(&dialog_lock);
    GAME_FUNCTION(init_function, RELOAD_EVENT_INIT_RVA)(event, DIALOG_YES, NULL);
    GAME_FUNCTION(string_value_function, DIALOG_VALUE_EVENT_RVA)(value, event);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_RVA)(record, SKIN_ID('a','c','t','e'), value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(value);
    GAME_FUNCTION(value_function, RELOAD_EVENT_FREE_RVA)(event);
    GAME_FUNCTION(fourcc_function, SQUAD_MENU_VALUE_FOURCC_RVA)(value, DIALOG_NO);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_RVA)(record, SKIN_ID('n','o','e','t'), value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(value);
    set_number(record, SKIN_ID('w','i','d','t'), 520.0f);
    set_number(record, SKIN_ID('h','e','i','g'), 250.0f);
    void *dialog = GAME_FUNCTION(dialog_function, DIALOG_SHOW_QUERY_RVA)(manager, manager, record);
    GAME_FUNCTION(value_function, RELOAD_EVENT_FREE_RVA)(record);
    dialog_object = dialog;
    return dialog != NULL;
}

/* In-game progress text. The companion sets the text of FM Face's portrait
   popup (skin IDs fmnP/fmnS, drawn over the portrait), or of the FM Face
   dialog that is open, by applying a {text} property record to the widget,
   as FM24 does when it creates a dialog. Widgets are found afresh under the
   screen's root widget (manager + 0x58) every time, so a widget destroyed
   by navigation or a skin reload is never touched. */
enum { PROGRESS_NONE, PROGRESS_REQUESTED, PROGRESS_DONE, PROGRESS_FAILED };
static volatile LONG progress_state;
static int progress_target;  /* 1 portrait popup, 2 open dialog, 3 open dialog becomes an OK message */
static char progress_text[1024];
#define SCREEN_ROOT 0x58
#define MAX_VISIT   16384
typedef void (*apply_function)(void *, void *);

/* UI thread only. The widget under the screen root with skin ID a or b, or
   the given object; NULL unless exactly one matches. */
static unsigned char *find_widget_under(void *manager, unsigned char *root, unsigned int a, unsigned int b,
                                        const void *object)
{
    unsigned char *found = NULL;
    unsigned int skin_id;
    int top = 0, visited = 0, matches = 0;
    if (!root || !widget_id(root, manager, &skin_id)) return NULL;
    search_stack[top++] = root;
    while (top) {
        unsigned char *node = search_stack[--top];
        if (++visited > MAX_VISIT || !widget_id(node, manager, &skin_id)) continue;
        if (object ? node == object : (skin_id == a || (b && skin_id == b))) {
            found = node;
            ++matches;
        }
        int count = widget_children(node, search_children, MAX_CHILDREN);
        for (int i = 0; i < count; ++i) {
            if (top == MAX_SEARCH) return NULL;
            search_stack[top++] = search_children[i];
        }
    }
    return matches == 1 ? found : NULL;
}

static unsigned char *find_widget(void *manager, unsigned int a, unsigned int b, const void *object)
{
    unsigned char *root;
    return read_pointer((unsigned char *)manager + SCREEN_ROOT, &root) ?
        find_widget_under(manager, root, a, b, object) : NULL;
}

/* An open FM Face message or progress dialog: hide No and relabel Yes.
   FALSE until both buttons exist (FM24 may build them after creation). */
static BOOL make_ok_dialog(void *manager, const char *label)
{
    if (dialog_state != DIALOG_SHOWN || !dialog_object) return FALSE;
    unsigned char *dialog = find_widget(manager, 0, 0, dialog_object);
    unsigned char *no = dialog ? find_widget_under(manager, dialog, SKIN_ID('n','o',' ',' '), 0, NULL) : NULL;
    unsigned char *yes = dialog ? find_widget_under(manager, dialog, SKIN_ID('y','e','s',' '), 0, NULL) : NULL;
    if (!no || !yes) return FALSE;
    unsigned char record[256] __attribute__((aligned(16))) = {0};
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(value_function, DIALOG_RECORD_INIT_RVA)(record);
    GAME_FUNCTION(bool_function, RELOAD_VALUE_BOOL_RVA)(value, 1);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_RVA)(record, SKIN_ID('h','i','d','n'), value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(value);
    GAME_FUNCTION(apply_function, DIALOG_APPLY_PROPERTIES_RVA)(no + WIDGET_PART, record);
    GAME_FUNCTION(value_function, RELOAD_EVENT_FREE_RVA)(record);
    GAME_FUNCTION(value_function, DIALOG_RECORD_INIT_RVA)(record);
    set_text(record, SKIN_ID('t','e','x','t'), label);
    GAME_FUNCTION(apply_function, DIALOG_APPLY_PROPERTIES_RVA)(yes + WIDGET_PART, record);
    GAME_FUNCTION(value_function, RELOAD_EVENT_FREE_RVA)(record);
    return TRUE;
}

static int ok_dialog_tries;
static BOOL make_ok_dialog(void *manager, const char *label);

static BOOL apply_progress(void *manager)
{
    if (progress_target == 3) {
        if (!make_ok_dialog(manager, "OK")) return FALSE;
        /* Now an OK message: a pending relabel must not bring back "Stop after this face". */
        dialog_kind = 2;
        ok_dialog_tries = 0;
    }
    unsigned char *widget = progress_target == 1 ?
        find_widget(manager, SKIN_ID('f','m','n','P'), SKIN_ID('f','m','n','S'), NULL) :
        dialog_state == DIALOG_SHOWN && dialog_object ? find_widget(manager, 0, 0, dialog_object) : NULL;
    if (!widget) return FALSE;
    unsigned char record[256] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(value_function, DIALOG_RECORD_INIT_RVA)(record);
    set_text(record, SKIN_ID('t','e','x','t'), progress_text);
    GAME_FUNCTION(apply_function, DIALOG_APPLY_PROPERTIES_RVA)(widget + WIDGET_PART, record);
    GAME_FUNCTION(value_function, RELOAD_EVENT_FREE_RVA)(record);
    return TRUE;
}

/* Called on the UI thread for every manager event. Returns TRUE when the
   event was an FM Face dialog answer and has been consumed. */
static BOOL dialog_tick(void *manager, unsigned int id)
{
    LONG state = dialog_state;
    if (id == DIALOG_YES || id == DIALOG_NO) {
        if (state == DIALOG_SHOWN) {
            InterlockedExchange(&dialog_state, id == DIALOG_YES ? DIALOG_ANSWER_YES : DIALOG_ANSWER_NO);
            log_line(id == DIALOG_YES ? "FMFACE24_DIALOG_YES" : "FMFACE24_DIALOG_NO");
        }
        return TRUE;
    }
    if (progress_state == PROGRESS_REQUESTED && id == IDLE_EVENT && reload_state != RELOAD_RUNNING) {
        BOOL applied = apply_progress(manager);
        InterlockedExchange(&progress_state, applied ? PROGRESS_DONE : PROGRESS_FAILED);
        if (!applied && progress_target != 1 && dialog_state == DIALOG_SHOWN)
            InterlockedExchange(&dialog_state, DIALOG_CLOSED);
        log_line(applied ? "FMFACE24_PROGRESS_SET" : "FMFACE24_PROGRESS_NOT_SET");
    }
    if (state == DIALOG_SHOWN && dialog_kind != 1 && ok_dialog_tries > 0 && id == IDLE_EVENT) {
        if (make_ok_dialog(manager, dialog_kind == 3 ? "Stop after this face" : "OK")) {
            ok_dialog_tries = 0;
            log_line("FMFACE24_DIALOG_OK_BUTTON");
        } else if (!--ok_dialog_tries) {
            log_line("FMFACE24_DIALOG_OK_BUTTON_NOT_FOUND");
        }
    }
    if (state == DIALOG_REQUESTED && id == IDLE_EVENT && reload_state != RELOAD_RUNNING &&
        reload_state != RELOAD_REQUESTED) {
        InterlockedExchange(&dialog_state, DIALOG_SHOWN);
        dialog_shown_at = GetTickCount64();
        if (show_dialog(manager)) {
            ok_dialog_tries = 20;
            log_line("FMFACE24_DIALOG_SHOWN");
        } else {
            InterlockedExchange(&dialog_state, DIALOG_FAILED);
            log_line("FMFACE24_DIALOG_FAILED");
        }
    }
    return FALSE;
}

static void queue_click(unsigned int id, unsigned int item, LONG64 click_ms, LONG person, LONG read_ms)
{
    AcquireSRWLockExclusive(&pending_lock);
    pending.event = id;
    pending.item = item;
    pending.click_ms = click_ms;
    pending.person = person;
    pending.read_ms = read_ms;
    ReleaseSRWLockExclusive(&pending_lock);
    SetEvent(event_ready);
}

/* Performance build (build.py --perf): time FM Face's own work on FM24's UI
   thread, excluding the game's handler, and log totals once a minute. */
#ifdef FMFACE_PERF
static LARGE_INTEGER perf_frequency;
static LONG64 perf_events, perf_game, perf_own, perf_own_max, perf_menu;
static ULONGLONG perf_reported;
static LONG64 perf_now(void)
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    return now.QuadPart;
}
static LONG64 perf_us(LONG64 ticks) { return ticks * 1000000 / perf_frequency.QuadPart; }
static int perf_original(void *object, unsigned int id, void *event)
{
    LONG64 start = perf_now();
    int result = original_event(object, id, event);
    perf_game += perf_now() - start;
    return result;
}
static void perf_report(void)
{
    ULONGLONG now = GetTickCount64();
    if (!perf_reported) perf_reported = now;
    if (now - perf_reported < 60000) return;
    char line[160];
    snprintf(line, sizeof(line), "FMFACE24_PERF events=%lld own_us=%lld own_max_us=%lld game_us=%lld menu_us=%lld",
             (long long)perf_events, (long long)perf_us(perf_own), (long long)perf_us(perf_own_max),
             (long long)perf_us(perf_game), (long long)perf_us(perf_menu));
    log_line(line);
    perf_events = perf_own = perf_own_max = perf_game = perf_menu = 0;
    perf_reported = now;
}
#define ORIGINAL_EVENT perf_original
#else
#define ORIGINAL_EVENT original_event
#endif

static void popup_opened(void *manager, void *event);

static int handle_event(void *object, unsigned int id, void *event)
{
    if (id != PROFILE_EVENT && id != SQUAD_EVENT && id != UPDATE_ITEM) {
        if ((id == DIALOG_YES || id == DIALOG_NO) && dialog_tick(object, id)) return 1;
        if (id == MENU_OPEN) popup_opened(object, event);
        int result = ORIGINAL_EVENT(object, id, event);
        if (id == IDLE_EVENT || id == SKIN_EVENT || id == STAGE_EVENT) reload_tick(object, id);
        if (id == IDLE_EVENT) dialog_tick(object, id);
        return result;
    }
    /* No blocking I/O or generation on the UI thread; only guarded memory
       reads. The command carries the person shown in the clicked screen's
       header and the click time. The companion refuses it unless the open
       profile is still that person, and refuses a command that arrives late. */
    LONG64 click_ms = unix_ms();
    ULONGLONG started = GetTickCount64();
    BOOL profile = id == PROFILE_EVENT || id == UPDATE_ITEM;
    LONG person = profile ? clicked_person_id(object, event) : 0;
    unsigned int item = profile ? selected_item(event) : 0;
    queue_click(profile ? PROFILE_EVENT : id, item, click_ms, person, (LONG)(GetTickCount64() - started));
    return 1;
}

static int native_event(void *object, unsigned int id, void *event)
{
#ifdef FMFACE_PERF
    LONG64 game_before = perf_game, start = perf_now();
    int result = handle_event(object, id, event);
    LONG64 own = perf_now() - start - (perf_game - game_before);
    ++perf_events;
    perf_own += own;
    if (own > perf_own_max) perf_own_max = own;
    perf_report();
    return result;
#else
    return handle_event(object, id, event);
#endif
}

/* Squad screen "Players" menu. FM24 builds it from provider objects; the
   provider that adds "Export Team for Versus Competition" (its last item) is
   extended to append "Generate missing squad faces…", and its command
   handler receives that item's FMFS command when it is chosen. */
typedef void (*menu_add_function)(void *, void **, void *);
typedef char (*menu_event_function)(void *, unsigned int, void *, void *);
typedef char (*record_check_function)(void *);
typedef void *(*item_new_function)(void *, void **, void *, unsigned int, unsigned int, unsigned int);
typedef void (*release_function)(void *, int);
static menu_add_function original_menu_add;
static menu_event_function original_menu_event;

static void add_squad_item(void *provider, void **builder, void *context);

static void squad_menu_add(void *provider, void **builder, void *context)
{
    original_menu_add(provider, builder, context);
#ifdef FMFACE_PERF
    LONG64 start = perf_now();
    add_squad_item(provider, builder, context);
    perf_menu += perf_now() - start;
#else
    add_squad_item(provider, builder, context);
#endif
}

static void add_squad_item(void *provider, void **builder, void *context)
{
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
    queue_click(SQUAD_EVENT, 0, unix_ms(), 0, 0);
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

/* Profile menus. A popup button keeps its rows as menu items: a root item
   (popup + 0x4f8, whose 'id  ' is the popup's skin ID) holds them as a
   vector of item pointers at +0x38, and each item keeps its properties, like
   an event, at +8. When a menu opens, its popup posts meop to its parent
   container, before FM24 draws the rows. The DLL then adds Generate new face
   and Update face to a person profile's Overview tab menu (once per tab
   widget), and greys out Update face, in that menu and in the portrait
   popup, when the companion says the person shown has no face: FM24 greys a
   row whose 'disa' property is true and ignores clicks on it. Without an
   answer within 30 ms the row stays enabled, and the companion refuses an
   update without a face as before. */
#define POPUP_ROOT_ITEM 0x4f8
#define ITEM_PROPERTIES 0x08
#define ITEM_CHILDREN   0x38
#define MAX_POPUP_ROWS  16
#define FACE_TIMEOUT_MS 30

/* The fourcc code of an item's property; 0 if absent or unreadable. */
static unsigned int item_fourcc(const unsigned char *item, unsigned int key)
{
    unsigned char *range[2];
    struct { unsigned long long key; unsigned char *value; } properties[MAX_PROPERTIES];
    struct { void *type; unsigned int code, unused; } value;
    if (!read_game(item + ITEM_PROPERTIES, range, sizeof(range)) || range[1] <= range[0] ||
        (size_t)(range[1] - range[0]) > sizeof(properties) || (range[1] - range[0]) % sizeof(properties[0]) ||
        !read_game(range[0], properties, (SIZE_T)(range[1] - range[0])))
        return 0;
    int count = (int)((range[1] - range[0]) / sizeof(properties[0]));
    for (int i = 0; i < count; ++i)
        if ((unsigned int)properties[i].key == key)
            return read_game(properties[i].value, &value, sizeof(value)) ? value.code : 0;
    return 0;
}

static SOCKET face_socket = INVALID_SOCKET;
static BOOL face_network;
static LONG face_serial;
/* Set after a check goes unanswered: Wine reports no error for a closed
   loopback port, so without a companion every menu would wait the full
   timeout. While quiet the DLL still sends each check but doesn't wait; any
   reply, even a late one to an earlier check, shows the companion is
   answering and ends it. */
static BOOL face_quiet;

/* Unlike the click bridge's, this socket is not connected: under Wine a
   connected UDP socket that once met a closed port (no companion yet) stops
   waking select for every other reply, so later checks timed out. Replies
   are accepted only from the bridge port on loopback. */
static struct sockaddr_in face_destination(void)
{
    struct sockaddr_in destination;
    memset(&destination, 0, sizeof(destination));
    destination.sin_family = AF_INET;
    destination.sin_port = htons(bridge_port);
    destination.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    return destination;
}

/* UI thread only. 1 if the companion says the person has a face, 0 if not,
   -1 without an answer. "FMFACE24F 1 <serial> <person> <token>" goes to the
   bridge port; the reply is "FMFACE24F <serial> <yes|no>". */
static int query_face(LONG person)
{
    if (!face_network) {
        WSADATA data;
        if (WSAStartup(MAKEWORD(2,2), &data)) return -1;
        face_network = TRUE;
    }
    if (face_socket == INVALID_SOCKET && (face_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)) == INVALID_SOCKET)
        return -1;
    struct sockaddr_in destination = face_destination(), source;
    LONG serial = ++face_serial;
    char packet[256], yes[48], no[48];
    int count = snprintf(packet, sizeof(packet), "FMFACE24F 1 %ld %ld %s", (long)serial, (long)person, bridge_token);
    snprintf(yes, sizeof(yes), "FMFACE24F %ld yes", (long)serial);
    snprintf(no, sizeof(no), "FMFACE24F %ld no", (long)serial);
    if (sendto(face_socket, packet, count, 0, (struct sockaddr *)&destination, sizeof(destination)) != count)
        goto failed;
    ULONGLONG deadline = GetTickCount64() + (face_quiet ? 0 : FACE_TIMEOUT_MS);
    for (;;) {
        ULONGLONG now = GetTickCount64();
        fd_set readable;
        FD_ZERO(&readable);
        FD_SET(face_socket, &readable);
        struct timeval wait = {0, now < deadline ? (long)(deadline - now) * 1000 : 0};
        int ready = select(0, &readable, NULL, NULL, &wait);
        if (ready == SOCKET_ERROR) goto failed;
        if (!ready) { face_quiet = TRUE; return -1; }
        char reply[64];
        int length = sizeof(source);
        int received = recvfrom(face_socket, reply, sizeof(reply) - 1, 0, (struct sockaddr *)&source, &length);
        if (received <= 0) {
            if (WSAGetLastError() == WSAECONNRESET) { face_quiet = TRUE; return -1; }  /* No companion listening. */
            goto failed;
        }
        reply[received] = 0;
        if (length != sizeof(source) || source.sin_port != destination.sin_port ||
            source.sin_addr.s_addr != destination.sin_addr.s_addr || strncmp(reply, "FMFACE24F ", 10))
            continue;
        if (face_quiet) {  /* The companion is answering again: wait for this check. */
            face_quiet = FALSE;
            deadline = GetTickCount64() + FACE_TIMEOUT_MS;
        }
        if (!strcmp(reply, yes)) return 1;
        if (!strcmp(reply, no)) return 0;
    }
failed:
    closesocket(face_socket);
    face_socket = INVALID_SOCKET;
    return -1;
}

/* The rows of a popup's menu; -1 unless the popup's root item names it. */
static int popup_rows(unsigned char *popup, unsigned int popup_id, unsigned char **root, unsigned char **rows)
{
    unsigned char *range[2];
    if (!read_pointer(popup + POPUP_ROOT_ITEM, root) || item_fourcc(*root, SKIN_ID('i','d',' ',' ')) != popup_id ||
        !read_game(*root + ITEM_CHILDREN, range, sizeof(range)) || range[1] < range[0] ||
        (size_t)(range[1] - range[0]) > MAX_POPUP_ROWS * sizeof(rows[0]) || (range[1] - range[0]) % sizeof(rows[0]) ||
        (range[1] > range[0] && !read_game(range[0], rows, (SIZE_T)(range[1] - range[0]))))
        return -1;
    return (int)((range[1] - range[0]) / sizeof(rows[0]));
}

/* A person profile's Overview tab menu (FM24's 'Pprf' tab button, a popup
   subclass) lists FM24's person actions (Set Nickname, Follow or Unfollow);
   FM Face appends its two rows to it. */
static BOOL person_overview(unsigned char **rows, int count)
{
    for (int i = 0; i < count; ++i) {
        unsigned int id = item_fourcc(rows[i], SKIN_ID('i','d',' ',' '));
        if (id == SKIN_ID('n','i','c','k') || id == SKIN_ID('S','U','B','S') || id == SKIN_ID('U','S','U','B'))
            return TRUE;
    }
    return FALSE;
}

static void set_fourcc(void *record, unsigned int key, unsigned int code)
{
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(fourcc_function, SQUAD_MENU_VALUE_FOURCC_RVA)(value, code);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_RVA)(record, key, value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE_RVA)(value);
}

/* A row whose event (evid) is its ID. FM24 sends a chosen row's properties
   as an event to the tab button, from where an event nobody handles reaches
   the screen's handler like the portrait popup's. */
static void add_menu_row(unsigned char *root, unsigned int id, const char *label)
{
    unsigned char text[16] __attribute__((aligned(16))) = {0};
    void *item = NULL;
    GAME_FUNCTION(string_init_function, SQUAD_MENU_STRING_INIT_RVA)(text, label, -1);
    GAME_FUNCTION(item_new_function, SQUAD_MENU_ITEM_NEW_RVA)(root, &item, text, id,
                                                             SKIN_ID('E','N','O','N'), SKIN_ID('N','U','L','L'));
    GAME_FUNCTION(value_function, SQUAD_MENU_STRING_FREE_RVA)(text);
    if (!item) return;
    set_fourcc((unsigned char *)item + ITEM_PROPERTIES, SKIN_ID('i','d',' ',' '), id);
    set_fourcc((unsigned char *)item + ITEM_PROPERTIES, SKIN_ID('e','v','i','d'), id);
    /* Drop this function's reference; the root item holds its own. */
    if (!InterlockedDecrement((volatile LONG *)((unsigned char *)item + 0x20)))
        (*(release_function *)*(void **)item)(item, 1);
}

/* A team's tab menu (FM24's tab-button class) whose rows include its Squad
   view (TeSq): a reserve or youth team's tab in the Development Center, or
   another club's Players tab. FM Face appends "Generate missing squad
   faces…" (FMFS) unless the menu has it already, as the first team's Squad
   menu does through its provider. Like the provider's, the command works on
   the Squad table shown; the companion refuses without one. */
static void add_squad_rows(unsigned char **children, int count)
{
    unsigned char *root, *rows[MAX_POPUP_ROWS];
    for (int i = 0; i < count; ++i) {
        void *vtable;
        unsigned int skin_id;
        if (!read_game(children[i], &vtable, sizeof(vtable)) || vtable != game_base + PROFILE_MENU_TAB_BUTTON_VTABLE_RVA ||
            !read_game(children[i] + WIDGET_ID, &skin_id, sizeof(skin_id)))
            continue;
        int rows_count = popup_rows(children[i], skin_id, &root, rows);
        BOOL squad = FALSE, present = FALSE;
        for (int j = 0; j < rows_count; ++j) {
            unsigned int id = item_fourcc(rows[j], SKIN_ID('i','d',' ',' '));
            squad |= id == SKIN_ID('T','e','S','q');
            present |= id == SQUAD_EVENT;
        }
        if (squad && !present && rows_count < MAX_POPUP_ROWS) {
            add_menu_row(root, SQUAD_EVENT, "Generate missing squad faces\xe2\x80\xa6");
            log_line("FMFACE24_TEAM_MENU_ADDED");
        }
    }
}

/* UI thread only, for every meop event: the source is the opened popup's
   parent container. */
static void popup_opened(void *manager, void *event)
{
    unsigned char *parent, *popup = NULL, *root = NULL, *rows[MAX_POPUP_ROWS], *update = NULL;
    unsigned int skin_id, popup_id = 0;
    int count = 0;
    if (!event || !read_pointer((unsigned char *)event + EVENT_SOURCE, &parent)) return;
    int children = widget_children(parent, search_children, MAX_CHILDREN);
    add_squad_rows(search_children, children);
    for (int i = 0; i < children; ++i) {
        unsigned char *child = search_children[i];
        void *vtable;
        if (!widget_id(child, manager, &skin_id)) continue;
        if (skin_id == SKIN_ID('P','p','r','f') &&
            (!read_game(child, &vtable, sizeof(vtable)) || vtable != game_base + PROFILE_MENU_TAB_BUTTON_VTABLE_RVA))
            continue;
        if (skin_id != SKIN_ID('f','m','n','P') && skin_id != SKIN_ID('f','m','n','S') && skin_id != SKIN_ID('P','p','r','f'))
            continue;
        if (popup) return;
        popup = child;
        popup_id = skin_id;
    }
    if (!popup || (count = popup_rows(popup, popup_id, &root, rows)) < 0) return;
    if (popup_id == SKIN_ID('P','p','r','f')) {
        if (!person_overview(rows, count)) return;
        BOOL present = FALSE;
        for (int i = 0; i < count; ++i)
            if (item_fourcc(rows[i], SKIN_ID('i','d',' ',' ')) == PROFILE_EVENT) present = TRUE;
        if (!present) {
            add_menu_row(root, PROFILE_EVENT, "Generate new face");
            add_menu_row(root, UPDATE_ITEM, "Update face");
            if ((count = popup_rows(popup, popup_id, &root, rows)) < 0) return;
        }
    }
    for (int i = 0; i < count; ++i)
        if (item_fourcc(rows[i], SKIN_ID('i','d',' ',' ')) == UPDATE_ITEM) update = rows[i];
    if (!update) return;
    LARGE_INTEGER f, t0, t1, t2;
    QueryPerformanceFrequency(&f); QueryPerformanceCounter(&t0);
    LONG person = popup_person_id(manager, popup);
    QueryPerformanceCounter(&t1);
    int face = person ? query_face(person) : -1;
    QueryPerformanceCounter(&t2);
    set_flag(update + ITEM_PROPERTIES, SKIN_ID('d','i','s','a'), face == 0);
    char line[160];
    snprintf(line, sizeof(line), "FMFACE24_%s person=%ld face=%s id_us=%lld query_us=%lld",
             popup_id == SKIN_ID('P','p','r','f') ? "OVERVIEW_MENU" : "POPUP", (long)person,
             face == 1 ? "yes" : face == 0 ? "no" : "unknown",
             (t1.QuadPart - t0.QuadPart) * 1000000 / f.QuadPart, (t2.QuadPart - t1.QuadPart) * 1000000 / f.QuadPart);
    log_line(line);
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
        unsigned int item = pending.item;
        LONG64 click_ms = pending.click_ms;
        LONG person = pending.person;
        LONG read_ms = pending.read_ms;
        pending.event = 0;
        ReleaseSRWLockExclusive(&pending_lock);
        if (!id) continue;
        if (id == PROFILE_EVENT) {
            char line[96];
            snprintf(line, sizeof(line), "FMFACE24_PROFILE_CLICK person=%ld read_ms=%ld item=%s", (long)person,
                     (long)read_ms, item == UPDATE_ITEM ? "update" : item == PROFILE_EVENT ? "generate" : "unknown");
            log_line(line);
            if (!item) {
                show_message(L"FM Face could not tell which menu item was chosen, so nothing was generated. "
                             L"Choose it again.");
                continue;
            }
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
        int count = snprintf(packet, sizeof(packet), "FMFACE24 5 %llu %ld %s %lld %ld %s",
                             (unsigned long long)bridge_instance, (long)serial,
                             id == SQUAD_EVENT ? "squad" : item == UPDATE_ITEM ? "update" : "profile",
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
        show_message(L"FM Face's companion isn't running. FM24 starts it; restart FM24 to try again. "
                     L"If you moved the FM Face folder, run its setup again (fmface-fm24.exe on Windows, "
                     L"install-linux.sh on Linux). fmface-fm24.log beside it says what went wrong.");
    }
    if (socket_fd != INVALID_SOCKET) closesocket(socket_fd);
    if (network) WSACleanup();
    return 0;
}

/* Standard base64 into out (NUL-terminated); FALSE if invalid or too long. */
static BOOL base64_text(const char *in, char *out, size_t size)
{
    size_t length = strlen(in), written = 0;
    unsigned int bits = 0;
    int count = 0;
    if (length % 4) return FALSE;
    for (size_t i = 0; i < length; ++i) {
        char c = in[i];
        int v = c >= 'A' && c <= 'Z' ? c - 'A' : c >= 'a' && c <= 'z' ? c - 'a' + 26 :
                c >= '0' && c <= '9' ? c - '0' + 52 : c == '+' ? 62 : c == '/' ? 63 : c == '=' ? -2 : -1;
        if (v == -1 || (v == -2 && i + 2 < length)) return FALSE;
        if (v == -2) break;
        bits = (bits << 6) | (unsigned int)v;
        if ((count += 6) >= 8) {
            count -= 8;
            if (written + 1 >= size) return FALSE;
            out[written++] = (char)((bits >> count) & 0xff);
        }
    }
    out[written] = 0;
    return written == strlen(out);  /* No embedded NULs. */
}

/* Loopback requests on bridge port + 1, all carrying the private token:
     "FMFACE24R 1 <serial> <token>": reload the current skin in game. The reply
     "FMFACE24R <serial> <status>" is queued, busy, done or failed.
     "FMFACE24D 1 <serial> <token> <confirm|message|progress> <title> <text>":
     show an FM24 dialog (title and text base64 UTF-8). The reply "FMFACE24D
     <serial> <status>" is queued, busy, shown, yes, no, closed or failed.
     "FMFACE24P 1 <serial> <token> <portrait|dialog|finish> <text|->": set
     progress text (base64 UTF-8; "-" clears it); "finish" also turns the open
     progress dialog into an OK message. The reply is queued, busy, done or
     failed.
   The companion repeats the same serial to poll and never starts a second
   reload or dialog that way. */
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
    long long last = 0, last_dialog = 0, last_progress = 0;
    static char packet[4096], title_b64[400], text_b64[1500], title[256], text[1024];
    for (;;) {
        char token[160], kind[16], extra;
        long long serial;
        struct sockaddr_in peer;
        int size = sizeof(peer);
        int received = recvfrom(socket_fd, packet, sizeof(packet) - 1, 0, (struct sockaddr *)&peer, &size);
        if (received <= 0 || peer.sin_addr.s_addr != htonl(INADDR_LOOPBACK)) continue;
        packet[received] = 0;
        const char *status;
        char reply[64];
        int count;
        if (sscanf(packet, "FMFACE24D 1 %lld %159s %15s %399s %1499s %c", &serial, token, kind,
                   title_b64, text_b64, &extra) == 5) {
            if (serial <= 0 || strcmp(token, bridge_token) || (strcmp(kind, "confirm") && strcmp(kind, "message") && strcmp(kind, "progress")) ||
                !base64_text(title_b64, title, sizeof(title)) || !base64_text(text_b64, text, sizeof(text)))
                continue;
            LONG state = dialog_state;
            if (serial > last_dialog) {
                if (!queue_dialog(!strcmp(kind, "confirm") ? 1 : !strcmp(kind, "progress") ? 3 : 2, title, text)) {
                    status = "busy";
                } else {
                    last_dialog = serial;
                    log_line("FMFACE24_DIALOG_REQUESTED");
                    status = "queued";
                }
            } else if (serial == last_dialog) {
                status = state == DIALOG_SHOWN ? "shown" : state == DIALOG_ANSWER_YES ? "yes" :
                         state == DIALOG_ANSWER_NO ? "no" : state == DIALOG_FAILED ? "failed" :
                         state == DIALOG_CLOSED ? "closed" : "queued";
            } else {
                continue;
            }
            count = snprintf(reply, sizeof(reply), "FMFACE24D %lld %s", serial, status);
            sendto(socket_fd, reply, count, 0, (struct sockaddr *)&peer, sizeof(peer));
            continue;
        }
        if (sscanf(packet, "FMFACE24P 1 %lld %159s %15s %1499s %c", &serial, token, kind, text_b64, &extra) == 4) {
            if (serial <= 0 || strcmp(token, bridge_token) || (strcmp(kind, "portrait") && strcmp(kind, "dialog") && strcmp(kind, "finish")) ||
                (strcmp(text_b64, "-") && !base64_text(text_b64, text, sizeof(text))))
                continue;
            LONG state = progress_state;
            if (serial > last_progress) {
                if (state == PROGRESS_REQUESTED) {
                    status = "busy";
                } else {
                    last_progress = serial;
                    progress_target = !strcmp(kind, "portrait") ? 1 : !strcmp(kind, "dialog") ? 2 : 3;
                    snprintf(progress_text, sizeof(progress_text), "%s", strcmp(text_b64, "-") ? text : "");
                    InterlockedExchange(&progress_state, PROGRESS_REQUESTED);
                    status = "queued";
                }
            } else if (serial == last_progress) {
                status = state == PROGRESS_DONE ? "done" : state == PROGRESS_FAILED ? "failed" : "queued";
            } else {
                continue;
            }
            count = snprintf(reply, sizeof(reply), "FMFACE24P %lld %s", serial, status);
            sendto(socket_fd, reply, count, 0, (struct sockaddr *)&peer, sizeof(peer));
            continue;
        }
        if (sscanf(packet, "FMFACE24R 1 %lld %159s %c", &serial, token, &extra) != 2 ||
            serial <= 0 || strcmp(token, bridge_token))
            continue;
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
        count = snprintf(reply, sizeof(reply), "FMFACE24R %lld %s", serial, status);
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

/* Start FM Face's companion (fmface24.ini "companion": the full path of
   fmface-fm24.exe) once the hooks are in, so the player never starts it. It
   gets FM24's process ID, exits when FM24 does, and exits at once if a
   companion is already running. DETACHED_PROCESS: no window; it logs to
   fmface-fm24.log beside itself and opens a window only to ask for a key.
   Under Proton the path is a Wine path (Z:\...). */
static DWORD WINAPI start_companion(LPVOID unused)
{
    (void)unused;
    WCHAR config[MAX_PATH], path[MAX_PATH], folder[MAX_PATH], game[MAX_PATH], command[3 * MAX_PATH + 64] = {0};
    if (!sibling_path(config, L"fmface24.ini") || !sibling_path(game, L"")) return 0;
    DWORD length = GetPrivateProfileStringW(L"fmface", L"companion", L"", path, MAX_PATH, config);
    if (!length) { log_line("FMFACE24_COMPANION_NOT_CONFIGURED"); return 0; }
    DWORD attributes = GetFileAttributesW(path);
    if (length >= MAX_PATH - 1 || attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        log_line("FMFACE24_COMPANION_MISSING"); return 0;
    }
    wcscpy(folder, path);
    WCHAR *slash = wcsrchr(folder, L'\\');
    if (!slash) { log_line("FMFACE24_COMPANION_MISSING"); return 0; }
    *slash = 0;
    game[wcslen(game) - 1] = 0;  /* Without the trailing backslash, which would escape the quote. */
    _snwprintf(command, sizeof(command) / sizeof(command[0]) - 1, L"\"%ls\" --started-by-fm24 %lu --game-dir \"%ls\"",
             path, (unsigned long)GetCurrentProcessId(), game);
    STARTUPINFOW startup;
    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process;
    if (!CreateProcessW(path, command, NULL, NULL, FALSE, DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP,
                        NULL, folder, &startup, &process)) {
        char line[64];
        snprintf(line, sizeof(line), "FMFACE24_COMPANION_START_FAILED error=%lu", GetLastError());
        log_line(line);
        return 0;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    log_line("FMFACE24_COMPANION_STARTED");
    return 0;
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
    bridge_instance = unix_ms();
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
#ifdef FMFACE_PERF
    QueryPerformanceFrequency(&perf_frequency);
#endif
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
    if ((thread = CreateThread(NULL, 0, start_companion, NULL, 0, NULL)))
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

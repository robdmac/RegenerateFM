/* MIT. Experimental FM24 24.4.2 native UI event bridge for macOS (Apple Silicon).
   The macOS counterpart of fmface24.c: the same events, bridge protocol and
   dialogs, at the addresses of the Mac build (supported_build_macos.json).
   FM24's run_fmface24.sh loads it with DYLD_INSERT_LIBRARIES. */
#include <arpa/inet.h>
#include <dlfcn.h>
#include <libproc.h>
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <mach-o/dyld.h>
#include <mach-o/loader.h>
#include <netinet/in.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>
#include "supported_build_macos.h"

typedef int BOOL;
#define TRUE 1
#define FALSE 0

#define PROFILE_EVENT 0x464d4647u /* FMFG */
#define SQUAD_EVENT   0x464d4653u /* FMFS */
#define UPDATE_ITEM   0x464d4655u /* FMFU: the portrait popup's "Update face" row */
#define IDLE_EVENT    0x49444c45u /* IDLE */
#define SKIN_EVENT    0x47436853u /* GChS: change skin */
#define STAGE_EVENT   0x6368736bu /* chsk: one queued stage of a skin change */
#define DIALOG_YES    0x464d4459u /* FMDY: FM Face dialog answered Yes or OK */
#define DIALOG_NO     0x464d444eu /* FMDN: FM Face dialog answered No */
#define MENU_OPEN     0x6d656f70u /* meop: a popup menu opened; posted to the popup button's parent */

typedef int (*event_handler)(void *, unsigned int, void *);
static event_handler original_event;
static pthread_mutex_t pending_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t pending_ready = PTHREAD_COND_INITIALIZER;
static int sequence;
/* One pending click, written by the UI thread and taken by the worker. */
static struct { unsigned int event, item; long long click_ms; long long person; long read_ms; } pending;
static unsigned short bridge_port;
/* Names this game run in click packets (the start time in milliseconds). */
static long long bridge_instance;
static char bridge_token[129];
static char log_path[1024];
static unsigned char *game_base;

static unsigned long long tick_ms(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (unsigned long long)now.tv_sec * 1000 + (unsigned long long)now.tv_nsec / 1000000;
}

/* Wall-clock milliseconds since the Unix epoch, comparable with the companion's clock. */
static long long unix_ms(void)
{
    struct timeval now;
    gettimeofday(&now, NULL);
    return (long long)now.tv_sec * 1000 + now.tv_usec / 1000;
}

static void log_line(const char *text)
{
    char stamp[32];
    time_t now = time(NULL);
    struct tm local;
    localtime_r(&now, &local);
    strftime(stamp, sizeof(stamp), "%Y-%m-%d %H:%M:%S", &local);
    FILE *file = log_path[0] ? fopen(log_path, "a") : NULL;
    if (!file) return;
    fprintf(file, "%s %s\n", stamp, text);
    fclose(file);
}

static BOOL queue_dialog(int kind, const char *title, const char *text);

/* Worker threads only. FM24 shows the message in its own dialog when the UI
   thread can take it; otherwise it only goes to fmface24.log. */
static void show_message(const char *text)
{
    if (!queue_dialog(2, "FM Face", text)) log_line(text);
}

/* Copy game memory without faulting on a stale or invalid pointer. */
static BOOL read_game(const void *address, void *buffer, size_t size)
{
    mach_vm_size_t copied = 0;
    return address && mach_vm_read_overwrite(mach_task_self(), (mach_vm_address_t)address, size,
                                             (mach_vm_address_t)buffer, &copied) == KERN_SUCCESS && copied == size;
}

/* Widget layout of the pinned FM24 build, checked live on the Mac build: the
   same offsets as fm24.exe. Container objects embed a widget part (+0x38)
   holding the UI manager, the parent object, and the skin ID; children are a
   vector of object pointers at +0x100. The header's subtitle ("Role - Club
   (ID:n)" with screen IDs enabled) is the text of the 'subs' widget. */
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

/* Reads the person ID shown in the open screen's header, starting from FM
   Face's portrait popup (the clicked menu's source widget). Runs on FM24's UI
   thread inside the menu callback, so the header cannot change to another
   screen while it is read. Every read is guarded, and any surprise returns 0,
   which the companion refuses. */
static long popup_person_id(void *manager, unsigned char *popup)
{
    unsigned char *object, *head = NULL, *subs = NULL;
    unsigned int skin_id;
    if (!widget_id(popup, manager, &skin_id) ||
        (skin_id != SKIN_ID('f','m','n','P') && skin_id != SKIN_ID('f','m','n','S') &&
         skin_id != SKIN_ID('P','p','r','f')))
        return 0;
    object = popup;
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
    return value <= 0x7fffffffu ? (long)value : 0;
}

/* Without "Show screen IDs in Title Bar" the header shows no ID. FM24 refers
   to the object a screen shows as type << 24 | index, 0x01000000 | the handle
   for a person; the companion turns the handle into the person (every person
   record carries it), so the library doesn't call into the game to resolve it.
   The sidebar menu ('side', SIDEBAR_MENU_TABLE) keeps that reference at
   +0x278 for every screen, also after a skin reload. The screen's
   CLIENT_OBJECT_BROWSER, an ancestor of every widget on it, holds it too until
   the first reload (FM Face reloads after each face), so it is only the
   fallback. */
static unsigned char *find_widget(void *manager, unsigned int a, unsigned int b, const void *object);

static unsigned int person_reference(unsigned int reference)
{
    unsigned int index = reference & 0x7fffff;
    return reference >> 24 == 1 && index && index != 0x7fffff ? reference : 0;
}

static unsigned int screen_reference(void *manager, unsigned char *widget)
{
    unsigned int skin_id, reference;
    void *vtable;
    unsigned char *sidebar = find_widget(manager, SKIN_ID('s','i','d','e'), 0, NULL);
    if (sidebar && read_game(sidebar, &vtable, sizeof(vtable)) && vtable == game_base + BROWSER_SIDEBAR_VTABLE &&
        read_game(sidebar + BROWSER_SIDEBAR_REFERENCE_OFFSET, &reference, sizeof(reference)) &&
        person_reference(reference))
        return reference;
    for (int depth = 0; depth < 64 && widget && widget_id(widget, manager, &skin_id); ++depth) {
        if (read_game(widget, &vtable, sizeof(vtable)) && vtable == game_base + BROWSER_VTABLE)
            return read_game(widget + BROWSER_REFERENCE_OFFSET, &reference, sizeof(reference)) ?
                   person_reference(reference) : 0;
        if (!read_pointer(widget + WIDGET_PARENT, &widget)) return 0;
    }
    return 0;
}

/* Who the popup's screen shows: the person ID from the header's "(ID:n)",
   else minus the screen's person reference, else 0 (refused). */
static long long popup_target(void *manager, unsigned char *popup)
{
    long person = popup_person_id(manager, popup);
    if (person) return person;
    unsigned int skin_id;
    if (!widget_id(popup, manager, &skin_id) ||
        (skin_id != SKIN_ID('f','m','n','P') && skin_id != SKIN_ID('f','m','n','S') &&
         skin_id != SKIN_ID('P','p','r','f')))
        return 0;
    return -(long long)screen_reference(manager, popup);
}

/* The bridge's person field: the ID, or "r<reference>" for the companion to resolve. */
static void format_target(char *out, size_t size, long long target)
{
    if (target < 0) snprintf(out, size, "r%llu", (unsigned long long)-target);
    else snprintf(out, size, "%lld", target);
}

static long long clicked_person_id(void *manager, void *event)
{
    unsigned char *source;
    if (!event || !read_pointer((unsigned char *)event + EVENT_SOURCE, &source)) return 0;
    return popup_target(manager, source);
}

/* The row that was chosen: FMFG (Generate new face) or FMFU (Update face).
   An event keeps its properties as a vector of {fourcc key, value pointer}
   at +0; a fourcc value holds its code at +8. The 'slct' value must have the
   same type as the event's own 'evid' value. Anything else returns 0. */
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
        !read_game(range[0], properties, (size_t)(range[1] - range[0])))
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

/* In-process skin reload, as in fmface24.c: on the next IDLE event the UI
   thread posts FM24's own "reload the current skin" event (GChS naming the
   current skin). FM24 runs it as queued chsk stages that count a byte of the
   manager (RELOAD_STAGE_OFFSET) down from 5 to -1; the reload is complete
   when a chsk stage leaves it at -1. */
enum { RELOAD_NONE, RELOAD_REQUESTED, RELOAD_RUNNING, RELOAD_DONE, RELOAD_FAILED };
static int reload_state;
static unsigned long long reload_posted;
static BOOL reload_started;

typedef void (*value_function)(void *);
typedef void (*pair_function)(void *, void *);
typedef void *(*top_function)(void *);
typedef void (*init_function)(void *, unsigned int, void *);
typedef void (*set_function)(void *, unsigned int, void *);
typedef void (*bool_function)(void *, char);
#define GAME_FUNCTION(type, offset) ((type)(game_base + (offset)))

static BOOL reload_stage(void *manager, signed char *stage)
{
    return read_game((unsigned char *)manager + RELOAD_STAGE_OFFSET, stage, sizeof(*stage));
}

static void set_flag(void *event, unsigned int key, char value)
{
    unsigned char flag[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(bool_function, RELOAD_VALUE_BOOL)(flag, value);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET)(event, key, flag);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE)(flag);
}

static void *top_manager(void)
{
    void *global;
    if (!read_game(game_base + RELOAD_MANAGER_GLOBAL, &global, sizeof(global)) || !global) return NULL;
    return GAME_FUNCTION(top_function, RELOAD_TOP_MANAGER)(global);
}

/* UI thread only. Mirrors the validated build's own reload command. */
static BOOL post_skin_reload(void *manager)
{
    signed char stage;
    if (top_manager() != manager || !reload_stage(manager, &stage) || (stage != -1 && stage != 5))
        return FALSE;
    unsigned char skin[64] __attribute__((aligned(16))) = {0};
    unsigned char event[256] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(value_function, RELOAD_VALUE_INIT)(skin);
    GAME_FUNCTION(pair_function, RELOAD_CURRENT_SKIN)(manager, skin);
    GAME_FUNCTION(init_function, RELOAD_EVENT_INIT)(event, SKIN_EVENT, NULL);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET_COPY)(event, SKIN_ID('S','k','N','M'), skin);
    set_flag(event, SKIN_ID('d','l','o','g'), 1);
    set_flag(event, SKIN_ID('d','e','l','a'), 1);
    set_flag(event, SKIN_ID('c','h','e','v'), 0);
    GAME_FUNCTION(pair_function, RELOAD_POST_EVENT)(manager, event);
    GAME_FUNCTION(value_function, RELOAD_EVENT_FREE)(event);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE)(skin);
    return TRUE;
}

enum { DIALOG_NONE, DIALOG_REQUESTED, DIALOG_SHOWN, DIALOG_ANSWER_YES, DIALOG_ANSWER_NO, DIALOG_FAILED,
       DIALOG_CLOSED };
static int dialog_state;

static void set_state(int *state, int value) { __atomic_store_n(state, value, __ATOMIC_SEQ_CST); }
static int get_state(int *state) { return __atomic_load_n(state, __ATOMIC_SEQ_CST); }

static void finish_reload(int state, const char *line)
{
    /* Reloading the skin destroys any open FM Face dialog (DIALOG_SHOWN). */
    int shown = DIALOG_SHOWN;
    __atomic_compare_exchange_n(&dialog_state, &shown, DIALOG_CLOSED, FALSE, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    set_state(&reload_state, state);
    log_line(line);
}

/* Called on the UI thread after FM24 has handled each manager event. */
static void reload_tick(void *manager, unsigned int id)
{
    int state = get_state(&reload_state);
    signed char stage;
    if (state == RELOAD_REQUESTED && id == IDLE_EVENT) {
        reload_started = FALSE;
        reload_posted = tick_ms();
        /* FM24 can handle the posted GChS before the post returns. */
        set_state(&reload_state, RELOAD_RUNNING);
        if (post_skin_reload(manager))
            log_line("FMFACE24_RELOAD_POSTED");
        else
            finish_reload(RELOAD_FAILED, "FMFACE24_RELOAD_NOT_POSTED");
    } else if (state == RELOAD_RUNNING) {
        if (id == SKIN_EVENT) reload_started = TRUE;
        if (id == STAGE_EVENT && reload_started && reload_stage(manager, &stage) && stage == -1)
            finish_reload(RELOAD_DONE, "FMFACE24_RELOAD_DONE");
        else if (tick_ms() - reload_posted > (reload_started ? 60000 : 10000))
            finish_reload(RELOAD_FAILED, "FMFACE24_RELOAD_TIMEOUT");
    }
}

/* FM24's own modal dialogs, as in fmface24.c. On the Mac build the game's
   "Quit game and return to start screen?" query is a property record (titl,
   text, acte = the event code posted for Yes, noet = the code posted for No,
   widt, heig) given to the dialog function with its owner and the UI
   manager. Kind 1 is a Yes/No question, kind 2 an OK message (No hidden, Yes
   relabelled OK), kind 3 squad progress (one "Stop after this face" button). */
static pthread_rwlock_t dialog_lock = PTHREAD_RWLOCK_INITIALIZER;
static int dialog_kind;
static char dialog_title[256], dialog_text[1024];
static unsigned long long dialog_shown_at;
static void *dialog_object;

typedef void *(*dialog_function)(void *, void *, void *);
typedef void (*string_value_function)(void *, void *);
typedef void (*float_value_function)(void *, float);
typedef void (*string_init_function)(void *, const char *, int);
typedef void (*fourcc_function)(void *, unsigned int);

static BOOL queue_dialog(int kind, const char *title, const char *text)
{
    if (!game_base) return FALSE;
    int state = get_state(&dialog_state);
    /* A dialog closed without an answer (for example by a skin reload) would
       otherwise block every later one; after ten minutes it is presumed gone. */
    if (state == DIALOG_REQUESTED ||
        (state == DIALOG_SHOWN && dialog_kind == 1 && tick_ms() - dialog_shown_at < 600000))
        return FALSE;
    pthread_rwlock_wrlock(&dialog_lock);
    dialog_kind = kind;
    snprintf(dialog_title, sizeof(dialog_title), "%s", title);
    snprintf(dialog_text, sizeof(dialog_text), "%s", text);
    pthread_rwlock_unlock(&dialog_lock);
    set_state(&dialog_state, DIALOG_REQUESTED);
    return TRUE;
}

/* Records (dialog properties and widget updates) have their own init, set
   and free functions on the Mac build; events and menu items use the event ones. */
static void set_text(void *record, unsigned int key, const char *text)
{
    unsigned char string[16] __attribute__((aligned(16))) = {0};
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(string_init_function, SQUAD_MENU_STRING_INIT)(string, text, -1);
    GAME_FUNCTION(string_value_function, DIALOG_VALUE_STRING)(value, string);
    GAME_FUNCTION(set_function, DIALOG_RECORD_SET)(record, key, value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE)(value);
    GAME_FUNCTION(value_function, SQUAD_MENU_STRING_FREE)(string);
}

static void set_record_value(void *record, unsigned int key, void *value)
{
    GAME_FUNCTION(set_function, DIALOG_RECORD_SET)(record, key, value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE)(value);
}

/* UI thread only. */
static BOOL show_dialog(void *manager)
{
    if (top_manager() != manager) return FALSE;
    unsigned char record[256] __attribute__((aligned(16))) = {0};
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(value_function, DIALOG_RECORD_INIT)(record);
    pthread_rwlock_rdlock(&dialog_lock);
    set_text(record, SKIN_ID('t','i','t','l'), dialog_title);
    set_text(record, SKIN_ID('t','e','x','t'), dialog_text);
    pthread_rwlock_unlock(&dialog_lock);
    GAME_FUNCTION(fourcc_function, SQUAD_MENU_VALUE_FOURCC)(value, DIALOG_YES);
    set_record_value(record, SKIN_ID('a','c','t','e'), value);
    GAME_FUNCTION(fourcc_function, SQUAD_MENU_VALUE_FOURCC)(value, DIALOG_NO);
    set_record_value(record, SKIN_ID('n','o','e','t'), value);
    GAME_FUNCTION(float_value_function, DIALOG_VALUE_FLOAT)(value, 520.0f);
    set_record_value(record, SKIN_ID('w','i','d','t'), value);
    GAME_FUNCTION(float_value_function, DIALOG_VALUE_FLOAT)(value, 250.0f);
    set_record_value(record, SKIN_ID('h','e','i','g'), value);
    void *dialog = GAME_FUNCTION(dialog_function, DIALOG_SHOW_QUERY)(manager, manager, record);
    GAME_FUNCTION(value_function, DIALOG_RECORD_FREE)(record);
    dialog_object = dialog;
    return dialog != NULL;
}

/* In-game progress text: a {text} property record applied to FM Face's
   portrait popup (fmnP/fmnS) or the open FM Face dialog. Widgets are found
   afresh under the screen's root widget (manager + 0x58) every time. */
enum { PROGRESS_NONE, PROGRESS_REQUESTED, PROGRESS_DONE, PROGRESS_FAILED };
static int progress_state;
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

static void apply_record(unsigned char *widget, void *record)
{
    GAME_FUNCTION(apply_function, DIALOG_APPLY_PROPERTIES)(widget + WIDGET_PART, record);
    GAME_FUNCTION(value_function, DIALOG_RECORD_FREE)(record);
}

/* An open FM Face message or progress dialog: hide No and relabel Yes.
   FALSE until both buttons exist (FM24 may build them after creation). */
static BOOL make_ok_dialog(void *manager, const char *label)
{
    if (get_state(&dialog_state) != DIALOG_SHOWN || !dialog_object) return FALSE;
    unsigned char *dialog = find_widget(manager, 0, 0, dialog_object);
    unsigned char *no = dialog ? find_widget_under(manager, dialog, SKIN_ID('n','o',' ',' '), 0, NULL) : NULL;
    unsigned char *yes = dialog ? find_widget_under(manager, dialog, SKIN_ID('y','e','s',' '), 0, NULL) : NULL;
    if (!no || !yes) return FALSE;
    unsigned char record[256] __attribute__((aligned(16))) = {0};
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(value_function, DIALOG_RECORD_INIT)(record);
    GAME_FUNCTION(bool_function, RELOAD_VALUE_BOOL)(value, 1);
    set_record_value(record, SKIN_ID('h','i','d','n'), value);
    apply_record(no, record);
    GAME_FUNCTION(value_function, DIALOG_RECORD_INIT)(record);
    set_text(record, SKIN_ID('t','e','x','t'), label);
    apply_record(yes, record);
    return TRUE;
}

static int ok_dialog_tries;

static BOOL apply_progress(void *manager)
{
    if (progress_target == 3 && !make_ok_dialog(manager, "OK")) return FALSE;
    unsigned char *widget = progress_target == 1 ?
        find_widget(manager, SKIN_ID('f','m','n','P'), SKIN_ID('f','m','n','S'), NULL) :
        get_state(&dialog_state) == DIALOG_SHOWN && dialog_object ? find_widget(manager, 0, 0, dialog_object) : NULL;
    if (!widget) return FALSE;
    unsigned char record[256] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(value_function, DIALOG_RECORD_INIT)(record);
    set_text(record, SKIN_ID('t','e','x','t'), progress_text);
    apply_record(widget, record);
    return TRUE;
}

/* Called on the UI thread for every manager event. Returns TRUE when the
   event was an FM Face dialog answer and has been consumed. */
static BOOL dialog_tick(void *manager, unsigned int id)
{
    int state = get_state(&dialog_state);
    if (id == DIALOG_YES || id == DIALOG_NO) {
        if (state == DIALOG_SHOWN) {
            set_state(&dialog_state, id == DIALOG_YES ? DIALOG_ANSWER_YES : DIALOG_ANSWER_NO);
            log_line(id == DIALOG_YES ? "FMFACE24_DIALOG_YES" : "FMFACE24_DIALOG_NO");
        }
        return TRUE;
    }
    int reload = get_state(&reload_state);
    if (get_state(&progress_state) == PROGRESS_REQUESTED && id == IDLE_EVENT && reload != RELOAD_RUNNING) {
        BOOL applied = apply_progress(manager);
        set_state(&progress_state, applied ? PROGRESS_DONE : PROGRESS_FAILED);
        if (!applied && progress_target != 1 && get_state(&dialog_state) == DIALOG_SHOWN)
            set_state(&dialog_state, DIALOG_CLOSED);
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
    if (state == DIALOG_REQUESTED && id == IDLE_EVENT && reload != RELOAD_RUNNING && reload != RELOAD_REQUESTED) {
        set_state(&dialog_state, DIALOG_SHOWN);
        dialog_shown_at = tick_ms();
        if (show_dialog(manager)) {
            ok_dialog_tries = 20;
            log_line("FMFACE24_DIALOG_SHOWN");
        } else {
            set_state(&dialog_state, DIALOG_FAILED);
            log_line("FMFACE24_DIALOG_FAILED");
        }
    }
    return FALSE;
}

static void queue_click(unsigned int id, unsigned int item, long long click_ms, long long person, long read_ms)
{
    pthread_mutex_lock(&pending_lock);
    pending.event = id;
    pending.item = item;
    pending.click_ms = click_ms;
    pending.person = person;
    pending.read_ms = read_ms;
    pthread_cond_signal(&pending_ready);
    pthread_mutex_unlock(&pending_lock);
}

static void popup_opened(void *manager, void *event);

static int native_event(void *object, unsigned int id, void *event)
{
    if (id != PROFILE_EVENT && id != SQUAD_EVENT && id != UPDATE_ITEM) {
        if ((id == DIALOG_YES || id == DIALOG_NO) && dialog_tick(object, id)) return 1;
        if (id == MENU_OPEN) popup_opened(object, event);
        int result = original_event(object, id, event);
        if (id == IDLE_EVENT || id == SKIN_EVENT || id == STAGE_EVENT) reload_tick(object, id);
        if (id == IDLE_EVENT) dialog_tick(object, id);
        return result;
    }
    /* No blocking I/O or generation on the UI thread; only guarded memory
       reads. The command carries the person shown in the clicked screen's
       header and the click time. */
    long long click_ms = unix_ms();
    unsigned long long started = tick_ms();
    BOOL profile = id == PROFILE_EVENT || id == UPDATE_ITEM;
    long long person = profile ? clicked_person_id(object, event) : 0;
    unsigned int item = profile ? selected_item(event) : 0;
    queue_click(profile ? PROFILE_EVENT : id, item, click_ms, person, (long)(tick_ms() - started));
    return 1;
}

/* Squad screen "Players" menu: the provider that adds "Export Team for
   Versus Competition" (TEAM_EXPORT_ACTION_UI) is extended to append
   "Generate missing squad faces…", and its command handler receives that
   item's FMFS command. On the Mac build the item constructor returns the new
   item through x8, which C does for a returned struct larger than 16 bytes;
   items are released through their deleting destructor, vtable slot 1. */
typedef void (*menu_add_function)(void *, void **, void *);
typedef char (*menu_event_function)(void *, unsigned int, void *, void *);
typedef char (*record_check_function)(void *);
typedef struct { void *item; void *unused[2]; } new_item;
typedef new_item (*item_new_function)(void *, void *, unsigned int, unsigned int, unsigned int);
typedef void (*release_function)(void *);
static menu_add_function original_menu_add;
static menu_event_function original_menu_event;

static void *make_item(void *builder, const char *label, unsigned int id)
{
    unsigned char text[16] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(string_init_function, SQUAD_MENU_STRING_INIT)(text, label, -1);
    new_item made = GAME_FUNCTION(item_new_function, SQUAD_MENU_ITEM_NEW)(builder, text, id, SKIN_ID('N','O','N','E'),
                                                                       SKIN_ID('N','U','L','L'));
    GAME_FUNCTION(value_function, SQUAD_MENU_STRING_FREE)(text);
    return made.item;
}

/* Drop this function's reference; the menu holds its own. */
static void release_item(void *item)
{
    if (__atomic_fetch_sub((int *)((unsigned char *)item + 0x20), 1, __ATOMIC_ACQ_REL) == 1)
        (*(release_function *)(*(unsigned char **)item + 8))(item);
}

static void set_fourcc(void *record, unsigned int key, unsigned int code)
{
    unsigned char value[64] __attribute__((aligned(16))) = {0};
    GAME_FUNCTION(fourcc_function, SQUAD_MENU_VALUE_FOURCC)(value, code);
    GAME_FUNCTION(set_function, RELOAD_EVENT_SET)(record, key, value);
    GAME_FUNCTION(value_function, RELOAD_VALUE_FREE)(value);
}

static void add_squad_item(void *provider, void **builder, void *context)
{
    if (!builder || !*builder || !GAME_FUNCTION(record_check_function, SQUAD_MENU_IS_RECORD)(context))
        return;
    void *item = make_item(*builder, "Generate missing squad faces\xe2\x80\xa6", SQUAD_EVENT);
    if (!item) return;
    set_fourcc((unsigned char *)item + 8, SKIN_ID('e','v','i','d'), SQUAD_EVENT);
    GAME_FUNCTION(pair_function, SQUAD_MENU_ITEM_APPEND)(provider, &item);
    release_item(item);
}

static void squad_menu_add(void *provider, void **builder, void *context)
{
    original_menu_add(provider, builder, context);
    add_squad_item(provider, builder, context);
}

static char squad_menu_event(void *provider, unsigned int id, void *event, void *extra)
{
    if (id != SQUAD_EVENT) return original_menu_event(provider, id, event, extra);
    queue_click(SQUAD_EVENT, 0, unix_ms(), 0, 0);
    return 1;
}

static struct sockaddr_in bridge_destination(void)
{
    struct sockaddr_in destination;
    memset(&destination, 0, sizeof(destination));
    destination.sin_family = AF_INET;
    destination.sin_port = htons(bridge_port);
    destination.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    return destination;
}

static int open_bridge(void)
{
    int socket_fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_fd < 0) return -1;
    struct sockaddr_in destination = bridge_destination();
    if (connect(socket_fd, (struct sockaddr *)&destination, sizeof(destination))) {
        close(socket_fd);
        return -1;
    }
    return socket_fd;
}

/* Profile menus, as in fmface24.c: a popup button's root item (popup +
   0x4f8, whose 'id  ' is the popup's skin ID) holds its rows as a vector of
   item pointers at +0x38, each with its properties at +8. */
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
        !read_game(range[0], properties, (size_t)(range[1] - range[0])))
        return 0;
    int count = (int)((range[1] - range[0]) / sizeof(properties[0]));
    for (int i = 0; i < count; ++i)
        if ((unsigned int)properties[i].key == key)
            return read_game(properties[i].value, &value, sizeof(value)) ? value.code : 0;
    return 0;
}

static int face_socket = -1;
static int face_serial;
/* Set after a check goes unanswered, so that without a companion every menu
   doesn't wait the full timeout; any reply ends it. */
static BOOL face_quiet;

/* UI thread only. 1 if the companion says the person has a face, 0 if not,
   -1 without an answer. "FMFACE24F 1 <serial> <person> <token>" (the person as format_target writes it) goes to the
   bridge port; the reply is "FMFACE24F <serial> <yes|no>". */
static int query_face(long long person)
{
    if (face_socket < 0 && (face_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)) < 0) return -1;
    struct sockaddr_in destination = bridge_destination(), source;
    int serial = ++face_serial;
    char packet[256], yes[48], no[48];
    char who[24];
    format_target(who, sizeof(who), person);
    int count = snprintf(packet, sizeof(packet), "FMFACE24F 1 %d %s %s", serial, who, bridge_token);
    snprintf(yes, sizeof(yes), "FMFACE24F %d yes", serial);
    snprintf(no, sizeof(no), "FMFACE24F %d no", serial);
    if (sendto(face_socket, packet, (size_t)count, 0, (struct sockaddr *)&destination, sizeof(destination)) != count)
        goto failed;
    unsigned long long deadline = tick_ms() + (face_quiet ? 0 : FACE_TIMEOUT_MS);
    for (;;) {
        unsigned long long now = tick_ms();
        fd_set readable;
        FD_ZERO(&readable);
        FD_SET(face_socket, &readable);
        struct timeval wait = {0, now < deadline ? (int)(deadline - now) * 1000 : 0};
        int ready = select(face_socket + 1, &readable, NULL, NULL, &wait);
        if (ready < 0) goto failed;
        if (!ready) { face_quiet = TRUE; return -1; }
        char reply[64];
        socklen_t length = sizeof(source);
        ssize_t received = recvfrom(face_socket, reply, sizeof(reply) - 1, 0, (struct sockaddr *)&source, &length);
        if (received <= 0) goto failed;
        reply[received] = 0;
        if (length != sizeof(source) || source.sin_port != destination.sin_port ||
            source.sin_addr.s_addr != destination.sin_addr.s_addr || strncmp(reply, "FMFACE24F ", 10))
            continue;
        if (face_quiet) {  /* The companion is answering again: wait for this check. */
            face_quiet = FALSE;
            deadline = tick_ms() + FACE_TIMEOUT_MS;
        }
        if (!strcmp(reply, yes)) return 1;
        if (!strcmp(reply, no)) return 0;
    }
failed:
    close(face_socket);
    face_socket = -1;
    return -1;
}

/* The rows of a popup's menu; -1 unless the popup's root item names it. */
static int popup_rows(unsigned char *popup, unsigned int popup_id, unsigned char **root, unsigned char **rows)
{
    unsigned char *range[2];
    if (!read_pointer(popup + POPUP_ROOT_ITEM, root) || item_fourcc(*root, SKIN_ID('i','d',' ',' ')) != popup_id ||
        !read_game(*root + ITEM_CHILDREN, range, sizeof(range)) || range[1] < range[0] ||
        (size_t)(range[1] - range[0]) > MAX_POPUP_ROWS * sizeof(rows[0]) || (range[1] - range[0]) % sizeof(rows[0]) ||
        (range[1] > range[0] && !read_game(range[0], rows, (size_t)(range[1] - range[0]))))
        return -1;
    return (int)((range[1] - range[0]) / sizeof(rows[0]));
}

/* A person profile's Overview tab menu lists FM24's person actions (Set
   Nickname, Follow or Unfollow); FM Face appends its two rows to it. */
static BOOL person_overview(unsigned char **rows, int count)
{
    for (int i = 0; i < count; ++i) {
        unsigned int id = item_fourcc(rows[i], SKIN_ID('i','d',' ',' '));
        if (id == SKIN_ID('n','i','c','k') || id == SKIN_ID('S','U','B','S') || id == SKIN_ID('U','S','U','B'))
            return TRUE;
    }
    return FALSE;
}

/* A row whose event (evid) is its ID, added to a popup's root item. */
static void add_menu_row(unsigned char *root, unsigned int id, const char *label)
{
    void *item = make_item(root, label, id);
    if (!item) return;
    set_fourcc((unsigned char *)item + ITEM_PROPERTIES, SKIN_ID('i','d',' ',' '), id);
    set_fourcc((unsigned char *)item + ITEM_PROPERTIES, SKIN_ID('e','v','i','d'), id);
    release_item(item);
}

static BOOL tab_button(unsigned char *object)
{
    void *vtable;
    return read_game(object, &vtable, sizeof(vtable)) && vtable == game_base + PROFILE_MENU_TAB_BUTTON_VTABLE;
}

/* A team's tab menu whose rows include its Squad view (TeSq): FM Face
   appends "Generate missing squad faces…" (FMFS) unless it has it already. */
static void add_squad_rows(unsigned char **children, int count)
{
    unsigned char *root, *rows[MAX_POPUP_ROWS];
    for (int i = 0; i < count; ++i) {
        unsigned int skin_id;
        if (!tab_button(children[i]) || !read_game(children[i] + WIDGET_ID, &skin_id, sizeof(skin_id)))
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
        if (!widget_id(child, manager, &skin_id)) continue;
        if (skin_id == SKIN_ID('P','p','r','f') && !tab_button(child)) continue;
        if (skin_id != SKIN_ID('f','m','n','P') && skin_id != SKIN_ID('f','m','n','S') && skin_id != SKIN_ID('P','p','r','f'))
            continue;
        if (popup) return;
        popup = child;
        popup_id = skin_id;
    }
    if (!popup) return;
    if ((count = popup_rows(popup, popup_id, &root, rows)) < 0) {
        log_line("FMFACE24_POPUP_ROWS_UNREADABLE");
        return;
    }
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
    struct timespec t0, t1, t2;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    long long person = popup_target(manager, popup);
    clock_gettime(CLOCK_MONOTONIC, &t1);
    int face = person ? query_face(person) : -1;
    clock_gettime(CLOCK_MONOTONIC, &t2);
    set_flag(update + ITEM_PROPERTIES, SKIN_ID('d','i','s','a'), face == 0);
    char line[160];
#define US(a, b) (((b).tv_sec - (a).tv_sec) * 1000000LL + ((b).tv_nsec - (a).tv_nsec) / 1000)
    char who[24];
    format_target(who, sizeof(who), person);
    snprintf(line, sizeof(line), "FMFACE24_%s person=%s face=%s id_us=%lld query_us=%lld",
             popup_id == SKIN_ID('P','p','r','f') ? "OVERVIEW_MENU" : "POPUP", who,
             face == 1 ? "yes" : face == 0 ? "no" : "unknown", (long long)US(t0, t1), (long long)US(t1, t2));
    log_line(line);
}

enum { REPLY_NONE, REPLY_ACCEPTED, REPLY_BUSY, REPLY_STALE, REPLY_SOCKET_ERROR };

/* Send one click and wait for the ACK carrying this exact sequence; see fmface24.c. */
static int exchange(int socket_fd, const char *packet, int count, int serial)
{
    char accepted[64], busy[64], stale[64];
    snprintf(accepted, sizeof(accepted), "FMFACE24 ACK %d accepted", serial);
    snprintf(busy, sizeof(busy), "FMFACE24 ACK %d busy", serial);
    snprintf(stale, sizeof(stale), "FMFACE24 ACK %d stale", serial);
    for (int attempt = 0; attempt < 3; ++attempt) {
        if (send(socket_fd, packet, (size_t)count, 0) != count) return REPLY_SOCKET_ERROR;
        unsigned long long deadline = tick_ms() + 700;
        for (;;) {
            unsigned long long now = tick_ms();
            if (now >= deadline) break;
            fd_set readable;
            FD_ZERO(&readable);
            FD_SET(socket_fd, &readable);
            struct timeval wait = {0, (int)(deadline - now) * 1000};
            int ready = select(socket_fd + 1, &readable, NULL, NULL, &wait);
            if (ready < 0) return REPLY_SOCKET_ERROR;
            if (!ready) break;
            char reply[100];
            ssize_t received = recv(socket_fd, reply, sizeof(reply) - 1, 0);
            if (received <= 0) {
                /* ICMP port unreachable: no companion is listening yet. */
                if (received < 0) { usleep(150000); break; }
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

static void *bridge_worker(void *unused)
{
    (void)unused;
    int socket_fd = -1;
    log_line("FMFACE24_BRIDGE_READY");
    for (;;) {
        pthread_mutex_lock(&pending_lock);
        while (!pending.event) pthread_cond_wait(&pending_ready, &pending_lock);
        unsigned int id = pending.event, item = pending.item;
        long long click_ms = pending.click_ms;
        long long person = pending.person;
        long read_ms = pending.read_ms;
        char who[24];
        format_target(who, sizeof(who), person);
        pending.event = 0;
        pthread_mutex_unlock(&pending_lock);
        if (id == PROFILE_EVENT) {
            char line[96];
            snprintf(line, sizeof(line), "FMFACE24_PROFILE_CLICK person=%s read_ms=%ld item=%s", who, read_ms,
                     item == UPDATE_ITEM ? "update" : item == PROFILE_EVENT ? "generate" : "unknown");
            log_line(line);
            if (!item) {
                show_message("FM Face could not tell which menu item was chosen, so nothing was generated. "
                             "Choose it again.");
                continue;
            }
        } else {
            log_line("FMFACE24_SQUAD_CLICK");
        }
        if (socket_fd < 0) socket_fd = open_bridge();
        if (socket_fd < 0) {
            log_line("FMFACE24_BRIDGE_SOCKET_FAILED");
            show_message("FM Face could not open its local connection. Restart FM24 and check fmface24.log.");
            continue;
        }
        int serial = __atomic_add_fetch(&sequence, 1, __ATOMIC_SEQ_CST);
        char packet[256];
        int count = snprintf(packet, sizeof(packet), "FMFACE24 5 %llu %d %s %lld %s %s",
                             (unsigned long long)bridge_instance, serial,
                             id == SQUAD_EVENT ? "squad" : item == UPDATE_ITEM ? "update" : "profile",
                             click_ms, who, bridge_token);
        int result = exchange(socket_fd, packet, count, serial);
        if (result == REPLY_ACCEPTED) { log_line("FMFACE24_CLICK_ACCEPTED"); continue; }
        if (result == REPLY_BUSY) {
            log_line("FMFACE24_CLICK_BUSY");
            show_message("FM Face is already processing a request. Please wait for it to finish.");
            continue;
        }
        if (result == REPLY_STALE) {
            log_line("FMFACE24_CLICK_STALE");
            show_message("FM Face received this click too late to be sure which screen was open, "
                         "so nothing was generated. Choose the menu item again.");
            continue;
        }
        if (result == REPLY_SOCKET_ERROR) {
            log_line("FMFACE24_BRIDGE_SOCKET_ERROR");
            close(socket_fd);
            socket_fd = -1;
        }
        log_line("FMFACE24_COMPANION_UNAVAILABLE");
        show_message("The FM Face companion isn't running. Start fm24/companion.py and try again.");
    }
    return NULL;
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

/* Standard base64 of size bytes into out (NUL-terminated). */
static void base64_encode(const unsigned char *in, size_t size, char *out)
{
    static const char digits[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    size_t written = 0;
    for (size_t i = 0; i < size; i += 3) {
        unsigned int bits = (unsigned int)in[i] << 16 | (i + 1 < size ? (unsigned int)in[i + 1] << 8 : 0) |
                            (i + 2 < size ? in[i + 2] : 0);
        out[written++] = digits[bits >> 18 & 63];
        out[written++] = digits[bits >> 12 & 63];
        out[written++] = i + 1 < size ? digits[bits >> 6 & 63] : '=';
        out[written++] = i + 2 < size ? digits[bits & 63] : '=';
    }
    out[written] = 0;
}

/* The OpenAI key prompt (key_prompt_macos.m), which the companion asks for
   when no key is saved. The typed key stays here only until the next request. */
void fmface_key_prompt(const char *text, void (*answer)(const char *key));
enum { KEY_NONE, KEY_SHOWN, KEY_ENTERED, KEY_CANCELLED };
static int key_state;
static pthread_mutex_t key_lock = PTHREAD_MUTEX_INITIALIZER;
static char entered_key[384];

static void key_answered(const char *key)
{
    pthread_mutex_lock(&key_lock);
    snprintf(entered_key, sizeof(entered_key), "%s", key ? key : "");
    set_state(&key_state, key ? KEY_ENTERED : KEY_CANCELLED);
    pthread_mutex_unlock(&key_lock);
    log_line(key ? "FMFACE24_KEY_ENTERED" : "FMFACE24_KEY_CANCELLED");
}

/* Loopback requests on bridge port + 1 (reload, dialogs, progress, the key
   prompt), all carrying the private token; the protocol is described in
   fmface24.c. "FMFACE24K 1 <serial> <token> <text>" shows the key prompt with
   the base64 text; the same serial polls for "shown", "key <base64>" or
   "cancel". */
static void *reload_listener(void *unused)
{
    (void)unused;
    int socket_fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    struct sockaddr_in local;
    memset(&local, 0, sizeof(local));
    local.sin_family = AF_INET;
    local.sin_port = htons((unsigned short)(bridge_port + 1));
    local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (socket_fd < 0 || bind(socket_fd, (struct sockaddr *)&local, sizeof(local))) {
        log_line("FMFACE24_RELOAD_PORT_FAILED");
        if (socket_fd >= 0) close(socket_fd);
        return NULL;
    }
    log_line("FMFACE24_RELOAD_READY");
    long long last = 0, last_dialog = 0, last_progress = 0, last_key = 0;
    static char packet[4096], title_b64[400], text_b64[1500], title[256], text[1024];
    for (;;) {
        char token[160], kind[16], extra;
        long long serial;
        struct sockaddr_in peer;
        socklen_t size = sizeof(peer);
        ssize_t received = recvfrom(socket_fd, packet, sizeof(packet) - 1, 0, (struct sockaddr *)&peer, &size);
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
            int state = get_state(&dialog_state);
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
            sendto(socket_fd, reply, (size_t)count, 0, (struct sockaddr *)&peer, sizeof(peer));
            continue;
        }
        if (sscanf(packet, "FMFACE24K 1 %lld %159s %1499s %c", &serial, token, text_b64, &extra) == 3) {
            if (serial <= 0 || strcmp(token, bridge_token) || !base64_text(text_b64, text, sizeof(text)))
                continue;
            char key_reply[640];
            pthread_mutex_lock(&key_lock);
            int state = get_state(&key_state);
            if (serial > last_key) {
                if (state == KEY_SHOWN) {
                    status = "busy";
                } else {
                    last_key = serial;
                    memset(entered_key, 0, sizeof(entered_key));
                    set_state(&key_state, KEY_SHOWN);
                    fmface_key_prompt(text, key_answered);
                    log_line("FMFACE24_KEY_PROMPT");
                    status = "shown";
                }
            } else if (serial == last_key) {
                status = state == KEY_CANCELLED ? "cancel" : "shown";
                if (state == KEY_ENTERED) {
                    char encoded[520];
                    base64_encode((const unsigned char *)entered_key, strlen(entered_key), encoded);
                    snprintf(key_reply, sizeof(key_reply), "key %s", encoded);
                    status = key_reply;
                }
            } else {
                pthread_mutex_unlock(&key_lock);
                continue;
            }
            char answer[700];
            count = snprintf(answer, sizeof(answer), "FMFACE24K %lld %s", serial, status);
            pthread_mutex_unlock(&key_lock);
            sendto(socket_fd, answer, (size_t)count, 0, (struct sockaddr *)&peer, sizeof(peer));
            continue;
        }
        if (sscanf(packet, "FMFACE24P 1 %lld %159s %15s %1499s %c", &serial, token, kind, text_b64, &extra) == 4) {
            if (serial <= 0 || strcmp(token, bridge_token) || (strcmp(kind, "portrait") && strcmp(kind, "dialog") && strcmp(kind, "finish")) ||
                (strcmp(text_b64, "-") && !base64_text(text_b64, text, sizeof(text))))
                continue;
            int state = get_state(&progress_state);
            if (serial > last_progress) {
                if (state == PROGRESS_REQUESTED) {
                    status = "busy";
                } else {
                    last_progress = serial;
                    progress_target = !strcmp(kind, "portrait") ? 1 : !strcmp(kind, "dialog") ? 2 : 3;
                    snprintf(progress_text, sizeof(progress_text), "%s", strcmp(text_b64, "-") ? text : "");
                    set_state(&progress_state, PROGRESS_REQUESTED);
                    status = "queued";
                }
            } else if (serial == last_progress) {
                status = state == PROGRESS_DONE ? "done" : state == PROGRESS_FAILED ? "failed" : "queued";
            } else {
                continue;
            }
            count = snprintf(reply, sizeof(reply), "FMFACE24P %lld %s", serial, status);
            sendto(socket_fd, reply, (size_t)count, 0, (struct sockaddr *)&peer, sizeof(peer));
            continue;
        }
        if (sscanf(packet, "FMFACE24R 1 %lld %159s %c", &serial, token, &extra) != 2 ||
            serial <= 0 || strcmp(token, bridge_token))
            continue;
        int state = get_state(&reload_state);
        if (serial > last) {
            if (state == RELOAD_REQUESTED || state == RELOAD_RUNNING) {
                status = "busy";
            } else {
                last = serial;
                set_state(&reload_state, RELOAD_REQUESTED);
                log_line("FMFACE24_RELOAD_REQUESTED");
                status = "queued";
            }
        } else if (serial == last) {
            status = state == RELOAD_DONE ? "done" : state == RELOAD_FAILED ? "failed" : "queued";
        } else {
            continue;
        }
        count = snprintf(reply, sizeof(reply), "FMFACE24R %lld %s", serial, status);
        sendto(socket_fd, reply, (size_t)count, 0, (struct sockaddr *)&peer, sizeof(peer));
    }
}

/* Memory service for the companion on bridge port + 2 (loopback TCP).
   macOS doesn't let another program read FM24's memory (its hardened
   runtime has no get-task-allow), so the companion's reads come through
   here. A connection must first send "FMFACE24M 1 <token>\n"; requests are
   then one byte and fixed-size little-endian arguments:
     'B': the image header address and the build's UUID.
     'M': writable regions not backed by a file, as {u64 start, u64 end}.
     'R' u64 address, u32 size (at most 16 MB): u32 status (0 or a
         kern_return_t) followed, when 0, by the bytes.
   Reads are mach_vm_read_overwrite on FM24's own task, so an address the
   game has unmapped fails instead of crashing it. Nothing is written. */
#define MEMORY_MAX_READ (16u << 20)

static BOOL send_all(int fd, const void *data, size_t size)
{
    const unsigned char *bytes = data;
    while (size) {
        ssize_t sent = send(fd, bytes, size, 0);
        if (sent <= 0) return FALSE;
        bytes += sent;
        size -= (size_t)sent;
    }
    return TRUE;
}

static BOOL receive_all(int fd, void *data, size_t size)
{
    unsigned char *bytes = data;
    while (size) {
        ssize_t got = recv(fd, bytes, size, 0);
        if (got <= 0) return FALSE;
        bytes += got;
        size -= (size_t)got;
    }
    return TRUE;
}

static BOOL send_regions(int fd)
{
    static uint64_t ranges[2 * 65536];
    uint32_t count = 0;
    mach_vm_address_t address = 0;
    for (;;) {
        mach_vm_size_t size = 0;
        vm_region_basic_info_data_64_t info;
        mach_msg_type_number_t info_count = VM_REGION_BASIC_INFO_COUNT_64;
        mach_port_t object = MACH_PORT_NULL;
        if (mach_vm_region(mach_task_self(), &address, &size, VM_REGION_BASIC_INFO_64,
                           (vm_region_info_t)&info, &info_count, &object) != KERN_SUCCESS)
            break;
        char path[8];
        if ((info.protection & (VM_PROT_READ | VM_PROT_WRITE)) == (VM_PROT_READ | VM_PROT_WRITE) &&
            size >= 64 && proc_regionfilename(getpid(), address, path, sizeof(path)) <= 0 &&
            count < 65536) {
            ranges[2 * count] = address;
            ranges[2 * count + 1] = address + size;
            ++count;
        }
        address += size;
    }
    return send_all(fd, &count, sizeof(count)) && send_all(fd, ranges, (size_t)count * 16);
}

static void *memory_connection(void *argument)
{
    int fd = (int)(intptr_t)argument;
    unsigned char *buffer = NULL;
    char hello[200];
    size_t used = 0;
    while (used < sizeof(hello) - 1) {
        ssize_t got = recv(fd, hello + used, 1, 0);
        if (got <= 0) goto done;
        if (hello[used++] == '\n') break;
    }
    hello[used] = 0;
    char expected[200];
    snprintf(expected, sizeof(expected), "FMFACE24M 1 %s\n", bridge_token);
    if (strcmp(hello, expected) || !send_all(fd, "OK\n", 3)) goto done;
    if (!(buffer = malloc(MEMORY_MAX_READ))) goto done;
    for (;;) {
        unsigned char op;
        if (!receive_all(fd, &op, 1)) break;
        if (op == 'B') {
            static const unsigned char uuid[16] = FM24_UUID_BYTES;
            uint64_t base = (uint64_t)(uintptr_t)game_base;
            if (!send_all(fd, &base, 8) || !send_all(fd, uuid, 16)) break;
        } else if (op == 'M') {
            if (!send_regions(fd)) break;
        } else if (op == 'R') {
            uint64_t address;
            uint32_t size, status;
            if (!receive_all(fd, &address, 8) || !receive_all(fd, &size, 4)) break;
            mach_vm_size_t copied = 0;
            kern_return_t result = size && size <= MEMORY_MAX_READ ?
                mach_vm_read_overwrite(mach_task_self(), address, size, (mach_vm_address_t)buffer, &copied) :
                KERN_INVALID_ARGUMENT;
            status = result == KERN_SUCCESS && copied == size ? 0 : (result ? (uint32_t)result : KERN_FAILURE);
            if (!send_all(fd, &status, 4) || (!status && !send_all(fd, buffer, size))) break;
        } else {
            break;
        }
    }
done:
    free(buffer);
    close(fd);
    return NULL;
}

static void *memory_listener(void *unused)
{
    (void)unused;
    int listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in local;
    memset(&local, 0, sizeof(local));
    local.sin_family = AF_INET;
    local.sin_port = htons((unsigned short)(bridge_port + 2));
    local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    /* A restarted FM24 must get the port back while the last run's
       connections are still in TIME_WAIT. */
    int reuse = 1;
    if (listener >= 0) setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
    if (listener < 0 || bind(listener, (struct sockaddr *)&local, sizeof(local)) || listen(listener, 4)) {
        log_line("FMFACE24_MEMORY_PORT_FAILED");
        if (listener >= 0) close(listener);
        return NULL;
    }
    log_line("FMFACE24_MEMORY_READY");
    for (;;) {
        struct sockaddr_in peer;
        socklen_t size = sizeof(peer);
        int fd = accept(listener, (struct sockaddr *)&peer, &size);
        if (fd < 0) continue;
        pthread_t thread;
        if (peer.sin_addr.s_addr != htonl(INADDR_LOOPBACK) ||
            pthread_create(&thread, NULL, memory_connection, (void *)(intptr_t)fd)) {
            close(fd);
            continue;
        }
        pthread_detach(thread);
    }
}

/* Replace one validated vtable pointer; refuses unless it holds the original.
   The vtables are in __DATA_CONST, which dyld makes read-only after binding
   but whose maximum protection allows writing again. */
static BOOL replace_slot(void **slot, void *expected, void *replacement)
{
    uintptr_t page = (uintptr_t)slot & ~(uintptr_t)(getpagesize() - 1);
    if (*slot != expected || mprotect((void *)page, (size_t)getpagesize(), PROT_READ | PROT_WRITE)) return FALSE;
    __atomic_store_n(slot, replacement, __ATOMIC_SEQ_CST);
    mprotect((void *)page, (size_t)getpagesize(), PROT_READ);
    return TRUE;
}

static void install_squad_menu(unsigned char *base)
{
    original_menu_add = (menu_add_function)(base + SQUAD_MENU_ADD_ORIGINAL);
    original_menu_event = (menu_event_function)(base + SQUAD_MENU_EVENT_ORIGINAL);
    void **add_slot = (void **)(base + SQUAD_MENU_ADD_SLOT);
    void **event_slot = (void **)(base + SQUAD_MENU_EVENT_SLOT);
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

/* The main image's LC_UUID must be the validated build's (arm64 slice). */
static BOOL supported_image(const struct mach_header_64 *header)
{
    static const unsigned char expected[16] = FM24_UUID_BYTES;
    if (header->magic != MH_MAGIC_64 || header->cputype != CPU_TYPE_ARM64) return FALSE;
    const unsigned char *command = (const unsigned char *)(header + 1);
    for (uint32_t i = 0; i < header->ncmds; ++i) {
        const struct load_command *load = (const struct load_command *)command;
        if (load->cmd == LC_UUID) return !memcmp(((const struct uuid_command *)load)->uuid, expected, 16);
        command += load->cmdsize;
    }
    return FALSE;
}

/* "key=value" from fmface24.ini's [fmface] section. */
static BOOL config_value(const char *path, const char *key, char *value, size_t size)
{
    FILE *file = fopen(path, "r");
    if (!file) return FALSE;
    char line[512];
    BOOL section = FALSE, found = FALSE;
    size_t key_length = strlen(key);
    while (!found && fgets(line, sizeof(line), file)) {
        line[strcspn(line, "\r\n")] = 0;
        if (line[0] == '[') { section = !strcmp(line, "[fmface]"); continue; }
        if (section && !strncmp(line, key, key_length) && line[key_length] == '=') {
            snprintf(value, size, "%s", line + key_length + 1);
            found = TRUE;
        }
    }
    fclose(file);
    return found;
}

/* Keeps FM Face out of programs FM24 starts (a browser, a crash reporter). */
static void forget_insert(const char *self)
{
    const char *inserted = getenv("DYLD_INSERT_LIBRARIES");
    if (!inserted) return;
    char rest[4096] = "";
    char copy[4096];
    snprintf(copy, sizeof(copy), "%s", inserted);
    for (char *saved = NULL, *part = strtok_r(copy, ":", &saved); part; part = strtok_r(NULL, ":", &saved)) {
        if (!strcmp(part, self)) continue;
        if (rest[0]) strlcat(rest, ":", sizeof(rest));
        strlcat(rest, part, sizeof(rest));
    }
    if (rest[0]) setenv("DYLD_INSERT_LIBRARIES", rest, 1);
    else unsetenv("DYLD_INSERT_LIBRARIES");
}

__attribute__((constructor)) static void install_event_hook(void)
{
    Dl_info self;
    char directory[1024], config[1100], value[160];
    if (!dladdr((void *)install_event_hook, &self) || !self.dli_fname) return;
    forget_insert(self.dli_fname);
    const struct mach_header_64 *header = NULL;
    for (uint32_t i = 0; i < _dyld_image_count(); ++i) {
        const char *name = _dyld_get_image_name(i);
        static const char suffix[] = "/fm.app/Contents/MacOS/fm";
        size_t length = name ? strlen(name) : 0;
        if (length >= sizeof(suffix) - 1 && !strcmp(name + length - (sizeof(suffix) - 1), suffix))
            header = (const struct mach_header_64 *)_dyld_get_image_header(i);
    }
    if (!header) return;  /* Not FM24 (a program it started). */
    snprintf(directory, sizeof(directory), "%s", self.dli_fname);
    char *slash = strrchr(directory, '/');
    if (!slash) return;
    *slash = 0;
    snprintf(log_path, sizeof(log_path), "%s/fmface24.log", directory);
    snprintf(config, sizeof(config), "%s/fmface24.ini", directory);
    if (!config_value(config, "enabled", value, sizeof(value)) || atoi(value) != 1) return;
    int port = config_value(config, "port", value, sizeof(value)) ? atoi(value) : 0;
    if (!config_value(config, "token", bridge_token, sizeof(bridge_token))) bridge_token[0] = 0;
    size_t length = strlen(bridge_token);
    if (port < 1024 || port > 65533 || length < 40 || length > 128) {
        log_line("FMFACE24_INVALID_CONFIG"); return;
    }
    for (size_t i = 0; i < length; ++i) {
        char c = bridge_token[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) {
            log_line("FMFACE24_INVALID_TOKEN"); return;
        }
    }
    bridge_port = (unsigned short)port;
    bridge_instance = unix_ms();
    if (!supported_image(header)) { log_line("FMFACE24_UNSUPPORTED_EXECUTABLE"); return; }
    unsigned char *base = game_base = (unsigned char *)header;
    event_handler *slot = (event_handler *)(base + VTABLE_SLOT_OFFSET);
    original_event = (event_handler)(base + HANDLER_OFFSET);
    if (*slot != original_event) { log_line("FMFACE24_HANDLER_MISMATCH"); return; }
    pthread_t thread;
    if (pthread_create(&thread, NULL, bridge_worker, NULL)) return;
    pthread_detach(thread);
    if (!replace_slot((void **)slot, (void *)original_event, (void *)native_event)) {
        log_line("FMFACE24_VTABLE_PROTECT_FAILED"); return;
    }
    log_line("FMFACE24_NATIVE_MENU_HOOK_INSTALLED");
    install_squad_menu(base);
    if (!pthread_create(&thread, NULL, reload_listener, NULL)) pthread_detach(thread);
    if (!pthread_create(&thread, NULL, memory_listener, NULL)) pthread_detach(thread);
}

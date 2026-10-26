/* SPDX-License-Identifier: GPL-3.0-or-later (see NOTICE.md). RegenerateFM's OpenAI key prompt for FM24 on macOS, part of libregeneratefm24.dylib.
   The companion asks for it (REGENERATEFM24K in regeneratefm24_macos.c) when no key is
   saved. It is a sheet on FM24's window with a secure field: the key isn't
   shown, and Cmd+V pastes even though FM24 has no Edit menu. */
#import <AppKit/AppKit.h>

typedef void (*key_answer_function)(const char *key);

static NSString *const KEYS_PAGE = @"https://platform.openai.com/api-keys";
/* The answer for Buy credits / Sign in, which the companion handles (companion.CREDITS_CHOICE). */
static const char *const CREDITS_CHOICE = "regeneratefm:credits";

static SEL edit_action(NSEvent *event)
{
    NSEventModifierFlags flags = event.modifierFlags & NSEventModifierFlagDeviceIndependentFlagsMask;
    if (flags != NSEventModifierFlagCommand) return NULL;
    NSString *key = event.charactersIgnoringModifiers.lowercaseString;
    if ([key isEqualToString:@"v"]) return @selector(paste:);
    if ([key isEqualToString:@"x"]) return @selector(cut:);
    if ([key isEqualToString:@"a"]) return @selector(selectAll:);
    if ([key isEqualToString:@"z"]) return @selector(undo:);
    return NULL;
}

/* Main thread. "Get a key…" opens OpenAI's key page and asks again; "Buy
   credits / Sign in" answers CREDITS_CHOICE. */
static void show_prompt(NSString *text, key_answer_function answer)
{
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = @"RegenerateFM: credits or your OpenAI API key";
    alert.informativeText = text;
    [alert addButtonWithTitle:@"Save key"];
    [alert addButtonWithTitle:@"Cancel"];
    [alert addButtonWithTitle:@"Get a key…"];
    [alert addButtonWithTitle:@"Buy credits / Sign in"];
    /* A key is longer than the field, which shows only its last dots, so the
       line below says how much was pasted and how it ends. */
    NSView *accessory = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 380, 46)];
    NSSecureTextField *field = [[NSSecureTextField alloc] initWithFrame:NSMakeRect(0, 22, 380, 24)];
    field.placeholderString = @"sk-…";
    field.cell.scrollable = YES;
    field.cell.wraps = NO;
    field.cell.usesSingleLineMode = YES;
    field.lineBreakMode = NSLineBreakByClipping;
    NSTextField *pasted = [NSTextField labelWithString:@""];
    pasted.frame = NSMakeRect(0, 0, 380, 16);
    pasted.font = [NSFont systemFontOfSize:NSFont.smallSystemFontSize];
    pasted.textColor = NSColor.secondaryLabelColor;
    pasted.lineBreakMode = NSLineBreakByTruncatingMiddle;
    [accessory addSubview:field];
    [accessory addSubview:pasted];
    alert.accessoryView = accessory;
    id changed = [[NSNotificationCenter defaultCenter]
        addObserverForName:NSControlTextDidChangeNotification object:field queue:nil usingBlock:^(NSNotification *note) {
            (void)note;
            NSString *key = [field.stringValue stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
            pasted.stringValue = key.length < 8 ? @"" :
                [NSString stringWithFormat:@"%lu characters, ending …%@", (unsigned long)key.length,
                                           [key substringFromIndex:key.length - 4]];
        }];
    /* The field can take focus only once the alert is laid out. */
    [alert layout];
    alert.window.initialFirstResponder = field;
    NSWindow *prompt = alert.window;
    /* Keys typed into the prompt are its own: FM24 never sees them. */
    id monitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskKeyDown handler:^NSEvent *(NSEvent *event) {
        if (event.window != prompt) return event;
        SEL action = edit_action(event);
        return action && [NSApp sendAction:action to:nil from:nil] ? nil : event;
    }];
    void (^finish)(NSModalResponse) = ^(NSModalResponse response) {
        [NSEvent removeMonitor:monitor];
        [[NSNotificationCenter defaultCenter] removeObserver:changed];
        if (response == NSAlertThirdButtonReturn + 1) {
            answer(CREDITS_CHOICE);
            return;
        }
        if (response == NSAlertThirdButtonReturn) {
            [[NSWorkspace sharedWorkspace] openURL:[NSURL URLWithString:KEYS_PAGE]];
            show_prompt(text, answer);
            return;
        }
        answer(response == NSAlertFirstButtonReturn ? field.stringValue.UTF8String : NULL);
    };
    NSWindow *game = NSApp.keyWindow ?: NSApp.mainWindow;
    if (game && !game.attachedSheet) {
        [alert beginSheetModalForWindow:game completionHandler:finish];
        [prompt makeFirstResponder:field];
    } else {
        [NSApp activateIgnoringOtherApps:YES];
        /* Runs once the modal session has made the prompt the key window. */
        dispatch_async(dispatch_get_main_queue(), ^{ [prompt makeFirstResponder:field]; });
        finish([alert runModal]);
    }
}

/* Any thread: shows the prompt on the main thread; answer gets the typed
   text, or NULL for Cancel, also on the main thread. */
void regeneratefm_key_prompt(const char *text, key_answer_function answer)
{
    NSString *shown = [NSString stringWithUTF8String:text] ?: @"";
    dispatch_async(dispatch_get_main_queue(), ^{ show_prompt(shown, answer); });
}

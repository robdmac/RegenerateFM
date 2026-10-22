"""A small Windows dialog that asks for the OpenAI key (Windows and Proton).

The companion FM24 starts has no window, and under Wine a console window
can't take a paste from the desktop's clipboard, so the key is typed or
pasted into an ordinary Win32 edit box instead: it shows dots, takes Ctrl+V
and the right-click menu, and Enter saves. The window stays above FM24. Only
ctypes and user32/shell32 are used; nothing is installed.
"""

from __future__ import annotations

import ctypes
import sys
from ctypes import wintypes

IDOK, IDCANCEL, ID_GET_KEY, ID_CREDITS = 1, 2, 3, 4
# What ask() returns for Buy credits / Sign in (companion.CREDITS_CHOICE).
CREDITS_CHOICE = "regeneratefm:credits"
_EDIT_ID = 10

if sys.platform == "win32":
    _user32 = ctypes.WinDLL("user32", use_last_error=True)
    _gdi32 = ctypes.WinDLL("gdi32")
    _shell32 = ctypes.WinDLL("shell32")
    _kernel32 = ctypes.WinDLL("kernel32")
    LRESULT = ctypes.c_ssize_t
    WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)

    class WNDCLASSW(ctypes.Structure):
        _fields_ = [("style", wintypes.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
                    ("cbWndExtra", ctypes.c_int), ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
                    ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH),
                    ("lpszMenuName", wintypes.LPCWSTR), ("lpszClassName", wintypes.LPCWSTR)]

    _user32.DefWindowProcW.restype = LRESULT
    _user32.DefWindowProcW.argtypes = (wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
    _user32.CreateWindowExW.restype = wintypes.HWND
    _user32.CreateWindowExW.argtypes = (wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
                                        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HWND,
                                        wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID)
    _user32.SendMessageW.restype = LRESULT
    _user32.SendMessageW.argtypes = (wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
    _user32.RegisterClassW.argtypes = (ctypes.POINTER(WNDCLASSW),)
    _user32.GetDlgItem.restype = wintypes.HWND
    _user32.GetDlgItem.argtypes = (wintypes.HWND, ctypes.c_int)
    _user32.GetWindowTextLengthW.argtypes = (wintypes.HWND,)
    _user32.GetWindowTextW.argtypes = (wintypes.HWND, wintypes.LPWSTR, ctypes.c_int)
    _user32.IsDialogMessageW.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.MSG))
    _user32.GetMessageW.argtypes = (ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT)
    _user32.TranslateMessage.argtypes = (ctypes.POINTER(wintypes.MSG),)
    _user32.DispatchMessageW.argtypes = (ctypes.POINTER(wintypes.MSG),)
    _user32.DestroyWindow.argtypes = (wintypes.HWND,)
    _user32.SetFocus.argtypes = (wintypes.HWND,)
    _user32.SetForegroundWindow.argtypes = (wintypes.HWND,)
    _user32.UnregisterClassW.argtypes = (wintypes.LPCWSTR, wintypes.HINSTANCE)
    _user32.ShowWindow.argtypes = (wintypes.HWND, ctypes.c_int)
    _user32.GetSystemMetrics.argtypes = (ctypes.c_int,)
    _user32.PostQuitMessage.argtypes = (ctypes.c_int,)
    _user32.LoadCursorW.restype = wintypes.HANDLE
    _user32.LoadCursorW.argtypes = (wintypes.HINSTANCE, wintypes.LPVOID)
    _gdi32.GetStockObject.restype = wintypes.HGDIOBJ
    _shell32.ShellExecuteW.argtypes = (wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.LPCWSTR,
                                       wintypes.LPCWSTR, ctypes.c_int)
    _kernel32.GetModuleHandleW.restype = wintypes.HMODULE

WS_OVERLAPPED_DIALOG = 0x00C00000 | 0x00080000  # WS_CAPTION | WS_SYSMENU
WS_CHILD, WS_VISIBLE, WS_TABSTOP, WS_BORDER = 0x40000000, 0x10000000, 0x00010000, 0x00800000
WS_EX_TOPMOST, WS_EX_DLGMODALFRAME, WS_EX_CLIENTEDGE = 0x00000008, 0x00000001, 0x00000200
ES_PASSWORD, ES_AUTOHSCROLL = 0x0020, 0x0080
BS_DEFPUSHBUTTON = 0x0001
WM_DESTROY, WM_CLOSE, WM_COMMAND, WM_SETFONT = 0x0002, 0x0010, 0x0111, 0x0030
WIDTH, HEIGHT = 640, 400


def ask(text: str, *, title: str = "RegenerateFM: credits or OpenAI key", link: str | None = None,
        credits: bool = False) -> str | None:
    """Show the dialog and return what was entered, None for Cancel or
    closing it, or CREDITS_CHOICE for Buy credits / Sign in (shown when
    ``credits``). ``text`` may have line breaks; ``link`` is opened by Get a key…"""
    if sys.platform != "win32":
        raise OSError("The key dialog is a Windows dialog.")
    result: dict[str, str | None] = {"key": None}
    instance = _kernel32.GetModuleHandleW(None)

    def procedure(window, message, wparam, lparam):
        if message == WM_COMMAND:
            command = wparam & 0xFFFF
            if command == IDOK:
                edit = _user32.GetDlgItem(window, _EDIT_ID)
                buffer = ctypes.create_unicode_buffer(_user32.GetWindowTextLengthW(edit) + 1)
                _user32.GetWindowTextW(edit, buffer, len(buffer))
                result["key"] = buffer.value
                _user32.DestroyWindow(window)
            elif command == IDCANCEL:
                _user32.DestroyWindow(window)
            elif command == ID_CREDITS:
                result["key"] = CREDITS_CHOICE
                _user32.DestroyWindow(window)
            elif command == ID_GET_KEY and link:
                _shell32.ShellExecuteW(window, "open", link, None, None, 1)
            return 0
        if message == WM_CLOSE:
            _user32.DestroyWindow(window)
            return 0
        if message == WM_DESTROY:
            _user32.PostQuitMessage(0)
            return 0
        return _user32.DefWindowProcW(window, message, wparam, lparam)

    callback = WNDPROC(procedure)  # Kept alive until the window is gone.
    name = f"RegenerateFMKeyDialog{id(callback)}"
    window_class = WNDCLASSW(lpfnWndProc=callback, hInstance=instance, lpszClassName=name,
                             hCursor=_user32.LoadCursorW(None, ctypes.c_void_p(32512)),  # IDC_ARROW
                             hbrBackground=wintypes.HBRUSH(16))  # COLOR_BTNFACE + 1
    if not _user32.RegisterClassW(ctypes.byref(window_class)):
        raise OSError(f"Couldn't register the key dialog ({ctypes.get_last_error()}).")
    left = max(0, (_user32.GetSystemMetrics(0) - WIDTH) // 2)
    top = max(0, (_user32.GetSystemMetrics(1) - HEIGHT) // 3)
    window = _user32.CreateWindowExW(WS_EX_TOPMOST | WS_EX_DLGMODALFRAME, name, title, WS_OVERLAPPED_DIALOG,
                                     left, top, WIDTH, HEIGHT, None, None, instance, None)
    if not window:
        _user32.UnregisterClassW(name, instance)
        raise OSError(f"Couldn't open the key dialog ({ctypes.get_last_error()}).")
    font = _gdi32.GetStockObject(17)  # DEFAULT_GUI_FONT

    def child(kind, label, style, x, y, width, height, control_id, extended=0):
        handle = _user32.CreateWindowExW(extended, kind, label, WS_CHILD | WS_VISIBLE | style, x, y, width, height,
                                         window, wintypes.HMENU(control_id), instance, None)
        _user32.SendMessageW(handle, WM_SETFONT, font, 1)
        return handle

    child("STATIC", text.replace("\n", "\r\n"), 0, 16, 14, WIDTH - 48, 250, 0)
    edit = child("EDIT", "", WS_TABSTOP | ES_PASSWORD | ES_AUTOHSCROLL, 16, 272, WIDTH - 48, 24, _EDIT_ID,
                 WS_EX_CLIENTEDGE)
    child("BUTTON", "Save key", WS_TABSTOP | BS_DEFPUSHBUTTON, WIDTH - 288, 312, 120, 30, IDOK)
    child("BUTTON", "Cancel", WS_TABSTOP, WIDTH - 160, 312, 112, 30, IDCANCEL)
    left = 16
    if credits:
        child("BUTTON", "Buy credits / Sign in", WS_TABSTOP, left, 312, 160, 30, ID_CREDITS)
        left += 168
    if link:
        child("BUTTON", "Get a key…", WS_TABSTOP, left, 312, 110, 30, ID_GET_KEY)
    _user32.ShowWindow(window, 5)  # SW_SHOW
    _user32.SetForegroundWindow(window)
    _user32.SetFocus(edit)
    message = wintypes.MSG()
    while _user32.GetMessageW(ctypes.byref(message), None, 0, 0) > 0:
        # Tab between controls, Enter for Save key, Esc for Cancel.
        if not _user32.IsDialogMessageW(window, ctypes.byref(message)):
            _user32.TranslateMessage(ctypes.byref(message))
            _user32.DispatchMessageW(ctypes.byref(message))
    _user32.UnregisterClassW(name, instance)
    return result["key"]

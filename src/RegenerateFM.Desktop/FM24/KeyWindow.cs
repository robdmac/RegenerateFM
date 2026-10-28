using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RegenerateFM.Desktop.FM24
{
    // A small Windows dialog that asks for the OpenAI key (Windows and Proton).
    // The companion FM24 starts has no window, and under Wine a console window
    // can't take a paste from the desktop's clipboard, so the key is typed or
    // pasted into an ordinary Win32 edit box instead: it shows dots, takes
    // Ctrl+V and the right-click menu, and Enter saves. The window stays above
    // FM24. Only user32, gdi32 and shell32 are used.
    public static class KeyWindow
    {
        private const int IdOk = 1, IdCancel = 2, IdGetKey = 3, IdCredits = 4, EditId = 10;
        private const int Width = 640, Height = 400;
        private const uint OverlappedDialog = 0x00C00000 | 0x00080000;  // WS_CAPTION | WS_SYSMENU
        private const uint Child = 0x40000000, Visible = 0x10000000, TabStop = 0x00010000;
        private const uint ExTopmost = 0x00000008, ExModalFrame = 0x00000001, ExClientEdge = 0x00000200;
        private const uint Password = 0x0020, AutoHorizontalScroll = 0x0080, DefaultPushButton = 0x0001;
        private const uint Destroy = 0x0002, Close = 0x0010, Command = 0x0111, SetFont = 0x0030;

        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

        // Shows the dialog and returns what was entered, null for Cancel or
        // closing it, or Companion.CreditsChoice for Buy credits / Sign in
        // (shown when ``credits``). ``text`` may have line breaks; ``link`` is
        // opened by Get a key….
        public static string Ask(string text, string title = null, string link = null, bool credits = false)
        {
            title ??= Text.T("RegenerateFM: credits or OpenAI key");
            if (!OperatingSystem.IsWindows())
                throw new IOException("The key dialog is a Windows dialog.");
            string result = null;
            IntPtr instance = GetModuleHandleW(null);
            WindowProcedure procedure = (window, message, wparam, lparam) =>
            {
                if (message == Command)
                {
                    int command = (int)(wparam.ToInt64() & 0xFFFF);
                    if (command == IdOk)
                    {
                        IntPtr edit = GetDlgItem(window, EditId);
                        var buffer = new StringBuilder(GetWindowTextLengthW(edit) + 1);
                        GetWindowTextW(edit, buffer, buffer.Capacity);
                        result = buffer.ToString();
                        DestroyWindow(window);
                    }
                    else if (command == IdCancel)
                        DestroyWindow(window);
                    else if (command == IdCredits)
                    {
                        result = Companion.CreditsChoice;
                        DestroyWindow(window);
                    }
                    else if (command == IdGetKey && link != null)
                        ShellExecuteW(window, "open", link, null, null, 1);
                    return IntPtr.Zero;
                }
                if (message == Close)
                {
                    DestroyWindow(window);
                    return IntPtr.Zero;
                }
                if (message == Destroy)
                {
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                }
                return DefWindowProcW(window, message, wparam, lparam);
            };
            string name = "RegenerateFMKeyDialog" + Guid.NewGuid().ToString("N");
            var windowClass = new WindowClass
            {
                Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = instance,
                ClassName = name,
                Cursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),  // IDC_ARROW
                Background = new IntPtr(16),                            // COLOR_BTNFACE + 1
            };
            if (RegisterClassW(ref windowClass) == 0)
                throw new IOException("Couldn't register the key dialog (" + Marshal.GetLastWin32Error() + ").");
            try
            {
                // Each button is as wide as its text needs, and at least its
                // English width; the dialog widens when a language's buttons
                // don't fit in one row.
                IntPtr font = GetStockObject(17);  // DEFAULT_GUI_FONT
                string save = Text.T("Save key"), cancel = Text.T("Cancel"), buy = Text.T("Buy credits / Sign in"), get = Text.T("Get a key…");
                int Fit(string label, int least) => Math.Max(least, TextWidth(font, label) + 24);
                int saveWidth = Fit(save, 120), cancelWidth = Fit(cancel, 112), buyWidth = Fit(buy, 160), getWidth = Fit(get, 110);
                int leftButtons = (credits ? buyWidth + 8 : 0) + (link != null ? getWidth + 8 : 0);
                int width = Math.Max(Width, 16 + leftButtons + 50 + saveWidth + 8 + cancelWidth + 48);
                int left = Math.Max(0, (GetSystemMetrics(0) - width) / 2);
                int top = Math.Max(0, (GetSystemMetrics(1) - Height) / 3);
                IntPtr dialog = CreateWindowExW(ExTopmost | ExModalFrame, name, title, OverlappedDialog, left, top, width, Height,
                                                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (dialog == IntPtr.Zero)
                    throw new IOException("Couldn't open the key dialog (" + Marshal.GetLastWin32Error() + ").");
                IntPtr Add(string kind, string label, uint style, int x, int y, int width, int height, int id, uint extended = 0)
                {
                    IntPtr handle = CreateWindowExW(extended, kind, label, Child | Visible | style, x, y, width, height,
                                                    dialog, new IntPtr(id), instance, IntPtr.Zero);
                    SendMessageW(handle, SetFont, font, new IntPtr(1));
                    return handle;
                }
                Add("STATIC", text.Replace("\n", "\r\n"), 0, 16, 14, width - 48, 250, 0);
                IntPtr edit = Add("EDIT", "", TabStop | Password | AutoHorizontalScroll, 16, 272, width - 48, 24, EditId, ExClientEdge);
                int cancelX = width - 48 - cancelWidth;
                Add("BUTTON", save, TabStop | DefaultPushButton, cancelX - 8 - saveWidth, 312, saveWidth, 30, IdOk);
                Add("BUTTON", cancel, TabStop, cancelX, 312, cancelWidth, 30, IdCancel);
                int x = 16;
                if (credits)
                {
                    Add("BUTTON", buy, TabStop, x, 312, buyWidth, 30, IdCredits);
                    x += buyWidth + 8;
                }
                if (link != null)
                    Add("BUTTON", get, TabStop, x, 312, getWidth, 30, IdGetKey);
                ShowWindow(dialog, 5);  // SW_SHOW
                SetForegroundWindow(dialog);
                SetFocus(edit);
                while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    // Tab between controls, Enter for Save key, Esc for Cancel.
                    if (!IsDialogMessageW(dialog, ref message))
                    {
                        TranslateMessage(ref message);
                        DispatchMessageW(ref message);
                    }
                }
            }
            finally
            {
                UnregisterClassW(name, instance);
                GC.KeepAlive(procedure);
            }
            return result;
        }

        // ``text``'s width in pixels in ``font``.
        private static int TextWidth(IntPtr font, string text)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                IntPtr previous = SelectObject(screen, font);
                GetTextExtentPoint32W(screen, text, text.Length, out var size);
                SelectObject(screen, previous);
                return size.Width;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Size
        {
            public int Width, Height;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Style;
            public IntPtr Procedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string MenuName;
            public string ClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr Window;
            public uint Value;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X, Y;
        }

        [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassW(ref WindowClass windowClass);

        [DllImport("user32", CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClassW(string name, IntPtr instance);

        [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint extended, string className, string title, uint style, int x, int y,
                                                     int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32")]
        private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

        [DllImport("user32")]
        private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

        [DllImport("user32")]
        private static extern IntPtr GetDlgItem(IntPtr window, int id);

        [DllImport("user32")]
        private static extern int GetWindowTextLengthW(IntPtr window);

        [DllImport("user32", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int size);

        [DllImport("user32")]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32")]
        private static extern void PostQuitMessage(int code);

        [DllImport("user32")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32")]
        private static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32")]
        private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

        [DllImport("user32")]
        private static extern int GetMessageW(out Message message, IntPtr window, uint first, uint last);

        [DllImport("user32")]
        private static extern bool IsDialogMessageW(IntPtr dialog, ref Message message);

        [DllImport("user32")]
        private static extern bool TranslateMessage(ref Message message);

        [DllImport("user32")]
        private static extern IntPtr DispatchMessageW(ref Message message);

        [DllImport("gdi32")]
        private static extern IntPtr GetStockObject(int index);

        [DllImport("gdi32")]
        private static extern IntPtr SelectObject(IntPtr context, IntPtr item);

        [DllImport("gdi32", CharSet = CharSet.Unicode)]
        private static extern bool GetTextExtentPoint32W(IntPtr context, string text, int length, out Size size);

        [DllImport("user32")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32")]
        private static extern int ReleaseDC(IntPtr window, IntPtr context);

        [DllImport("shell32", CharSet = CharSet.Unicode)]
        private static extern IntPtr ShellExecuteW(IntPtr window, string operation, string file, string parameters, string directory, int show);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);
    }
}

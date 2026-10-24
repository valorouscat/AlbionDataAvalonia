using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Runtime.InteropServices;

namespace AlbionDataAvalonia.Views;

/// <summary>Window-local Win32 hotkeys and styles. No keyboard hook or input injection.</summary>
internal sealed class MapOverlayPlatform : IDisposable
{
    private const int ExtendedStyle = -20;
    private const long Layered = 0x80000, Transparent = 0x20, NoActivate = 0x8000000;
    private const long OwnedStyles = Layered | Transparent | NoActivate;
    private const int VisibilityKey = 0xAF01, ModeKey = 0xAF02;
    private const uint Hotkey = 0x0312, Modifiers = 0x0001 | 0x0002 | 0x4000; // Alt, Control, no repeat.
    private readonly Window window;
    private readonly Action toggleVisibility;
    private readonly Action toggleOverlay;
    private nint handle;
    private long originalStyles;
    private bool capturedStyles, registered;

    public MapOverlayPlatform(Window window, Action toggleVisibility, Action toggleOverlay)
    {
        this.window = window;
        this.toggleVisibility = toggleVisibility;
        this.toggleOverlay = toggleOverlay;
        if (OperatingSystem.IsWindows()) Win32Properties.AddWndProcHookCallback(window, WindowProc);
    }

    public bool ShortcutsAvailable => registered;

    // Native opacity affects the entire Windows window, including the map's opaque image background.
    public bool Configure(bool overlay, int opacity, bool clickThrough, out bool passingClicks, out string error)
    {
        passingClicks = false;
        error = "";
        if (!OperatingSystem.IsWindows()) return false;
        handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return false;
        if (!capturedStyles)
        {
            originalStyles = ReadStyles(handle) & OwnedStyles;
            capturedStyles = true;
        }
        // Keep the mode shortcut registered in the normal window too, so it can enter overlay mode.
        if (!registered)
        {
            var visibility = RegisterHotKey(handle, VisibilityKey, Modifiers, 'M');
            var normal = RegisterHotKey(handle, ModeKey, Modifiers, 'O');
            registered = visibility && normal;
            if (!registered)
            {
                if (visibility) UnregisterHotKey(handle, VisibilityKey);
                if (normal) UnregisterHotKey(handle, ModeKey);
                error = "Couldn't register Ctrl+Alt+M and Ctrl+Alt+O. Another app may use them. Click-through stays off; the map remains interactive.";
            }
        }

        var pass = overlay && clickThrough && registered;
        var styles = (ReadStyles(handle) & ~OwnedStyles) | originalStyles;
        if (overlay) styles |= Layered;
        if (pass) styles |= Transparent | NoActivate;
        if (!WriteStyles(handle, styles) || overlay && !SetLayeredWindowAttributes(handle, 0, (byte)Math.Clamp(opacity * 255 / 100, 51, 255), 2))
        {
            WriteStyles(handle, (ReadStyles(handle) & ~OwnedStyles) | originalStyles);
            error = "Windows couldn't apply the overlay window style. Click-through stays off; the map remains interactive.";
            return false;
        }
        SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        if (!overlay && (styles & Layered) != 0) SetLayeredWindowAttributes(handle, 0, 255, 2);
        passingClicks = pass;
        return overlay;
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Hotkey && registered && (wParam == VisibilityKey || wParam == ModeKey))
        {
            handled = true;
            // Changing window visibility/styles from inside its native WndProc can re-enter Avalonia.
            Dispatcher.UIThread.Post(wParam == VisibilityKey ? toggleVisibility : toggleOverlay);
        }
        return 0;
    }

    private void UnregisterShortcuts()
    {
        if (!registered) return;
        UnregisterHotKey(handle, VisibilityKey);
        UnregisterHotKey(handle, ModeKey);
        registered = false;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        UnregisterShortcuts();
        Win32Properties.RemoveWndProcHookCallback(window, WindowProc);
    }

    private static long ReadStyles(nint hwnd) => nint.Size == 8 ? GetWindowLongPtr(hwnd, ExtendedStyle).ToInt64() : GetWindowLong(hwnd, ExtendedStyle);
    private static bool WriteStyles(nint hwnd, long styles)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = nint.Size == 8 ? SetWindowLongPtr(hwnd, ExtendedStyle, (nint)styles) : (nint)SetWindowLong(hwnd, ExtendedStyle, (int)styles);
        return previous != 0 || Marshal.GetLastPInvokeError() == 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}

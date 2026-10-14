using System.Runtime.InteropServices;
using FloatingMemo.Interop;

namespace FloatingMemo.Services;

/// <summary>
/// A system-wide hotkey registered with RegisterHotKey.
/// </summary>
/// <remarks>
/// RegisterHotKey delivers WM_HOTKEY to the given window even while it is hidden,
/// so the memo can be summoned from any app. WinUI 3 has no managed hook for raw
/// window messages, so we subclass the window (SetWindowSubclass) to receive it.
/// </remarks>
internal sealed class GlobalHotkey : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly int _id;
    private readonly uint _modifiers;
    private readonly uint _virtualKey;

    // Must stay referenced for the lifetime of the subclass, or the GC would
    // collect the delegate while native code still calls into it.
    private readonly NativeMethods.SubclassProc _subclassProc;

    private bool _subclassed;
    private bool _registered;

    public event EventHandler? Pressed;

    public GlobalHotkey(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
    {
        _hwnd = hwnd;
        _id = id;
        _modifiers = modifiers | NativeMethods.MOD_NOREPEAT;
        _virtualKey = virtualKey;
        _subclassProc = WindowProc;
    }

    /// <summary>Registers the hotkey. Returns false with the Win32 error code on failure.</summary>
    public bool TryRegister(out int win32Error)
    {
        win32Error = 0;
        if (_registered)
        {
            return true;
        }

        if (!_subclassed)
        {
            _subclassed = NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)_id, UIntPtr.Zero);
        }

        if (!NativeMethods.RegisterHotKey(_hwnd, _id, _modifiers, _virtualKey))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        _registered = true;
        return true;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, _id);
            _registered = false;
        }

        if (_subclassed)
        {
            NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)_id);
            _subclassed = false;
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MediaController
{
    public class HotkeyManager : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;

        private const uint VK_LEFT = 0x25;
        private const uint VK_RIGHT = 0x27;
        private const uint VK_SPACE = 0x20;

        private const int HOTKEY_ID_PREV = 9001;
        private const int HOTKEY_ID_NEXT = 9002;
        private const int HOTKEY_ID_PLAYPAUSE = 9003;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private IntPtr _hWnd;
        private HwndSource? _source;

        public event Action? OnPreviousPressed;
        public event Action? OnNextPressed;
        public event Action? OnPlayPausePressed;

        public void Initialize(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _hWnd = helper.EnsureHandle();

            _source = HwndSource.FromHwnd(_hWnd);
            _source?.AddHook(HwndHook);

            // Register Hotkeys: Ctrl + Alt + Left / Right / Space
            RegisterHotKey(_hWnd, HOTKEY_ID_PREV, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_LEFT);
            RegisterHotKey(_hWnd, HOTKEY_ID_NEXT, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_RIGHT);
            RegisterHotKey(_hWnd, HOTKEY_ID_PLAYPAUSE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                switch (id)
                {
                    case HOTKEY_ID_PREV:
                        OnPreviousPressed?.Invoke();
                        handled = true;
                        break;
                    case HOTKEY_ID_NEXT:
                        OnNextPressed?.Invoke();
                        handled = true;
                        break;
                    case HOTKEY_ID_PLAYPAUSE:
                        OnPlayPausePressed?.Invoke();
                        handled = true;
                        break;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_source != null)
            {
                _source.RemoveHook(HwndHook);
                _source = null;
            }

            if (_hWnd != IntPtr.Zero)
            {
                UnregisterHotKey(_hWnd, HOTKEY_ID_PREV);
                UnregisterHotKey(_hWnd, HOTKEY_ID_NEXT);
                UnregisterHotKey(_hWnd, HOTKEY_ID_PLAYPAUSE);
                _hWnd = IntPtr.Zero;
            }
        }
    }
}

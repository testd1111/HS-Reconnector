using System;
using System.Windows.Input;
using System.Windows.Interop;
using HS_Reconnector.Native;

namespace HS_Reconnector.Core
{
    internal sealed class HotkeyManager : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModNoRepeat = 0x4000;
        private const int HotkeyId = 1;
        private readonly HwndSource messageWindow;

        private bool registered;
        private Key currentKey = Key.None;
        private HotkeyModifiers currentModifiers = HotkeyModifiers.None;

        public event Action Pressed;

        public HotkeyManager()
        {
            messageWindow = new HwndSource(new HwndSourceParameters("HS-Reconnector Hotkey")
            {
                ParentWindow = new IntPtr(-3) // HWND_MESSAGE
            });
            messageWindow.AddHook(HandleMessage);
        }

        public bool Register(Key key, HotkeyModifiers modifiers)
        {
            if (key == Key.None)
            {
                Unregister();
                return true;
            }

            if (key == Key.F12 || modifiers == HotkeyModifiers.None)
            {
                if (key == Key.F12)
                    AppLogger.Error("F12 cannot be registered as a global hotkey because it is reserved by Windows");
                else
                    AppLogger.Error("Refusing to register a modifier-less global hotkey: " + key);
                return false;
            }

            if (registered && currentKey == key && currentModifiers == modifiers)
                return true;

            var hadPrevious = registered;
            var previousKey = currentKey;
            var previousModifiers = currentModifiers;

            Unregister();

            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            registered = Win32.RegisterHotKey(messageWindow.Handle, HotkeyId, (uint)modifiers | ModNoRepeat, (uint)virtualKey);
            if (registered)
            {
                currentKey = key;
                currentModifiers = modifiers;
                return true;
            }

            AppLogger.Error(string.Format("RegisterHotKey failed for {0}+{1}", modifiers, key));
            if (hadPrevious)
            {
                var prevVk = KeyInterop.VirtualKeyFromKey(previousKey);
                if (Win32.RegisterHotKey(messageWindow.Handle, HotkeyId, (uint)previousModifiers | ModNoRepeat, (uint)prevVk))
                {
                    registered = true;
                    currentKey = previousKey;
                    currentModifiers = previousModifiers;
                }
            }

            return false;
        }

        public void Unregister()
        {
            if (!registered)
                return;

            Win32.UnregisterHotKey(messageWindow.Handle, HotkeyId);
            registered = false;
            currentKey = Key.None;
            currentModifiers = HotkeyModifiers.None;
        }

        public void Dispose()
        {
            Unregister();
            messageWindow.Dispose();
        }

        private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (registered && message == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Pressed?.Invoke();
            }
            return IntPtr.Zero;
        }
    }
}

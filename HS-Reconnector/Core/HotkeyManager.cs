using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using HS_Reconnector.Native;

namespace HS_Reconnector.Core
{
    internal sealed class HotkeyManager : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModNoRepeat = 0x4000;

        private bool registered;
        private Key currentKey = Key.None;
        private HotkeyModifiers currentModifiers = HotkeyModifiers.None;
        private ushort hotkeyAtom;
        private int HotkeyId => hotkeyAtom;

        public event Action Pressed;

        public HotkeyManager()
        {
            hotkeyAtom = Win32.GlobalAddAtom(
                "HS-Reconnector-" +
                Process.GetCurrentProcess().Id +
                "-" +
                Guid.NewGuid().ToString("N"));

            if (hotkeyAtom == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;
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
            registered = Win32.RegisterHotKey(IntPtr.Zero, HotkeyId, (uint)modifiers | ModNoRepeat, (uint)virtualKey);
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
                if (Win32.RegisterHotKey(IntPtr.Zero, HotkeyId, (uint)previousModifiers | ModNoRepeat, (uint)prevVk))
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

            Win32.UnregisterHotKey(IntPtr.Zero, HotkeyId);
            registered = false;
            currentKey = Key.None;
            currentModifiers = HotkeyModifiers.None;
        }

        public void Dispose()
        {
            Unregister();
            ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcher_ThreadFilterMessage;

            if (hotkeyAtom != 0)
            {
                Win32.GlobalDeleteAtom(hotkeyAtom);
                hotkeyAtom = 0;
            }
        }

        private void ComponentDispatcher_ThreadFilterMessage(ref MSG msg, ref bool handled)
        {
            if (msg.message == WmHotkey && msg.wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Pressed?.Invoke();
            }
        }
    }
}

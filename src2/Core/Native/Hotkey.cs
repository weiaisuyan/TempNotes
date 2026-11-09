using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TempNotes.Native
{
    /// <summary>全局组合键热键（RegisterHotKey）。</summary>
    public static class Hotkey
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        private static IntPtr _hwnd = IntPtr.Zero;
        private static int _id;
        private static bool _registered;

        /// <summary>解析 "Ctrl+Alt+N" → 修饰位 + 虚拟键码。失败返回 false。</summary>
        public static bool Parse(string combo, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(combo)) return false;
            var parts = combo.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            string last = null;
            foreach (var raw in parts)
            {
                string p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= MOD_CONTROL; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "win": case "windows": mods |= MOD_WIN; break;
                    default: last = p; break;
                }
            }
            if (last == null) return false;
            if (mods == 0) return false;   // 不允许无修饰的裸键做全局热键

            if (last.Length == 1)
            {
                char c = char.ToUpperInvariant(last[0]);
                if (c >= 'A' && c <= 'Z') { vk = (uint)c; return true; }
                if (c >= '0' && c <= '9') { vk = (uint)c; return true; }
                return false;
            }
            if (last.Length >= 2 && (last[0] == 'F' || last[0] == 'f'))
            {
                int n;
                if (int.TryParse(last.Substring(1), out n) && n >= 1 && n <= 24)
                {
                    vk = (uint)(0x70 + n - 1);
                    return true;
                }
            }
            switch (last.ToLowerInvariant())
            {
                case "space": vk = 0x20; return true;
                case "enter": vk = 0x0D; return true;
                case "tab": vk = 0x09; return true;
                case "insert": vk = 0x2D; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "pageup": vk = 0x21; return true;
                case "pagedown": vk = 0x22; return true;
                case "`": case "oem3": vk = 0xC0; return true;
            }
            return false;
        }

        public static bool Register(Window w, string combo)
        {
            Unregister();
            uint mods, vk;
            if (!Parse(combo, out mods, out vk)) return false;
            _hwnd = new WindowInteropHelper(w).Handle;
            if (_hwnd == IntPtr.Zero) return false;
            _id = 0x544E; // 'TN'
            _registered = RegisterHotKey(_hwnd, _id, mods | MOD_NOREPEAT, vk);
            return _registered;
        }

        public static void Unregister()
        {
            if (_registered && _hwnd != IntPtr.Zero)
            {
                try { UnregisterHotKey(_hwnd, _id); } catch { }
            }
            _registered = false;
        }

        public static bool IsRegistered { get { return _registered; } }
    }
}

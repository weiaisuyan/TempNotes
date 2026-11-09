using System;
using Microsoft.Win32;

namespace TempNotes.Native
{
    /// <summary>开机自启：写 HKCU\...\Run，不需要管理员、不弹 UAC。</summary>
    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TempNotes";

        public static string ExePath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    var v = k.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        public static bool SetEnabled(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(ValueName, "\"" + ExePath + "\" --tray", RegistryValueKind.String);
                    else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch { return false; }
        }
    }
}

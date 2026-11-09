using System;
using System.Runtime.InteropServices;

namespace TempNotes.Native
{
    /// <summary>删除到 Windows 回收站，并可查询回收站条目数（用于自检取证）。</summary>
    public static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        /// <summary>返回回收站当前条目数（整机，按盘符逐个累加）。失败返回 -1。</summary>
        public static long GetItemCount()
        {
            long total = 0;
            bool any = false;
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                if (d.DriveType != System.IO.DriveType.Fixed && d.DriveType != System.IO.DriveType.Removable) continue;
                var info = new SHQUERYRBINFO();
                info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
                if (SHQueryRecycleBin(d.Name, ref info) == 0)
                {
                    total += info.i64NumItems;
                    any = true;
                }
            }
            return any ? total : -1;
        }

        /// <summary>只查单个盘符的回收站条目数。失败返回 -1。</summary>
        public static long GetItemCount(string driveRoot)
        {
            var info = new SHQUERYRBINFO();
            info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
            return SHQueryRecycleBin(driveRoot, ref info) == 0 ? info.i64NumItems : -1;
        }

        /// <summary>直接枚举 $Recycle.Bin 下各 SID 目录里的 $R* 条目数——SHQueryRecycleBin 在部分机器上恒返回 0，不可信。</summary>
        public static long CountByDirectory()
        {
            long total = 0;
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                if (d.DriveType != System.IO.DriveType.Fixed) continue;
                string root;
                try { root = System.IO.Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"); }
                catch { continue; }
                if (!System.IO.Directory.Exists(root)) continue;
                try
                {
                    foreach (var sid in System.IO.Directory.GetDirectories(root))
                    {
                        try
                        {
                            foreach (var f in System.IO.Directory.GetFiles(sid, "$R*")) total++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return total;
        }

        /// <summary>在回收站里找指定文件名（原始名）是否存在，用于取证"真的进去了"。</summary>
        public static bool ExistsInRecycleBin(string originalFileName)
        {
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                if (d.DriveType != System.IO.DriveType.Fixed) continue;
                string root;
                try { root = System.IO.Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"); }
                catch { continue; }
                if (!System.IO.Directory.Exists(root)) continue;
                try
                {
                    foreach (var sid in System.IO.Directory.GetDirectories(root))
                    {
                        try
                        {
                            // $R 是内容，$I 是元数据（含原始文件名）。用 Shell COM 太慢，直接读 $I 里的 UTF-16 原名。
                            foreach (var info in System.IO.Directory.GetFiles(sid, "$I*"))
                            {
                                try
                                {
                                    byte[] bytes = System.IO.File.ReadAllBytes(info);
                                    if (bytes.Length < 28) continue;
                                    string name = System.Text.Encoding.Unicode.GetString(bytes, 28, bytes.Length - 28).TrimEnd('\0');
                                    if (string.Equals(System.IO.Path.GetFileName(name), originalFileName,
                                            StringComparison.OrdinalIgnoreCase)) return true;
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>把文件丢进回收站（不弹确认框，不进"永久删除"）。</summary>
        public static void DeleteToRecycleBin(string path)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace TempNotes.Native
{
    /// <summary>
    /// 用 Windows Shell 取缩略图/图标。
    ///
    /// 图标清晰度的关键：SHGetFileInfo + SHGFI_LARGEICON 只有 32×32，
    /// 放大到 80px 一定糊。所以走 SHGetImageList(SHIL_JUMBO) 取系统 256×256 大图标。
    /// </summary>
    public static class ShellThumbnail
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [Flags]
        private enum SIIGBF : uint
        {
            RESIZETOFIT = 0x00,
            BIGGERSIZEOK = 0x01,
            MEMORYONLY = 0x02,
            ICONONLY = 0x04,
            THUMBNAILONLY = 0x08,
            INCACHEONLY = 0x10
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        private static Guid IID_IShellItemImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // ---------------- 缩略图（图片内容 / 视频首帧） ----------------

        public static BitmapSource GetThumbnail(string path, int size, out int hr, bool thumbnailOnly = true)
        {
            hr = -1;
            IShellItemImageFactory factory;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref IID_IShellItemImageFactory, out factory);
            }
            catch (Exception) { return null; }
            if (factory == null) return null;

            IntPtr hbmp = IntPtr.Zero;
            try
            {
                var flags = SIIGBF.BIGGERSIZEOK | (thumbnailOnly ? SIIGBF.THUMBNAILONLY : 0);
                hr = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero) return null;

                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                Marshal.ReleaseComObject(factory);
            }
        }

        // ---------------- 高分辨率文件类型图标 ----------------

        private const int SHIL_LARGE = 0x0;
        private const int SHIL_EXTRALARGE = 0x2;
        private const int SHIL_JUMBO = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(IntPtr hbmImage, IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        private const uint SHGFI_SYSICONINDEX = 0x000004000;
        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>取文件类型图标，优先 256×256（jumbo），逐级降级。放到 80px 也不糊。</summary>
        public static BitmapSource GetFileIcon(string path, int size = 256)
        {
            string ext = System.IO.Path.GetExtension(path);
            string probe = string.IsNullOrEmpty(ext) ? path : "dummy" + ext;

            var info = new SHFILEINFO();
            IntPtr r = SHGetFileInfo(probe, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf(info), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
            if (r == IntPtr.Zero) return FallbackIcon(probe);

            int idx = info.iIcon;

            foreach (var shil in new[] { SHIL_JUMBO, SHIL_EXTRALARGE, SHIL_LARGE })
            {
                var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                IImageList list = null;
                IntPtr hIcon = IntPtr.Zero;
                try
                {
                    if (SHGetImageList(shil, ref iid, out list) != 0 || list == null) continue;
                    if (list.GetIcon(idx, 0x00000001, out hIcon) == 0 && hIcon != IntPtr.Zero)
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(
                            hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                }
                catch { }
                finally
                {
                    if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
                    if (list != null) { try { Marshal.ReleaseComObject(list); } catch { } }
                }
            }
            return FallbackIcon(probe);
        }

        private static BitmapSource FallbackIcon(string path)
        {
            var info = new SHFILEINFO();
            IntPtr r = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(info.hIcon); }
        }
    }
}

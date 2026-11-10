using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows;
using TempNotes;

namespace TempNotes2
{
    /// <summary>
    /// WebView2 ↔ C# 的唯一通道。
    ///
    /// 协议：JS 发纯文本 "命令|参数1|参数2…"（避免在两端各写一遍 JSON 解析），
    /// C# 回推时用 JSON（JS 侧 JSON.parse 天然支持）。
    ///
    /// 这么设计的原因：消息体都很短（id、文本、索引），纯文本分隔比 JSON 少一半出错面；
    /// 而回推的笔记列表是结构化数据，用 JSON 更省事。
    /// </summary>
    public class Bridge
    {
        private readonly Store _store;
        private readonly Action<string> _eval;      // 执行 JS 的回调
        private readonly Action<string> _log;
        private readonly BlockingCollection<Attachment> _thumbQueue = new BlockingCollection<Attachment>();
        private readonly HashSet<string> _thumbQueued = new HashSet<string>();
        private readonly object _thumbLock = new object();

        /// <summary>
        /// 前端分片上传中的文件。大文件（几个 GB）不能塞进一条 base64 消息，
        /// 页面按 2MB 切片传过来，这里顺序写进临时文件，收完再一次性落成附件。
        /// </summary>
        private class Upload
        {
            public string Token;
            public string Name;
            public string TargetId;
            public string TempPath;
            public long Size;
            public long Got;
            public FileStream Fs;
        }
        private readonly Dictionary<string, Upload> _uploads = new Dictionary<string, Upload>();
        /// <summary>
        /// 一批拖入里有几个文件还没收完。收完之前不 PushAll：
        /// 页面每收一个就重渲染一次的话，同批其他文件的上传卡片会被 DOM 重建冲掉。
        /// </summary>
        private int _upBatchRemain;
        // 一次拖入多个文件、目标又是「新建」时，整批共用一个新笔记
        private string _upBatchNoteId = "";
        private readonly List<Attachment> _upThumbPending = new List<Attachment>();

        /// <summary>前端上报附件矩形时回调（由 MainWindow 接到 AttDragHost）。</summary>
        public Action<string> OnAttRects;
        /// <summary>数据目录切换成功后回调（MainWindow 用它重挂缩略图虚拟主机并重新推数据）</summary>
        public Action<string> OnDataRootChanged;
        /// <summary>弹窗/批量模式下整体停用附件热区（热区是原生置顶窗，会浮在 HTML 弹窗上面）</summary>
        public Action<bool> OnHotzones;
        public Action OnAttHide;
        /// <summary>外部文件拖入开始/结束（true=开始）。期间要收起附件热区。</summary>
        public Action<bool> OnExtDrag;
        /// <summary>批量编辑开关。批量模式下整卡点击只做选中，附件热区必须全部撤掉。</summary>
        public Action<bool> OnBatchMode;

        /// <summary>置顶开关（实现由 MainWindow 提供）。</summary>
        public Action<int,int,int,int> OnDragGrip;   // grabY, cardHeight, listTop, listBottom
        public Action OnDragBegin;
        public Action OnDragEnd;
        public Action<bool> OnTopmost;

        /// <summary>开机自启开关（实现由 MainWindow 提供）。</summary>
        public Action<bool> OnAutoStart;

        /// <summary>窗口背景色跟随配色。</summary>
        public Action<string> OnSetBackground;

        /// <summary>主色（accent）跟随配色，标题栏图钉用它做按压/置顶底色。</summary>
        public Action<string> OnSetAccent;

        /// <summary>网页端是否正在编辑/拖拽（v2812）：true 时 C# 推迟磁盘刷新，别打断输入。</summary>
        public Action<bool> OnBusy;

        public Bridge(Store store, Action<string> eval, Action<string> log)
        {
            _store = store;
            _eval = eval;
            _log = log;

            // Shell 缩略图提供程序是 COM，放在 STA 后台线程里最稳；
            // 单线程串行也避免多张大图同时解码把机器拖慢。
            var thumbThread = new Thread(ThumbWorker)
            {
                IsBackground = true,
                Name = "TempNotesThumbs"
            };
            thumbThread.SetApartmentState(ApartmentState.STA);
            thumbThread.Start();
        }

        // ---------------- 接收 JS 消息 ----------------

        public void Handle(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;

            string[] p = raw.Split('|');
            string cmd = p[0];
            // copy / attrects 是高频命令（attrects 每次悬停和滚动都发，copy 带整段文本）。
            // 每条都 File.AppendAllText 会让快速双击明显发卡 —— 这两类不记。
            // upchunk 一条 2MB base64：写进日志会直接把文件和磁盘打爆，绝对不记。
            if (cmd != "copy" && cmd != "attrects" && cmd != "atthide" && cmd != "dbg" && cmd != "upchunk")
                _log("JS→C#  " + raw);

            try
            {
                switch (cmd)
                {
                case "draggrip":
                    {
                        int g0 = 0, h0 = 80, lt0 = 0, lb0 = 660;
                        if (p.Length > 1) int.TryParse(p[1], out g0);
                        if (p.Length > 2) int.TryParse(p[2], out h0);
                        if (p.Length > 3) int.TryParse(p[3], out lt0);
                        if (p.Length > 4) int.TryParse(p[4], out lb0);
                        if (OnDragGrip != null) OnDragGrip(g0, h0, lt0, lb0);
                    }
                    break;

                case "dragbegin":
                    if (OnDragBegin != null) OnDragBegin();
                    break;

                case "dragend":
                    if (OnDragEnd != null) OnDragEnd();
                    if (OnBusy != null) OnBusy(false);
                    break;

                case "busy":
                    // busy|1 用户开始编辑/拖拽  busy|0 结束（v2812）
                    if (p.Length > 1 && OnBusy != null) OnBusy(p[1] == "1");
                    break;

                case "extdrag":
                    // extdrag|1 外部文件开始拖入（收起附件热区）/ 0 结束或取消
                    if (OnExtDrag != null) OnExtDrag(p.Length > 1 && p[1] == "1");
                    break;

                case "upbegin":
                    // upbegin|token|目标笔记id|文件名|字节数
                    UpBegin(p);
                    break;

                case "upbatch":
                    // upbatch|个数：这一批收完才重渲染
                    {
                        int bn = 0;
                        int.TryParse(p.Length > 1 ? p[1] : "0", out bn);
                        _upBatchRemain += Math.Max(0, bn);
                        _upBatchNoteId = "";
                    }
                    break;

                case "upchunk":
                    // upchunk|token|base64（2MB 切片）
                    UpChunk(p);
                    break;

                case "upend":
                    // upend|token → 落成附件
                    UpEnd(p);
                    break;

                case "upcancel":
                    UpCancel(p);
                    break;

                    case "ready":
                        PushAll();
                        break;

                    case "add":
                        // add|<urlencoded 文本> —— 用 encodeURIComponent，文本里的 | 与换行都能过
                        _store.AddNote(p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "", "");
                        _store.SaveAfterPrune();      // 空文本不该留下空卡片
                        PushAll();
                        break;

                    case "update":
                        // update|id|<urlencoded 新文本>
                        if (p.Length > 2)
                        {
                            var n = Find(p[1]);
                            if (n != null)
                            {
                                n.Text = Uri.UnescapeDataString(p[2]);
                                _store.Touch(n); _store.Save(); PushAll();
                                // 页面只在「编辑结束（失焦 / Ctrl+Enter）」时发一次 update，
                                // 所以这里文字为空就是真的清空了 —— 直接判空，不用定时器兜。
                                if (string.IsNullOrWhiteSpace(n.Text)) PruneIfEmpty(n.Id);
                            }
                        }
                        break;

                    case "delete":
                        // delete|id  → 进回收站（status=2），不物理删除
                        if (p.Length > 1)
                        {
                            var n = Find(p[1]);
                            if (n != null) { _store.SetStatus(n, 2); _store.Save(); PushAll(); }
                        }
                        break;

                    case "toggle":
                        // toggle|id  → 待办/完成互换
                        if (p.Length > 1)
                        {
                            var n = Find(p[1]);
                            if (n != null)
                            {
                                bool toTodo = (n.Status == 1);
                                _store.SetStatus(n, toTodo ? 0 : 1);
                                // 两边都排到这个区的第一位：刚完成的那条应该出现在已完成区最上面，
                                // 刚取消完成的回到待办区最上面（用户 2026-11-09 明确要求）。
                                if (toTodo) SortToTop(n, false);
                                else SortToTopDone(n);
                                _store.SaveAfterPrune();   // 空卡片切完成会留下幽灵，一并清掉
                                PushAll();
                            }
                        }
                        break;

                    case "color":
                        // color|id|#RRGGBB（空字符串=清除）
                        if (p.Length > 2)
                        {
                            var n = Find(p[1]);
                            if (n != null) { n.Color = p[2]; _store.Touch(n); _store.Save(); PushAll(); }
                        }
                        break;

                    case "reorder":
                        // reorder|id1,id2,id3…  → 整序写回
                        if (p.Length > 1)
                        {
                            var ids = p[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                            _store.ApplyOrder(ids);
                            // 不回推：前端已是最新顺序，重建 DOM 会让刚拖完的卡片闪一下
                        }
                        break;

                    case "copy":
                        // copy|文本 —— 由 C# 走原生剪贴板（UI 线程 STA）。
                        // 文本里可能含 '|'，所以取第一个 '|' 之后的全部内容，不能只用 p[1]。
                        {
                            int bar = raw.IndexOf('|');
                            CopyTextToClipboard(bar >= 0 ? raw.Substring(bar + 1) : "");
                        }
                        break;

                    case "copyatts":
                        // copyatts|笔记id —— 把这条待办的全部附件当文件列表放进剪贴板
                        if (p.Length > 1) CopyAttachmentsToClipboard(p[1]);
                        break;

                    case "batch":
                        // batch|op|id1,id2,…   op = done|todo|delete
                        if (p.Length > 2) BatchOp(p[1], p[2]);
                        break;

                    case "pick":
                        PickFiles();
                        break;

                    case "newfile":
                        // newfile|文件名|base64   —— JS 拖入时把内容传过来（浏览器拿不到本地路径）
                        if (p.Length > 2) AddFileFromBase64(p[1], p[2]);
                        break;

                    case "dropset":
                        // dropset|目标笔记id(空=新建)|个数|名字|base64|名字|base64…
                        {
                            int cnt = 0;
                            int.TryParse(p.Length > 2 ? p[2] : "0", out cnt);
                            if (p.Length > 3 && cnt > 0) AddFilesFromBase64(p[1], p, 3, cnt);
                        }
                        break;

                    case "attmove":
                        // attmove|附件id|目标笔记id|插入位置(-1=末尾)
                        {
                            int idx = -1;
                            if (p.Length > 3) int.TryParse(p[3], out idx);
                            if (p.Length > 2) MoveAttachment(p[1], p[2], idx);
                        }
                        break;

                    case "trash":
                        PushTrash();
                        break;

                    case "restore":
                        if (p.Length > 1)
                        {
                            var n = Find(p[1]);
                            if (n != null)
                            {
                                _store.SetStatus(n, 0);
                                SortToTop(n, false);   // 恢复到未完成 → 待办区第一位
                                _store.Save(); PushTrash();
                            }
                        }
                        break;

                    case "purge":
                        if (p.Length > 1)
                        {
                            var n = Find(p[1]);
                            if (n != null) { _store.Purge(n); PushTrash(); }
                        }
                        break;

                    case "empttrash":
                        PurgeAllTrash();
                        break;

                    case "orphanscan":
                        ScanOrphans();
                        break;

                    case "orphanpurge":
                        CleanOrphans();
                        break;

                    case "palette":
                        if (p.Length > 1)
                        {
                            int idx;
                            if (int.TryParse(p[1], out idx))
                            {
                                _store.Settings.PaletteIndex = idx;
                                _store.SaveSettings();
                                PushPalette();
                            }
                        }
                        break;

                    case "getstate":
                        PushAll();
                        PushState();
                        break;

                    case "topmost":
                        if (p.Length > 1) OnTopmost(p[1] == "1");
                        break;

                    case "autostart":
                        if (p.Length > 1) OnAutoStart(p[1] == "1");
                        break;

                    case "rightdone":
                        if (p.Length > 1)
                        {
                            _store.Settings.RightDone = p[1] == "1";
                            _store.SaveSettings();
                            PushState();
                        }
                        break;

                    case "pin":
                        // pin|id —— 置顶/取消置顶（只对未完成的待办有效）
                        if (p.Length > 1)
                        {
                            var n = Find(p[1]);
                            if (n != null && n.Status == 0)
                            {
                                n.Pinned = !n.Pinned;
                                SortToTop(n, n.Pinned);   // 置顶 → 置顶区第一位；取消 → 待办区第一位
                                _store.Touch(n);
                                _store.SaveAfterPrune();
                                PushAll();
                            }
                        }
                        break;

                    case "closetotray":
                        if (p.Length > 1)
                        {
                            _store.Settings.CloseToTray = p[1] == "1";
                            _store.SaveSettings();
                            PushState();
                        }
                        break;

                    // 只推设置（设置面板打开时刷新“数据占用”，不重渲染整个列表）
                    case "state":
                        PushState();
                        break;

                    case "pickdataroot":
                        PickDataRoot();
                        break;

                    case "__setbg":
                        // 前端切配色时同步窗口背景（否则窗口边框区域还是旧色）
                        if (p.Length > 1 && OnSetBackground != null) OnSetBackground(p[1]);
                        break;

                    case "__setaccent":
                        // 前端切配色时同步主色（标题栏图钉的悬浮/按压/置顶底色）
                        if (p.Length > 1 && OnSetAccent != null) OnSetAccent(p[1]);
                        break;

                    case "dbg":
                        if (p.Length > 1) _log("JS " + p[1]);
                        break;

                    case "attrects":
                        // attrects|id,x,y,w,h;…   空串=全部收起
                        if (OnAttRects != null) OnAttRects(p.Length > 1 ? p[1] : "");
                        break;

                    case "atthide":
                        // 滚动开始：只收起指针正下方的热区（不写日志，也不打断正在按下的那个）
                        if (OnAttHide != null) OnAttHide();
                        break;

                    case "hotzones":
                        if (OnHotzones != null) OnHotzones(p.Length > 1 && p[1] == "1");
                        break;

                    case "batchmode":
                        // batchmode|1 / 0 —— 批量模式：撤掉全部附件热区，
                        // 否则点缩略图会被原生热区吃掉（打开文件），选不中这一条。
                        if (OnBatchMode != null) OnBatchMode(p.Length > 1 && p[1] == "1");
                        break;

                    case "delatt":
                        // delatt|附件id —— 只从笔记里摘掉，不动原文件（引用模式下绝不能再删源）
                        if (p.Length > 1) RemoveAttachment(p[1]);
                        break;

                    case "open":
                        // open|附件id —— 用系统默认程序打开
                        if (p.Length > 1) OpenAttachment(p[1]);
                        break;

                    case "openurl":
                        // openurl|<urlencoded 链接> —— 笔记里的 Markdown 链接，用默认浏览器打开
                        if (p.Length > 1) OpenUrl(Uri.UnescapeDataString(p[1]));
                        break;

                    case "copyfile":
                        // copyfile|附件id —— 把文件本身放进剪贴板（CF_HDROP），微信 Ctrl+V 直接发
                        if (p.Length > 1) CopyFileToClipboard(p[1]);
                        break;

                    case "reveal":
                        // reveal|附件id —— 在资源管理器里定位
                        if (p.Length > 1) RevealAttachment(p[1]);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log("处理失败 " + cmd + " : " + ex.Message);
            }
        }

        // ---------------- 回推数据 ----------------

        /// <summary>把全部笔记推给前端（量小，每次全量，避免增量同步的坑）。</summary>
        /// <summary>给前端弹一条提示。</summary>
        public void Hint(string text)
        {
            string t = (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
            _eval("window.api.hint(\"" + t + "\");");
        }

        public void PushAll()
        {
            string json = NotesJson();
            _eval("window.api.setNotes(" + json + ");");
        }

        private string NotesJson()
        {
            var list = _store.Db.Notes
                .Where(n => n.Status != 2)                    // 回收站不出现在主列表
                .OrderBy(n => n.Status)                        // 待办在前，已完成沉底
                .ThenByDescending(n => n.SortKey)
                .ThenByDescending(n => n.CreatedAt)
                .Select(ToDto)
                .ToList();

            var ser = new DataContractJsonSerializer(typeof(List<NoteDto>));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, list);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private NoteDto ToDto(Note n)
        {
            return new NoteDto
            {
                id = n.Id,
                text = n.Text ?? "",
                color = n.Color ?? "",
                status = n.Status,
                created = n.CreatedAt,
                updated = n.UpdatedAt,
                sort = n.SortKey,
                pinned = n.Pinned,
                atts = (n.Attachments ?? new List<Attachment>()).Select(a =>
                {
                    bool wantsThumb = a.Kind == "image" || a.Kind == "video";
                    string thumb = ThumbUrl(a);
                    // 缺图时先让页面显示内置类型图标，缩略图在后台串行生成；
                    // 生成完只替换对应附件，不再让 UI 线程等图片解码。
                    if (string.IsNullOrEmpty(thumb) && wantsThumb) QueueThumb(a);
                    return new AttDto
                    {
                        id = a.Id,
                        kind = a.Kind ?? "file",
                        name = a.OrigName ?? "",
                        size = a.Size,
                        thumb = thumb,
                        // 图片/视频的占位用页面内置矢量图标，避免同步取系统大图标。
                        fileIcon = string.IsNullOrEmpty(thumb) && !wantsThumb ? FileIconUrl(a) : "",
                        path = a.StoredPath ?? ""
                    };
                }).ToList()
            };
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_SYSICONINDEX = 0x4000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const int SHIL_JUMBO = 0x4;
        private const int ILD_TRANSPARENT = 0x1;

        private static Guid IID_IImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

        /// <summary>SHGetImageList 返回的系统图标列表；只用到 GetIcon，前面几个方法的顺序必须与原生 vtable 一致。</summary>
        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
            int ReplaceIcon(int i, IntPtr hicon, out int pi);
            int SetOverlayImage(int iImage, int iOverlay);
            int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            int AddMasked(IntPtr hbmImage, int crMask, out int pi);
            int Draw(IntPtr pimldp);
            int Remove(int i);
            int GetIcon(int i, int flags, out IntPtr picon);
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        /// <summary>取 256×256 的 jumbo 系统图标（SHGFI_LARGEICON 只有 32px，放大后必糊）。</summary>
        private static IntPtr JumboIcon(string path, bool useFileAttributes)
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_SYSICONINDEX | (useFileAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
            IntPtr ret = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
            if (ret == IntPtr.Zero) return IntPtr.Zero;

            IImageList list = null;
            try
            {
                Guid iid = IID_IImageList;
                if (SHGetImageList(SHIL_JUMBO, ref iid, out list) != 0 || list == null) return IntPtr.Zero;
                IntPtr hIcon;
                return list.GetIcon(info.iIcon, ILD_TRANSPARENT, out hIcon) == 0 ? hIcon : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
            finally
            {
                if (list != null) { try { Marshal.ReleaseComObject(list); } catch { } }
            }
        }

        /// <summary>把 HICON 按原尺寸取出来、等比缩放到 targetPx 存 PNG（透明底保留）。</summary>
        private void RenderIconPng(IntPtr hIcon, string outPath, int targetPx)
        {
            // Bitmap.FromHicon 不还原 HICON 的 AND 掩码：透明像素会变成不透明纯黑，
            // 页面上就是图标后面顶着一块黑底。WPF 的 CreateBitmapSourceFromHIcon 会按
            // 掩码还原 alpha，存成带透明通道的 PNG。
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(targetPx, targetPx));
                src.Freeze();
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                using (var fs = File.Create(outPath)) enc.Save(fs);
                _log("图标已生成 " + targetPx + "px(透明底) → " + Path.GetFileName(outPath));
            }
            catch (Exception ex) { _log("图标渲染失败 " + ex.Message); }
        }

        private string IconCachePath(Attachment a)
        {
            // v3：旧缓存是不透明黑底，换名字让所有图标重新生成一次
            return Path.Combine(_store.ThumbDir, "ico_v3_" + a.Id + ".png");
        }

        // ---- 视频首帧缩略图 ----
        // 用 Windows Shell 的缩略图提供程序（和资源管理器里看到的那张一样），
        // 比“紫色播放图标”正确得多。只对 local 文件有效，失败就退回系统图标。
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const int SIIGBF_BIGGERSIZEOK = 0x1;
        private const int SIIGBF_THUMBNAILONLY = 0x8;

        private bool MakeShellThumb(Attachment a, int px, bool thumbnailOnly = true)
        {
            try
            {
                if (string.IsNullOrEmpty(a.StoredPath) || !File.Exists(a.StoredPath)) return false;
                string outPath = ThumbPathV2(a);
                if (File.Exists(outPath)) return true;

                Guid iid = typeof(IShellItemImageFactory).GUID;
                IShellItemImageFactory f;
                SHCreateItemFromParsingName(a.StoredPath, IntPtr.Zero, ref iid, out f);
                IntPtr hbm = IntPtr.Zero;
                try
                {
                    int flags = SIIGBF_BIGGERSIZEOK | (thumbnailOnly ? SIIGBF_THUMBNAILONLY : 0);
                    int hr = f.GetImage(new SIZE { cx = px, cy = px },
                        flags, out hbm);
                    if (hr != 0 || hbm == IntPtr.Zero)
                    {
                        _log("视频缩略图失败 hr=" + hr + (thumbnailOnly ? "(仅缩略图)" : "(含图标)") + " " + a.OrigName);
                        return false;
                    }
                    Directory.CreateDirectory(_store.ThumbDir);
                    using (var bmp = System.Drawing.Bitmap.FromHbitmap(hbm))
                        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    _log("视频缩略图已生成 " + a.OrigName);
                    return true;
                }
                finally
                {
                    if (hbm != IntPtr.Zero) DeleteObject(hbm);
                    try { Marshal.ReleaseComObject(f); } catch { }
                }
            }
            catch (Exception ex) { _log("视频缩略图异常 " + ex.Message); return false; }
        }

        /// <summary>
        /// 系统拿不到视频首帧时（典型是没装 HEVC 解码器的 H.265），用 ffmpeg 抽一帧。
        /// 只在这个后台线程里跑，抽不出图就安静失败。
        /// </summary>
        private bool MakeFfmpegThumb(Attachment a, int px)
        {
            try
            {
                if (string.IsNullOrEmpty(a.StoredPath) || !File.Exists(a.StoredPath)) return false;
                string outPath = ThumbPathV2(a);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                // 先取 1 秒（开头常是黑场/台标），再退回第 0 帧
                foreach (double ss in new[] { 1.0, 0.0 })
                {
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    string inv = ss.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    psi.Arguments = "-hide_banner -loglevel error -y -ss " + inv +
                        " -i \"" + a.StoredPath + "\" -frames:v 1 -vf \"scale=" + px +
                        ":-2:flags=lanczos\" -f image2 \"" + outPath + "\"";
                    try
                    {
                        using (var p = System.Diagnostics.Process.Start(psi))
                        {
                            if (p == null) return false;
                            p.BeginOutputReadLine();
                            p.BeginErrorReadLine();
                            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                            else p.WaitForExit();
                        }
                    }
                    catch { return false; }

                    if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                    {
                        _log("视频缩略图已生成(ffmpeg) " + a.OrigName);
                        return true;
                    }
                }
            }
            catch (Exception ex) { _log("视频缩略图 ffmpeg 异常 " + ex.Message); }
            return false;
        }

        /// <summary>
        /// 取系统里该文件类型的关联图标（PDF = Acrobat/WPS、docx = Word），渲染成 PNG 缓存。
        /// 不再自己画图标。
        /// </summary>
        private string FileIconUrl(Attachment a)
        {
            try
            {
                string path = a.StoredPath;
                if (string.IsNullOrEmpty(path)) return "";
                if (!File.Exists(path) && !Directory.Exists(path)) return "";

                string cache = IconCachePath(a);
                if (!File.Exists(cache))
                {
                    Directory.CreateDirectory(_store.ThumbDir);
                    IntPtr h = JumboIcon(path, false);
                    if (h == IntPtr.Zero)
                    {
                        // 拿不到真实路径的就按扩展名取类型图标
                        string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                        h = JumboIcon("file" + ext, true);
                    }
                    if (h == IntPtr.Zero) return "";
                    try { RenderIconPng(h, cache, 160); }
                    finally { DestroyIcon(h); }
                    _log("文件图标 " + Path.GetFileName(path) + " → 已生成(高清)");
                }
                bool ok = File.Exists(cache);
                return ok
                    ? "https://thumbs.local/" + Uri.EscapeDataString(Path.GetFileName(cache))
                    : "";
            }
            catch (Exception ex) { _log("文件图标异常 " + ex.Message); return ""; }
        }

        private string ThumbUrl(Attachment a)
        {
            try
            {
                string p = ThumbPathV2(a);
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    return "https://thumbs.local/" + Uri.EscapeDataString(Path.GetFileName(p));
            }
            catch { }
            return "";
        }

        /// <summary>高清缩略图缓存（不和旧版 180px 缓存混用）</summary>
        private string ThumbPathV2(Attachment a)
        {
            return Path.Combine(_store.ThumbDir, a.Id + "_v2.png");
        }

        // ---------------- 辅助 ----------------

        private Note Find(string id)
        {
            return _store.Db.Notes.FirstOrDefault(n => n.Id == id);
        }

        private Attachment FindAtt(string attId)
        {
            foreach (var n in _store.Db.Notes)
            {
                if (n.Attachments == null) continue;
                var a = n.Attachments.FirstOrDefault(x => x.Id == attId);
                if (a != null) return a;
            }
            return null;
        }

        /// <summary>附件 id → 本地文件路径（拖出用）。</summary>
        public string PathOfAtt(string attId)
        {
            var a = FindAtt(attId);
            return a == null ? "" : (a.StoredPath ?? "");
        }

        public string NoteIdOfAtt(string attId)
        {
            foreach (var n in _store.Db.Notes)
            {
                if (n.Attachments == null) continue;
                if (n.Attachments.Any(a => a.Id == attId)) return n.Id;
            }
            return "";
        }

        /// <summary>
        /// 系统路径拖入（WebView 之外或透明附件热区上）。targetId 为空 = 新建备注；
        /// 有值 = 追加到该备注。这里统一负责生成缩略图，避免两条拖入路径表现不一致。
        /// </summary>
        public void AddDroppedPaths(string[] files, string targetId)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (files == null || files.Length == 0) return;
                Note note = string.IsNullOrEmpty(targetId) ? null : Find(targetId);
                int added = 0;

                foreach (var f in files)
                {
                    if (Directory.Exists(f))
                    {
                        note = note ?? _store.AddNote("", "");
                        if (note.Attachments == null) note.Attachments = new List<Attachment>();
                        note.Attachments.Add(new Attachment
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Kind = "file",
                            OrigName = Path.GetFileName(f),
                            StoredPath = f,
                            Size = 0,
                            Linked = true
                        });
                        _store.Touch(note);
                        added++;
                    }
                    else if (File.Exists(f))
                    {
                        note = note ?? _store.AddNote("", "");
                        var att = _store.AddAttachment(note, f, false);
                        added++;
                    }
                }

                if (added > 0)
                {
                    _store.Save();
                    PushAll();
                    _log("原生拖入完成 n=" + added + " " + sw.ElapsedMilliseconds + "ms");
                    Hint(added > 1 ? ("已加入 " + added + " 个文件") : "已加入文件");
                }
            }
            catch (Exception ex) { _log("拖入失败 " + ex.Message); }
        }

        private void BatchOp(string op, string idsCsv)
        {
            var ids = idsCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var id in ids)
            {
                var n = Find(id);
                if (n == null) continue;
                if (op == "done") _store.SetStatus(n, 1);
                else if (op == "todo") _store.SetStatus(n, 0);
                else if (op == "delete") _store.SetStatus(n, 2);
            }
            _store.Save();
            PushAll();
        }

        private void PurgeAllTrash()
        {
            var trash = _store.Db.Notes.Where(n => n.Status == 2).ToList();
            foreach (var n in trash) _store.Purge(n);
            PushTrash();
        }

        /// <summary>
        /// 扫描 blobs / thumbs 里不属于任何笔记的文件，把数量与体积回给页面。
        /// 页面弹 confirm 后再发 orphanpurge 执行 —— 删除是不可恢复的，必须先让人看见数字。
        /// </summary>
        private void ScanOrphans()
        {
            try
            {
                var files = _store.FindOrphanFiles();
                if (files.Count == 0) { Hint("没有无主附件"); PushState(); return; }
                long bytes = 0;
                foreach (var f in files) { try { bytes += new FileInfo(f).Length; } catch { } }
                _log("无主附件扫描：" + files.Count + " 个 / " + FmtSize(bytes));
                _eval("window.api.orphanInfo(" + files.Count + "," + bytes + ");");
            }
            catch (Exception ex) { Hint("扫描失败：" + ex.Message); }
        }

        private void CleanOrphans()
        {
            try
            {
                long bytes;
                int n = _store.PurgeOrphans(out bytes);
                _log("清理无主附件：" + n + " 个 / " + FmtSize(bytes));
                Hint(n > 0 ? ("已清理 " + n + " 个文件，释放 " + FmtSize(bytes)) : "没有无主附件");
                PushState();
            }
            catch (Exception ex) { Hint("清理失败：" + ex.Message); }
        }

        private static string FmtSize(long total)
        {
            if (total >= 1024L * 1024 * 1024) return (total / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
            if (total >= 1024 * 1024) return (total / 1024.0 / 1024).ToString("0.0") + " MB";
            if (total >= 1024) return (total / 1024.0).ToString("0") + " KB";
            return total + " B";
        }

        /// <summary>把回收站里的笔记推给前端。</summary>
        public void PushTrash()
        {
            var trashNotes = _store.Db.Notes.Where(n => n.Status == 2).ToList();
            var list = trashNotes.OrderByDescending(n => n.UpdatedAt).Select(ToDto).ToList();

            // 回收站占用的空间：只算还能找到的附件实体 + 缩略图（原文件可能已被移走）
            long bytes = 0;
            int files = 0;
            foreach (var n in trashNotes)
            {
                foreach (var a in (n.Attachments ?? new List<Attachment>()))
                {
                    if (a == null) continue;
                    try { if (!string.IsNullOrEmpty(a.StoredPath) && File.Exists(a.StoredPath)) { bytes += new FileInfo(a.StoredPath).Length; files++; } } catch { }
                    try { string t = ThumbPathV2(a); if (File.Exists(t)) bytes += new FileInfo(t).Length; } catch { }
                }
            }
            string meta = "共 " + trashNotes.Count + " 条 · " + files + " 个文件 · " + FmtBytes(bytes);

            var ser = new DataContractJsonSerializer(typeof(List<NoteDto>));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, list);
                _eval("window.api.setTrash(" + Encoding.UTF8.GetString(ms.ToArray()) + "," + JsonStr(meta) + ");");
            }
        }

        private static string FmtBytes(long b)
        {
            if (b >= 1024L * 1024 * 1024) return (b / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
            if (b >= 1024 * 1024) return (b / 1024.0 / 1024).ToString("0.0") + " MB";
            if (b >= 1024) return (b / 1024.0).ToString("0") + " KB";
            return b + " B";
        }

        /// <summary>把字符串塞进 JSON（路径里有反斜杠，必须转义）</summary>
        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>推设置状态（配色、置顶、自启、窗口尺寸、数据目录/占用）。</summary>
        public void PushState()
        {
            var st = _store.Settings;
            string json = "{" +
                "\"palette\":" + st.PaletteIndex + "," +
                "\"topmost\":" + (st.Topmost ? "true" : "false") + "," +
                "\"autoStart\":" + (st.AutoStart ? "true" : "false") + "," +
                "\"rightDone\":" + (st.RightDone ? "true" : "false") + "," +
                "\"closeToTray\":" + (st.CloseToTray ? "true" : "false") + "," +
                "\"dataRoot\":" + JsonStr(_store.Root) + "," +
                "\"dataSize\":" + JsonStr(_store.DataSizeText) + "," +
                "\"winW\":" + (int)st.WinW + "," +
                "\"winH\":" + (int)st.WinH +
                "}";
            _eval("window.api.setState(" + json + ");");
        }

        public void PushPalette()
        {
            _eval("window.api.setPalette(" + _store.Settings.PaletteIndex + ");");
        }

        /// <summary>选一个新数据目录：先把现有数据迁移过去，再切过去（无需重启）。</summary>
        private void PickDataRoot()
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择随记的数据目录（放到 OneDrive 里即可跨端同步）";
                    dlg.ShowNewFolderButton = true;
                    try { dlg.SelectedPath = _store.Root; } catch { }
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                    string msg;
                    string target = dlg.SelectedPath;
                    if (!_store.SwitchRoot(target, out msg))
                    {
                        _log("切换数据目录失败：" + msg);
                        System.Windows.MessageBox.Show("切换失败：" + msg, "随记");
                        return;
                    }
                    _log("数据目录切换 → " + _store.Root + "（" + msg + "）");
                    if (OnDataRootChanged != null) OnDataRootChanged(_store.Root);
                    System.Windows.MessageBox.Show(
                        "数据目录已切换到：\n" + _store.Root + "\n\n" + msg + "\n\n不用重启，已经生效。",
                        "随记");
                }
            }
            catch (Exception ex) { _log("切换数据目录异常 " + ex.Message); }
        }

        /// <summary>打开系统文件选择框，把选中的文件挂到一条新笔记上。</summary>
        private void PickFiles()
        {
            try
            {
                var dlg = new System.Windows.Forms.OpenFileDialog
                {
                    Multiselect = true,
                    Title = "选择要加入笔记的文件"
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                Note last = null;
                foreach (var f in dlg.FileNames)
                {
                    if (!File.Exists(f)) continue;
                    last = last ?? _store.AddNote("", "");
                    var att = _store.AddAttachment(last, f, false);
                }
                if (last != null)
                {
                    _store.Save();
                    PushAll();
                    Hint("已加入 " + dlg.FileNames.Length + " 个文件");
                }
            }
            catch (Exception ex) { _log("选文件失败 " + ex.Message); }
        }

        /// <summary>
        /// 网页拖入的文件：JS 只能拿到内容（浏览器安全策略不允许读本地路径），
        /// 所以把 base64 传过来，落地到 blobs 目录再建附件。
        /// </summary>
        private void AddFileFromBase64(string name, string b64)
        {
            try
            {
                if (string.IsNullOrEmpty(b64)) return;

                var note = _store.AddNote("", "");
                string dir = Path.Combine(_store.BlobDir, note.Id);
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, name);

                byte[] bytes = Convert.FromBase64String(b64);
                File.WriteAllBytes(target, bytes);

                note.Attachments.Add(new Attachment
                {
                    Kind = GuessKind(name),
                    OrigName = name,
                    StoredPath = target,
                    Size = bytes.Length,
                    Linked = false
                });
                _store.Save();
                PushAll();
                Hint("已加入 " + name);
            }
            catch (Exception ex) { _log("拖入失败 " + ex.Message); }
        }

        /// <summary>
        /// 拖进来的多个文件：targetId 为空就新建一条笔记，否则加到那条笔记上。
        /// 顺便给图片生成缩略图（拖入路径以前完全不生成，所以只有图标没有缩略图）。
        /// </summary>
        private void AddFilesFromBase64(string targetId, string[] p, int start, int count)
        {
            try
            {
                Note note = string.IsNullOrEmpty(targetId) ? null : Find(targetId);
                if (note == null) note = _store.AddNote("", "");

                int added = 0;
                for (int i = 0; i < count; i++)
                {
                    int k = start + i * 2;
                    if (k + 1 >= p.Length) break;
                    string name = p[k];
                    string b64 = p[k + 1];
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(b64)) continue;
                    // 只留文件名本体：拖拽源可能带来路径/含斜杠的名字
                    try { name = Path.GetFileName(name.Replace('\\', '/')); } catch { }
                    if (string.IsNullOrEmpty(name)) continue;

                    try
                    {
                        string dir = Path.Combine(_store.BlobDir, note.Id);
                        Directory.CreateDirectory(dir);
                        string target = Path.Combine(dir, name);
                        int n = 1;
                        while (File.Exists(target))
                        {
                            target = Path.Combine(dir,
                                Path.GetFileNameWithoutExtension(name) + " (" + (++n) + ")" + Path.GetExtension(name));
                        }

                        byte[] bytes = Convert.FromBase64String(b64);
                        File.WriteAllBytes(target, bytes);

                        if (note.Attachments == null) note.Attachments = new List<Attachment>();
                        var a = new Attachment
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Kind = GuessKind(name),
                            OrigName = name,
                            StoredPath = target,
                            Size = bytes.Length,
                            Linked = false
                        };
                        note.Attachments.Add(a);
                        added++;
                    }
                    catch (Exception ex) { _log("拖入失败 " + name + "： " + ex.Message); }
                }

                if (added > 0)
                {
                    _store.Touch(note);
                    _store.Save();
                    PushAll();
                    Hint(added > 1 ? ("已加入 " + added + " 个文件") : "已加入文件");
                }
            }
            catch (Exception ex) { _log("批量拖入失败 " + ex.Message); }
        }

        /* ---------------- 大文件分片上传（无大小上限） ----------------
           页面拿到的是 File 对象（浏览器不给本地路径），大文件又不能整块 base64，
           所以按 2MB 切片顺序送过来：临时文件 → 收完搬进 blobs → 建附件。
           失败要把状态和原因回给页面，页面据此把占位块变成红色「失败」。 */

        private void UpBegin(string[] p)
        {
            // upbegin|token|targetId|name|size
            if (p.Length < 5) return;
            string token = p[1];
            if (string.IsNullOrEmpty(token)) return;
            Upload old;
            if (_uploads.TryGetValue(token, out old)) UpCancel(new[] { "upcancel", token });

            string name = p[3] ?? "file";
            try { name = Path.GetFileName(name.Replace('\\', '/')); } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = "file";

            long size = 0;
            long.TryParse(p[4], out size);

            try
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "TempNotes-upload");
                Directory.CreateDirectory(tmpDir);
                string tmp = Path.Combine(tmpDir, token + ".part");
                var u = new Upload
                {
                    Token = token,
                    Name = name,
                    TargetId = p[2] ?? "",
                    TempPath = tmp,
                    Size = size,
                    Fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)
                };
                _uploads[token] = u;
                _log("上传开始 " + name + " " + (size / 1048576.0).ToString("0.0") + "MB");
            }
            catch (Exception ex)
            {
                _log("上传开始失败 " + name + " " + ex.Message);
                _eval("window.api.upFail&&window.api.upFail('" + Js(token) + "','" + Js("无法创建临时文件") + "')");
                UpBatchStep();
            }
        }

        private void UpChunk(string[] p)
        {
            // upchunk|token|base64
            if (p.Length < 3) return;
            Upload u;
            if (!_uploads.TryGetValue(p[1], out u)) return;
            try
            {
                byte[] bytes = Convert.FromBase64String(p[2]);
                u.Fs.Write(bytes, 0, bytes.Length);
                u.Got += bytes.Length;
            }
            catch (Exception ex) { UpFail(u, "写入失败：" + ex.Message); }
        }

        private void UpEnd(string[] p)
        {
            if (p.Length < 2) return;
            Upload u;
            if (!_uploads.TryGetValue(p[1], out u)) return;
            _uploads.Remove(u.Token);

            try
            {
                u.Fs.Flush();
                u.Fs.Dispose();
                u.Fs = null;

                Note note = string.IsNullOrEmpty(u.TargetId) ? null : Find(u.TargetId);
                if (note == null)
                {
                    // 同一批拖入（一次多个文件到输入框/空白处）只新建一条笔记，
                    // 后面的文件都挂到它上面；否则每个文件各生成一条待办。
                    if (!string.IsNullOrEmpty(_upBatchNoteId)) note = Find(_upBatchNoteId);
                    if (note == null)
                    {
                        note = _store.AddNote("", "");
                        _upBatchNoteId = note.Id;
                    }
                }

                string dir = Path.Combine(_store.BlobDir, note.Id);
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, u.Name);
                int n = 1;
                while (File.Exists(target))
                {
                    target = Path.Combine(dir, Path.GetFileNameWithoutExtension(u.Name) +
                        " (" + (++n) + ")" + Path.GetExtension(u.Name));
                }
                File.Move(u.TempPath, target);

                if (note.Attachments == null) note.Attachments = new List<Attachment>();
                var a = new Attachment
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = GuessKind(u.Name),
                    OrigName = Path.GetFileName(target),
                    StoredPath = target,
                    Size = u.Got,
                    Linked = false
                };
                note.Attachments.Add(a);
                _store.Touch(note);
                _store.Save();
                _upThumbPending.Add(a);
                UpBatchStep();
                _log("上传完成 " + u.Name + " " + (u.Got / 1048576.0).ToString("0.0") + "MB");
                _eval("window.api.upDone&&window.api.upDone('" + Js(u.Token) + "','" + Js(a.Id) + "')");
            }
            catch (Exception ex) { UpFail(u, ex.Message); }
        }

        private void UpCancel(string[] p)
        {
            if (p.Length < 2) return;
            Upload u;
            if (!_uploads.TryGetValue(p[1], out u)) return;
            _uploads.Remove(p[1]);
            try { if (u.Fs != null) u.Fs.Dispose(); } catch { }
            try { if (File.Exists(u.TempPath)) File.Delete(u.TempPath); } catch { }
            UpBatchStep();
        }

        private void UpFail(Upload u, string why)
        {
            if (u == null) return;
            if (_uploads.ContainsKey(u.Token)) _uploads.Remove(u.Token);
            try { if (u.Fs != null) u.Fs.Dispose(); } catch { }
            try { if (File.Exists(u.TempPath)) File.Delete(u.TempPath); } catch { }
            _log("上传失败 " + u.Name + " " + why);
            _eval("window.api.upFail&&window.api.upFail('" + Js(u.Token) + "','" + Js(why) + "')");
            Hint("复制失败：" + u.Name);
            UpBatchStep();
        }

        /// <summary>一批收完了才重渲染 + 排缩略图（提前渲染会把同批没传完的卡片冲掉）。</summary>
        private void UpBatchStep()
        {
            if (_upBatchRemain > 0) _upBatchRemain--;
            if (_upBatchRemain > 0) return;
            _upBatchNoteId = "";
            var done = new List<Attachment>(_upThumbPending);
            _upThumbPending.Clear();
            PushAll();
            foreach (var a in done) QueueThumb(a);
            if (done.Count > 0) Hint(done.Count > 1 ? ("已加入 " + done.Count + " 个文件") : "已加入文件");
        }

        /// <summary>塞进单引号 JS 字符串里：转义反斜杠/引号/换行。</summary>
        private static string Js(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>附件重排 / 跨笔记移动。index = -1 表示追加到末尾。</summary>
        private void MoveAttachment(string attId, string toNoteId, int index)
        {
            try
            {
                Note from = null, to = Find(toNoteId);
                Attachment found = null;
                foreach (var n in _store.Db.Notes)
                {
                    if (n.Attachments == null) continue;
                    var a = n.Attachments.FirstOrDefault(x => x.Id == attId);
                    if (a != null) { found = a; from = n; break; }
                }
                if (found == null || to == null) return;

                if (to.Attachments == null) to.Attachments = new List<Attachment>();
                int oldIndex = to.Attachments.IndexOf(found);
                if (from != null && from != to) from.Attachments.Remove(found);
                else if (oldIndex >= 0) to.Attachments.RemoveAt(oldIndex);

                // 同一个列表内移动：index 是按「移除前」的列表算的，移除后要往前挪一位
                if (from == to && oldIndex >= 0 && index > oldIndex) index--;

                if (index < 0 || index > to.Attachments.Count) to.Attachments.Add(found);
                else to.Attachments.Insert(index, found);

                if (from != null) _store.Touch(from);
                _store.Touch(to);
                _store.Save();
                PushAll();
                // 附件被拖走的那条如果既没文字也没附件了，整条删掉
                if (from != null && from != to) PruneIfEmpty(from.Id);
            }
            catch (Exception ex) { _log("附件移动失败 " + ex.Message); }
        }

        /// <summary>
        /// 缩略图后台队列：解码大图、取视频首帧都可能上百毫秒，
        /// 不能占着 WPF UI 线程。串行执行，避免多张图同时抢 CPU。
        /// </summary>
        private void QueueThumb(Attachment source)
        {
            if (source == null || string.IsNullOrEmpty(source.Id)) return;
            lock (_thumbLock)
            {
                // 本次运行只尝试一次；失败就保持文件图标，避免每次 PushAll 重试。
                if (!_thumbQueued.Add(source.Id)) return;
            }

            var a = new Attachment
            {
                Id = source.Id,
                Kind = source.Kind,
                OrigName = source.OrigName,
                StoredPath = source.StoredPath,
                Size = source.Size,
                Linked = source.Linked
            };
            _thumbQueue.Add(a);
        }

        private void ThumbWorker()
        {
            foreach (var a in _thumbQueue.GetConsumingEnumerable())
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    MakeThumb(a);
                    string url = ThumbUrl(a);
                    // 以前这里是 return —— 一条附件没生成出图就把整个后台线程结束了，
                    // 之后所有图片/视频缩略图都不再生成（视频只剩一个大图标）。
                    if (string.IsNullOrEmpty(url)) { _log("缩略图未生成 " + (a.OrigName ?? "")); continue; }
                    string bust = url + (url.IndexOf('?') >= 0 ? "&" : "?") + "v=" + DateTime.UtcNow.Ticks;
                    _eval("window.api.setAttThumb&&window.api.setAttThumb('" + a.Id + "','" + bust + "');");
                    _log("缩略图后台完成 " + (a.OrigName ?? "") + " " + sw.ElapsedMilliseconds + "ms");
                }
                catch (Exception ex) { _log("缩略图后台失败 " + ex.Message); }
            }
        }

        /// <summary>为图片附件生成缩略图（thumbs/&lt;attId&gt;.png，由 thumbs.local 虚拟主机提供）。</summary>
        public void MakeThumb(Attachment a)
        {
            try
            {
                if (a == null) return;
                // 视频：取系统缩略图（首帧）；系统解不了（如未装 HEVC 的 H.265）就让 ffmpeg 抽帧。
                // 两者都失败才退到系统类型图标，页面至少不会只剩一个大播放按钮。
                if (a.Kind == "video")
                {
                    if (MakeShellThumb(a, 256)) return;
                    if (MakeFfmpegThumb(a, 256)) return;
                    MakeShellThumb(a, 256, false);
                    return;
                }
                if (a.Kind != "image") return;
                if (string.IsNullOrEmpty(a.StoredPath) || !File.Exists(a.StoredPath)) return;

                string outPath = ThumbPathV2(a);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                using (var src = System.Drawing.Image.FromFile(a.StoredPath))
                {
                    // 显示尺寸 78px，Windows 150% 缩放后需要 117 个物理像素；
                    // 留到 256 是为了高分屏和以后放大显示都不糊。
                    const int BOX = 256;
                    double scale = Math.Min((double)BOX / src.Width, (double)BOX / src.Height);
                    int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                    int h = Math.Max(1, (int)Math.Round(src.Height * scale));
                    using (var bmp = new System.Drawing.Bitmap(w, h))
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(src, 0, 0, w, h);
                        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                _log("缩略图已生成 " + a.OrigName);
            }
            catch (Exception ex) { _log("缩略图失败 " + ex.Message); }
        }

        private static string GuessKind(string name)
        {
            string e = (Path.GetExtension(name) ?? "").ToLowerInvariant();
            if (e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif" || e == ".bmp" || e == ".webp") return "image";
            if (e == ".mp4" || e == ".mov" || e == ".avi" || e == ".mkv" || e == ".webm") return "video";
            return "file";
        }

        private readonly object _clipLock = new object();

        // ================= 原生剪贴板（不依赖 OLE / 消息泵） =================
        // 为什么不用 System.Windows.Forms.Clipboard：它走 OLE，要求 STA 线程**带消息泵**。
        // 放在没有消息循环的后台线程上，几次之后线程会卡死 —— 表现就是「连点几次复制后
        // 再也不弹提示」。这里直接调 Win32，任何线程都能用。
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, System.Text.StringBuilder lpszFile, uint cch);

        private const uint CF_UNICODETEXT = 13;
        private const uint CF_HDROP = 15;
        private const uint GMEM_MOVEABLE = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct DROPFILES { public uint pFiles; public int x; public int y; public int fNC; public int fWide; }

        /// <summary>把一段文本放进剪贴板（CF_UNICODETEXT），写完回读校验。失败抛异常，交给重试。</summary>
        private static void ClipWriteText(string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (h == IntPtr.Zero) throw new InvalidOperationException("GlobalAlloc 失败");
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); throw new InvalidOperationException("GlobalLock 失败"); }
            Marshal.Copy(bytes, 0, p, bytes.Length);
            GlobalUnlock(h);

            if (!OpenClipboard(IntPtr.Zero))
            {
                GlobalFree(h);
                throw new InvalidOperationException("OpenClipboard 失败 err=" + Marshal.GetLastWin32Error());
            }
            try
            {
                EmptyClipboard();
                if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero)
                    throw new InvalidOperationException("SetClipboardData 失败 err=" + Marshal.GetLastWin32Error());
                h = IntPtr.Zero;      // 成功：所有权归系统，不能再 free
            }
            finally { CloseClipboard(); }

            // 回读校验
            if (!OpenClipboard(IntPtr.Zero)) throw new InvalidOperationException("回读 OpenClipboard 失败");
            try
            {
                IntPtr g = GetClipboardData(CF_UNICODETEXT);
                if (g == IntPtr.Zero) throw new InvalidOperationException("回读为空");
                IntPtr q = GlobalLock(g);
                if (q == IntPtr.Zero) throw new InvalidOperationException("回读 GlobalLock 失败");
                string back = Marshal.PtrToStringUni(q);
                GlobalUnlock(g);
                if (back != text) throw new InvalidOperationException("读回内容不一致");
            }
            finally { CloseClipboard(); }
        }

        /// <summary>把一组文件放进剪贴板（CF_HDROP），粘到微信/资源管理器就是这些文件。</summary>
        private static void ClipWriteFiles(string[] paths)
        {
            var sb = new StringBuilder();
            foreach (var f in paths) sb.Append(f).Append('\0');
            sb.Append('\0');
            byte[] list = Encoding.Unicode.GetBytes(sb.ToString());

            int head = Marshal.SizeOf(typeof(DROPFILES));
            int total = head + list.Length;
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)total);
            if (h == IntPtr.Zero) throw new InvalidOperationException("GlobalAlloc 失败");
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); throw new InvalidOperationException("GlobalLock 失败"); }
            try
            {
                var df = new DROPFILES { pFiles = (uint)head, x = 0, y = 0, fNC = 0, fWide = 1 };
                Marshal.StructureToPtr(df, p, false);
                Marshal.Copy(list, 0, new IntPtr(p.ToInt64() + head), list.Length);
            }
            finally { GlobalUnlock(h); }

            if (!OpenClipboard(IntPtr.Zero))
            {
                GlobalFree(h);
                throw new InvalidOperationException("OpenClipboard 失败 err=" + Marshal.GetLastWin32Error());
            }
            try
            {
                EmptyClipboard();
                if (SetClipboardData(CF_HDROP, h) == IntPtr.Zero)
                    throw new InvalidOperationException("SetClipboardData(HDROP) 失败 err=" + Marshal.GetLastWin32Error());
                h = IntPtr.Zero;
            }
            finally { CloseClipboard(); }

            if (!OpenClipboard(IntPtr.Zero)) throw new InvalidOperationException("回读 OpenClipboard 失败");
            try
            {
                IntPtr g = GetClipboardData(CF_HDROP);
                if (g == IntPtr.Zero) throw new InvalidOperationException("回读为空");
                uint n = DragQueryFileW(g, 0xFFFFFFFF, null, 0);
                if (n != (uint)paths.Length) throw new InvalidOperationException("文件数不一致：" + n + "/" + paths.Length);
            }
            finally { CloseClipboard(); }
        }

        /// <summary>
        /// 剪贴板写操作统一走这里：**独立 STA 线程 + 退避重试**。
        ///
        /// 为什么不能直接调：`OpenClipboard` 会被别的进程短时间独占（微信、输入法、
        /// 浏览器/WebView2 的剪贴板同步都会抢），一抢不到就抛 CLIPBRD_E_CANT_OPEN
        /// (0x800401D0) —— 用户看到的就是「时而成功时而报错」。重试 5 次（15/30/60/120/200ms，
        /// 最坏约 0.4s）基本都能成功；放后台线程是为了不卡界面。
        /// op 返回成功提示文案；抛异常＝这次失败、继续重试。
        /// </summary>
        private void RunClipboardOp(Func<string> op)
        {
            var t = new System.Threading.Thread(() =>
            {
                string okMsg = null, err = null;
                int[] wait = { 15, 30, 60, 120, 200 };
                lock (_clipLock)
                {
                    for (int i = 0; i <= wait.Length; i++)
                    {
                        try { okMsg = op(); err = null; break; }
                        catch (Exception ex) { err = ex.Message; }
                        if (i < wait.Length) { try { System.Threading.Thread.Sleep(wait[i]); } catch { } }
                    }
                }
                try
                {
                    if (err == null) Hint(okMsg);
                    else
                    {
                        _log("剪贴板写入失败（已重试）：" + err);
                        Hint("复制失败：剪贴板被其他程序占用，稍后再试");
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>复制文字：写完回读校验，再由执行方发提示（页面不许自己喊成功）。</summary>
        private void CopyTextToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) { Hint("这条没有可复制的文字"); return; }
            RunClipboardOp(() => { ClipWriteText(text); return "文字已复制"; });
        }

        /// <summary>
        /// 把一条待办里的全部附件当「文件列表」放进剪贴板（粘到微信/资源管理器就是多个文件）。
        /// 附件是引用模式，原文件可能已经被删/移走 —— 这些跳过，并在提示里说清楚少了几个。
        /// </summary>
        private void CopyAttachmentsToClipboard(string noteId)
        {
            try
            {
                var n = Find(noteId);
                if (n == null || n.Attachments == null || n.Attachments.Count == 0)
                { Hint("这条没有可复制的内容"); return; }

                var paths = new List<string>();
                int miss = 0;
                foreach (var a in n.Attachments)
                {
                    if (a == null || string.IsNullOrEmpty(a.StoredPath) || !File.Exists(a.StoredPath)) { miss++; continue; }
                    paths.Add(a.StoredPath);
                }
                if (paths.Count == 0) { Hint(miss + " 个附件都已丢失，无法复制"); return; }

                string okMsg = miss > 0
                    ? ("已复制 " + paths.Count + " 个文件（" + miss + " 个已丢失）")
                    : (paths.Count == 1 ? "文件已复制" : "已复制 " + paths.Count + " 个文件");
                int expect = paths.Count;
                RunClipboardOp(() =>
                {
                    ClipWriteFiles(paths.ToArray());
                    _log("已复制附件到剪贴板 n=" + expect + " miss=" + miss);
                    return okMsg;
                });
            }
            catch (Exception ex) { Hint("复制失败：" + ex.Message); }
        }

        private void CopyFileToClipboard(string attId)
        {
            var a = FindAtt(attId);
            if (a == null || string.IsNullOrEmpty(a.StoredPath)) { Hint("文件不存在"); return; }
            if (!File.Exists(a.StoredPath)) { Hint("文件已被移动或删除"); return; }

            string one = a.StoredPath;
            RunClipboardOp(() =>
            {
                ClipWriteFiles(new[] { one });
                _log("已复制文件到剪贴板: " + one);
                return "已复制文件";
            });
        }

        /// <summary>
        /// 一条待办既没有文字也没有附件时，整条移除（回收站里的不动）。
        /// 调用时机：①编辑结束（失焦）且文字为空；②附件被删；③附件被拖到别的笔记后检查来源那条。
        /// </summary>
        /// <summary>
        /// 把一条笔记排到「它要进去的那个区」的第一位。
        /// Sort 是「越大越靠前」，所以取同区最大 SortKey + 1。
        /// 触发点：①点角标置顶；②取消置顶回到待办区；③已完成/回收站恢复到待办区。
        /// </summary>
        private void SortToTop(Note n, bool pinned)
        {
            try
            {
                long max = 0;
                foreach (var x in _store.Db.Notes)
                {
                    if (x == null || ReferenceEquals(x, n)) continue;
                    if (x.Status != 0) continue;          // 待办区/置顶区都是 status=0
                    if (x.Pinned != pinned) continue;     // 只在同一区里比
                    long k = x.SortKey;
                    if (k > max) max = k;
                }
                n.Sort = max + 1;
            }
            catch { }
        }

        /// <summary>把一条「已完成」的笔记排到已完成区第一位（列表按 Sort 降序，最大 = 最上面）。</summary>
        private void SortToTopDone(Note n)
        {
            try
            {
                long max = 0;
                foreach (var x in _store.Db.Notes)
                {
                    if (x == null || ReferenceEquals(x, n)) continue;
                    if (x.Status != 1) continue;
                    long k = x.SortKey;
                    if (k > max) max = k;
                }
                n.Sort = max + 1;
            }
            catch { }
        }

        private void PruneIfEmpty(string noteId)
        {
            try
            {
                var n = Find(noteId);
                if (n == null) return;
                if (n.Status == 2) return;                       // 回收站里不自动删
                if (!string.IsNullOrWhiteSpace(n.Text)) return;   // 还有文字
                if (n.Attachments != null && n.Attachments.Count > 0) return;  // 还有附件
                _store.Db.Notes.Remove(n);
                // 必须跳过合并：Save() 会先 MergeFromDisk，磁盘上这条还在 → 按 id 加回来，删了等于没删。
                // （这也是以前「空卡片明明该删却一直在」的直接原因。）
                _store.Save(true);
                _log("空待办自动移除 " + noteId);
                PushAll();
            }
            catch { }
        }

        private void RemoveAttachment(string attId)
        {
            foreach (var n in _store.Db.Notes)
            {
                if (n.Attachments == null) continue;
                var a = n.Attachments.FirstOrDefault(x => x.Id == attId);
                if (a == null) continue;
                string ownerId = n.Id;
                _store.DeleteAttachment(n, a, false);   // false：不删原文件
                try { File.Delete(ThumbPathV2(a)); } catch { }
                try { File.Delete(IconCachePath(a)); } catch { }
                _store.Save();
                PushAll();
                PruneIfEmpty(ownerId);
                return;
            }
        }

        private void OpenAttachment(string attId)
        {
            var a = FindAtt(attId);
            if (a == null || string.IsNullOrEmpty(a.StoredPath)) return;
            try { System.Diagnostics.Process.Start(a.StoredPath); } catch { }
        }

        /// <summary>用默认浏览器打开笔记里的 Markdown 链接。只放行 http/https/mailto，
        /// 其它协议（file:、javascript: 等）一律忽略 —— 页面传来的内容不能当命令入口。</summary>
        private void OpenUrl(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                var u = url.Trim();
                bool ok = u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                       || u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
                if (!ok) { _log("拒绝打开非 http(s) 链接: " + u); return; }
                System.Diagnostics.Process.Start(u);
            }
            catch (Exception ex) { _log("打开链接失败 " + ex.Message); }
        }

        private void RevealAttachment(string attId)
        {
            var a = FindAtt(attId);
            if (a == null || string.IsNullOrEmpty(a.StoredPath)) return;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + a.StoredPath + "\"");
            }
            catch { }
        }

        // ---------------- 传输对象 ----------------

        [DataContract]
        public class NoteDto
        {
            [DataMember(Name = "id")] public string id { get; set; }
            [DataMember(Name = "text")] public string text { get; set; }
            [DataMember(Name = "color")] public string color { get; set; }
            [DataMember(Name = "status")] public int status { get; set; }
            [DataMember(Name = "created")] public long created { get; set; }
            [DataMember(Name = "updated")] public long updated { get; set; }
            [DataMember(Name = "sort")] public long sort { get; set; }
            [DataMember(Name = "pinned")] public bool pinned { get; set; }
            [DataMember(Name = "atts")] public List<AttDto> atts { get; set; }
        }

        [DataContract]
        public class AttDto
        {
            [DataMember(Name = "id")] public string id { get; set; }
            [DataMember(Name = "kind")] public string kind { get; set; }
            [DataMember(Name = "name")] public string name { get; set; }
            [DataMember(Name = "size")] public long size { get; set; }
            [DataMember(Name = "thumb")] public string thumb { get; set; }
            [DataMember(Name = "fileIcon")] public string fileIcon { get; set; }
            [DataMember(Name = "path")] public string path { get; set; }
        }
    }
}

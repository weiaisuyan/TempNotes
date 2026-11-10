using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace TempNotes2
{
    /// <summary>
    /// 附件交互层：每个可见附件上面盖一个透明小窗口。
    ///
    /// 为什么必须由 WPF 做：WebView2 里的 JS 无法发起 Windows 原生拖拽（CF_HDROP），
    /// 而「拖到微信输入框」依赖的正是原生文件拖拽。
    ///
    /// 按下 → 移动 → 松开 分四种结局：
    ///   1. 原地单击，落在右上 22×22  → 删除角标（通知页面 delatt）
    ///   2. 原地单击，其余区域        → 打开文件
    ///   3. 拖动，松手在应用窗口内     → 站内拖动：坐标转发给页面做重排/跨笔记移动
    ///   4. 拖动，松手在应用窗口外     → 系统级文件拖拽（拖到微信/资源管理器）
    /// </summary>
    public class AttDragHost
    {
        private class Entry
        {
            public Window Win;
            public string AttId;
            public string Path;
            public Point DownAt;
            public string Mode;          // "" | "js"（站内拖动） | "ext"（系统拖拽） | "badge"（按在删除角标上）
            public bool Dragging;
            public DateTime OutSince;    // 指针开始持续落在窗口外的时刻
            public DateTime ShownAt;
            public double CssX;          // 最近一次页面上报的 CSS 位置
            public double CssY;
            public double PlacedX, PlacedY, PlacedW, PlacedH;   // 上次真正落到屏幕上的位置
            public double ScrL, ScrT, ScrR, ScrB;               // 上次落到屏幕上的物理像素矩形（命中判定用，免去每次 GetWindowRect）
            public bool Raised;          // 已经抬到主窗口之上（不用每帧重复抬）
            public bool EverRaised;      // 这辈子至少抬过一次（首次要重写 Topmost 样式）
            public bool WinShown;        // WPF 层面 Show() 过一次（首帧要建 HWND，很贵）
            public IntPtr Hwnd;          // 缓存 HWND，之后显隐/定位全走原生 API
            public bool Parked;          // 用 ShowWindow(SW_HIDE) 收起来了（WPF 那边仍是 Visible）
            public double CssW, CssH;    // 最近一次上报的 CSS 尺寸
            public DateTime UnseenAt;    // 什么时候开始不在页面上报了（拆窗要等一会儿）
            public bool Hover;           // 指针在小窗上（页面据此显示删除角标）
            public bool Press;           // 左键按着（页面据此给「按下」反馈）
            public bool BadgePress;      // 按在删除角标上
            /// <summary>
            /// 主窗口不是前台时，只为了「悬停预览」临时亮起来的。
            /// 光标离开或本程序被别的程序盖住时必须立刻收回去。
            /// </summary>
            public bool HoverOnly;
        }

        /// <summary>删除角标热区边长（CSS 像素，锚在附件方块右上角）</summary>
        private const double BADGE = 22;
        /// <summary>角标视觉上相对方块内缩 3px，判定区要一起内缩，否则边缘 3px 点不中</summary>
        private const double BADGE_INSET = 3;

        private readonly Window _owner;
        private readonly FrameworkElement _webHost;
        private readonly Func<string, string> _pathOf;
        private readonly Action<string> _log;
        private readonly Action<string> _eval;
        private readonly Action<string> _onDelete;
        private readonly Action<string> _onCopy;
        private readonly Action<string> _onReveal;
        private readonly Action<string[], string> _onDropFiles;
        private readonly bool _suppressOpen;
        private readonly Dictionary<string, Entry> _live = new Dictionary<string, Entry>();
        private bool _noHotzones;   // 批量模式下整体停用热区
        /// <summary>性能埋点：只在真的慢（>8/10ms）时写一行日志，平时零成本。</summary>
        private readonly Stopwatch _perfWatch = new Stopwatch();
        private static long _initTicks;   // SourceInitialized 内部耗时（建窗时要拆开看）
        private readonly uint _pid = (uint)Process.GetCurrentProcess().Id;
        /// <summary>
        /// 本程序自己的所有进程：主进程 + WebView2 的浏览器/渲染进程（msedgewebview2.exe）。
        /// 页面区域的命中结果属于渲染进程，不是主进程 —— 只比主进程 pid 会永远判成「不是自己」。
        /// </summary>
        private HashSet<uint> _ownPids = new HashSet<uint>();
        private DateTime _ownPidsAt = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer _refreshTimer;
        private System.Windows.Threading.DispatcherTimer _hoverTimer;
        /// <summary>
        /// 主窗口正在被拖动/改尺寸。热区是 AllowsTransparency 的分层窗口（软件合成），
        /// 每帧跟着 SetWindowPos 会把这 5~20 个窗口整块重绘，主窗口就被拖得发沉。
        /// 移动期间用户根本不可能点附件，直接整批藏起来，松手再按新坐标显示。
        /// </summary>
        private bool _ownerMoving;
        /// <summary>
        /// 松手后「延后恢复」窗口期：连续快速拖动时，每一段拖动结束都立刻 Show()
        /// 5~20 个分层透明窗 + 逐个 SetWindowPos，会把 UI 线程排满，下一段拖动就追不上鼠标。
        /// 现在改成手停下来 320ms 后一次性恢复；期间又开始拖就直接取消，热区保持隐藏。
        /// </summary>
        private bool _settlePending;
        /// <summary>
        /// 外部文件正被拖进本程序（OLE 拖拽进行中）。
        /// 期间必须把热区全部收起来：它们是 Topmost 的分层窗，会抢掉 OLE 的命中和落点，
        /// 页面也就收不到连续的 dragover（占位预览会卡住不动），拖动本身也会发涩。
        /// </summary>
        private bool _extDrag;
        /// <summary>
        /// 页面里正在拖待办条。期间热区必须整批保持收起，且悬停计时器不许把它们叫回来 ——
        /// 热区是 Topmost 独立窗，一旦在拖动路径上复活就会吃掉 mousemove，
        /// 表现就是「拖着拖着卡住/一卡一卡」。
        /// </summary>
        private bool _pageDrag;
        private System.Windows.Threading.DispatcherTimer _settleTimer;
        /// <summary>兜底：万一没收到 WM_EXITSIZEMOVE，左键一抬起就自己恢复，不能把热区一直憋着。</summary>
        private System.Windows.Threading.DispatcherTimer _moveWatch;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(
            IntPtr h, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(
            IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        // 批量摆窗：一次提交代替 N 次独立 SetWindowPos（收起十几个热区时省掉 20~30ms）
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr BeginDeferWindowPos(int n);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr DeferWindowPos(
            IntPtr hdwp, IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EndDeferWindowPos(IntPtr hdwp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W pe);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W pe);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x00000002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOCOPYBITS = 0x0100;
        private const uint SWP_NOREDRAW = 0x0008;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        public AttDragHost(Window owner, FrameworkElement webHost, Func<string, string> pathOf,
                           Action<string> log, Action<string> eval, Action<string> onDelete,
                           Action<string> onCopy, Action<string> onReveal,
                           Action<string[], string> onDropFiles, bool suppressOpen = false)
        {
            _owner = owner;
            _webHost = webHost;
            _pathOf = pathOf;
            _log = log;
            _eval = eval;
            _onDelete = onDelete;
            _onCopy = onCopy;
            _onReveal = onReveal;
            _onDropFiles = onDropFiles;
            _suppressOpen = suppressOpen;

            _owner.LocationChanged += (s, e) => SyncToOwner();
            _owner.SizeChanged += (s, e) => SyncToOwner();
            _owner.Activated += (s, e) =>
            {
                // 激活会让窗口 z 序重排，标记成「需要再抬一次」，但只抬一次
                foreach (var kv in _live) kv.Value.Raised = false;
                // 按着热区时（点附件顺手把窗口抬到前台）不做页面重报：
                // 重报会跑一次 Update，正在拖的那个热区一旦被关掉就丢了鼠标捕获，
                // 松手事件再也送不到 —— 页面里的拖动幽灵就永远卡在列表上。
                if (!AnyPressed()) SchedulePageRefresh();
            };
            // 热区是独立置顶窗；点了它同样会把主窗口 Deactivated。
            // 若这时 HideAll，正在按下的那个热区会连同兄弟一起消失，
            // 表现为“按住缩略图毫无反应”。所以先判断新前台是不是自家热区。
            _owner.Deactivated += (s, e) =>
            {
                // 点热区时 Windows 会先给主窗口发 WA_INACTIVE，再送鼠标按下，
                // 顺序上比 MouseLeftButtonDown 还早，没法用「是否按住」判断。
                // 用光标位置判断最可靠：按在热区上就说明这次点击属于附件，
                // 点别的程序时光标一定不在任何热区矩形里。
                bool onHot = CursorInHotspot() || ForegroundIsHotspot();
                if (!onHot) HideAll("窗口失焦");
            };
            _owner.Closed += (s, e) =>
            {
                try { if (_hoverTimer != null) _hoverTimer.Stop(); } catch { }
                foreach (var kv in _live)
                {
                    try { kv.Value.Win.Close(); } catch { }
                }
                _live.Clear();
            };

            EnsureHoverTimer();
        }

        public void Update(string payload)
        {
            // 主窗口正在被拖动/收尾：这一整轮原生窗操作全部跳过。
            // 页面在移动过程中仍可能因为滚动/布局重报矩形，哪怕只是给十几个
            // 已藏起来的窗逐个 Park/Close，也要在 UI 线程上跑几十毫秒 ——
            // 系统移动循环被卡住，窗口就会一顿一顿地追鼠标。
            // 坐标不会丢：拖动结束 320ms 后的 RestoreAfterMove 会让页面重新全量上报。
            if (_ownerMoving || _settlePending) return;
            if (string.IsNullOrEmpty(payload)) { HideAll("页面没有可见附件"); return; }
            // 批量模式：一条热区都不建，页面自己接管全部点击（只做选中）
            if (_noHotzones) return;

            var seen = new HashSet<string>();
            int shownNow = 0;
            _perfWatch.Restart();
            long tCreate = 0, tPlace = 0, tShow = 0, tShowFirst = 0, tRaise = 0, tClose = 0, tMark;
            int firstShows = 0;
            // 主窗口不在前台时绝不能显示热区：它们是 Topmost 的独立窗口，
            // 否则会漂在别的程序上面，用户点别的程序却砸到看不见的附件层。
            // 主窗口移动期间同理：保持隐藏，等手停下来后重新上报再显示。
            bool canShow = !_ownerMoving && !_settlePending && !_extDrag && !_pageDrag && _owner.IsVisible && (_owner.IsActive || AnyPressed());
            foreach (var part in payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(',');
                if (f.Length < 5) continue;

                string id = f[0];
                double x, y, w, h;
                if (!double.TryParse(f[1], out x)) continue;
                if (!double.TryParse(f[2], out y)) continue;
                if (!double.TryParse(f[3], out w)) continue;
                if (!double.TryParse(f[4], out h)) continue;
                if (w < 8 || h < 8) continue;

                seen.Add(id);
                Entry e;
                if (!_live.TryGetValue(id, out e))
                {
                    // 移动/收尾期间不新建热区窗：建 HWND + 分层表面很贵，
                    // 这时候建出来也只会被立刻藏起来，纯浪费。
                    if (!canShow) continue;
                    tMark = _perfWatch.ElapsedTicks;
                    e = new Entry { AttId = id };
                    e.Win = CreateWindow(e);
                    _live[id] = e;
                    tCreate += _perfWatch.ElapsedTicks - tMark;
                }
                e.Path = _pathOf(id);
                e.CssX = x;
                e.CssY = y;
                // WPF 的 Width/Height 只在建窗时设一次（设一次就是一轮布局 + SetWindowPos），
                // 之后尺寸变化全交给 Place() 的原生 SetWindowPos。
                if (!e.WinShown)
                {
                    if (double.IsNaN(e.Win.Width) || Math.Abs(e.Win.Width - w) > 0.5) e.Win.Width = w;
                    if (double.IsNaN(e.Win.Height) || Math.Abs(e.Win.Height - h) > 0.5) e.Win.Height = h;
                }
                e.CssW = w; e.CssH = h;
                if (!canShow)
                {
                    // 悬停预览中的那一个先留着：页面在非激活状态下也可能重报矩形，
                    // 一旦顺手 Hide() 就会把鼠标头顶的小窗拆掉（MouseLeave → 角标消失），
                    // 110ms 后又被悬停计时器叫回来 —— 视觉上就是角标一闪一闪。
                    if (e.HoverOnly && e.Hover) continue;
                    // 正按着的那一个同样不能藏：藏窗＝丢捕获＝松手收不到。
                    if (IsBusy(e)) continue;
                    // 藏着的窗口不做布局/摆放；坐标已经记下来了，恢复时一次性摆好。
                    Park(e);
                    continue;
                }
                if (!e.WinShown)
                {
                    tMark = _perfWatch.ElapsedTicks;
                    EnsureShownOnce(e);
                    long dtFirst = _perfWatch.ElapsedTicks - tMark;
                    tShowFirst += dtFirst;
                    firstShows++;
                }
                tMark = _perfWatch.ElapsedTicks;
                Place(e, x, y);
                tPlace += _perfWatch.ElapsedTicks - tMark;
                if (e.Parked)
                {
                    tMark = _perfWatch.ElapsedTicks;
                    Unpark(e);
                    tShow += _perfWatch.ElapsedTicks - tMark;
                    shownNow++;
                    e.HoverOnly = false;
                    e.ShownAt = DateTime.Now;
                }
                e.UnseenAt = DateTime.MinValue;
                if (!e.Raised)
                {
                    tMark = _perfWatch.ElapsedTicks;
                    RaiseAboveOwner(e);
                    tRaise += _perfWatch.ElapsedTicks - tMark;
                    e.Raised = true;
                }
            }

            // 建一个热区要 50~80ms，拆掉下次滚回来又要重建 —— 所以滚出视口不立刻拆，
            // 先原地留着（收起状态，几乎不占资源），超过 1.5s 没再出现才 Close。
            var dead = new List<string>();
            var nowAt = DateTime.Now;
            foreach (var kv in _live)
            {
                var e0 = kv.Value;
                if (seen.Contains(kv.Key)) { e0.UnseenAt = DateTime.MinValue; continue; }
                if (IsBusy(e0)) continue;
                if (e0.UnseenAt == DateTime.MinValue) { e0.UnseenAt = nowAt; continue; }
                if ((nowAt - e0.UnseenAt).TotalMilliseconds > 1500) dead.Add(kv.Key);
            }
            // 上限保护：附件特别多时，超出 80 个就把最久没露面的拆掉。
            if (_live.Count - dead.Count > 80)
            {
                foreach (var kv in _live)
                {
                    if (_live.Count - dead.Count <= 80) break;
                    if (seen.Contains(kv.Key) || IsBusy(kv.Value) || dead.Contains(kv.Key)) continue;
                    if (kv.Value.UnseenAt == DateTime.MinValue) continue;
                    dead.Add(kv.Key);
                }
            }
            tMark = _perfWatch.ElapsedTicks;
            // 一次拆一批也是几十毫秒（Close 一个 ~10ms）。每轮最多拆 2 个，
            // 剩下的下一轮再拆 —— 热区本来就是隐藏的，晚拆一会儿没任何影响。
            int closedNow = 0;
            foreach (var id in dead)
            {
                if (closedNow >= 2) break;
                try { _live[id].Win.Close(); } catch { }
                _live.Remove(id);
                closedNow++;
            }
            tClose += _perfWatch.ElapsedTicks - tMark;

            // 滚动停手后一次性恢复十几个分层窗是这条链路里最贵的一步（每个 Show +
            // Topmost 都要动 DWM）。超过 10ms 就记一笔，卡顿时可以直接对数字。
            if (_perfWatch.ElapsedMilliseconds >= 10)
                _log("热区定位 n=" + _live.Count + " 显示=" + shownNow + " 耗时=" + _perfWatch.ElapsedMilliseconds + "ms" +
                     " [create=" + Ms(tCreate) + " place=" + Ms(tPlace) + " show=" + Ms(tShow) +
                     " 首帧=" + firstShows + "/" + Ms(tShowFirst) +
                     " raise=" + Ms(tRaise) + " close=" + Ms(tClose) + "]");
        }

        private static double Ms(long ticks)
        {
            return Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 1);
        }

        public void HideAll(string why)
        {
            int revealed = 0;
            var toPark = new List<Entry>();
            foreach (var kv in _live)
            {
                var e = kv.Value;
                if (e.Win == null) continue;
                bool visible = !e.Parked && e.Win.IsVisible;
                if (visible) revealed++;
                // 站内拖动进行中被强制收起（失焦/窗口移动/矩形消失），MouseLeftButtonUp
                // 就送不到了：页面里的 .att-ghost 会一直浮在列表上，列表还能滚它却不动。
                // 收起来之前先告诉页面「这次拖动作废」，让幽灵走回槽位然后销毁。
                if (e.Mode == "js")
                {
                    try { SafeEval("window.api.attDragEnd('" + e.AttId + "',null,null)"); } catch { }
                    e.Dragging = false;
                    e.Mode = "";
                    try { e.Win.ReleaseMouseCapture(); } catch { }
                }
                // 下面三个 setter 自带「状态没变就直接 return」，藏过一次之后再调是零成本。
                SetHover(e, false);
                SetPress(e, false);
                SetBadge(e, false);
                e.HoverOnly = false;
                // 只有真的可见才 Hide：连续拖动时重复 Hide 会反复拆/建 DWM 分层表面。
                if (visible) toPark.Add(e);
            }
            if (revealed > 0) _log("隐藏附件热区（" + why + "）n=" + revealed);
            ParkMany(toPark, why);
        }

        /// <summary>
        /// 批量收起热区：DeferWindowPos 把所有窗的移动攒成一次提交。
        /// 旧写法逐个 SetWindowPos（自记约 1.8ms/个），十几个热区就是 20~30ms，
        /// 全压在「按下标题栏」那一帧上，起手会先顿一下。批量失败则逐个兜底。
        /// </summary>
        private void ParkMany(List<Entry> list, string why)
        {
            if (list == null || list.Count == 0) return;
            foreach (var e in list) { e.Parked = true; e.PlacedX = double.NaN; e.PlacedY = double.NaN; }

            long t0 = Stopwatch.GetTimestamp();
            bool batched = false;
            if (list.Count > 1)
            {
                IntPtr hdwp = BeginDeferWindowPos(list.Count);
                if (hdwp != IntPtr.Zero)
                {
                    bool ok = true;
                    foreach (var e in list)
                    {
                        if (e.Hwnd == IntPtr.Zero) continue;
                        int w = e.PlacedW >= 1 ? (int)e.PlacedW : 1;
                        int h = e.PlacedH >= 1 ? (int)e.PlacedH : 1;
                        IntPtr next = DeferWindowPos(hdwp, e.Hwnd, IntPtr.Zero, -32000, -32000, w, h,
                            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW);
                        if (next == IntPtr.Zero) { ok = false; break; }
                        hdwp = next;
                    }
                    if (ok && EndDeferWindowPos(hdwp)) batched = true;
                }
            }
            if (!batched) foreach (var e in list) ParkNow(e);

            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms >= 8)
                _log("热区收起（" + why + "）n=" + list.Count + " 耗时=" + Math.Round(ms, 1) + "ms" +
                     (batched ? " 批量提交" : " 逐个兜底"));
        }

        /// <summary>把单个窗口挪到屏幕外（不做 Parked 判定，供批量路径与兜底使用）。</summary>
        private void ParkNow(Entry e)
        {
            if (e == null || e.Hwnd == IntPtr.Zero) return;
            try
            {
                SetWindowPos(e.Hwnd, IntPtr.Zero, -32000, -32000,
                    (int)e.PlacedW, (int)e.PlacedH,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW);
            }
            catch { }
        }

        /// <summary>
        /// 页面检测到「外部文件拖进来 / 松开或离开」。
        /// 开始：立刻收起全部热区，让 OLE 拖拽的命中落到 WebView2 页面上（预览才能跟着指针走）；
        /// 结束：等页面把新的矩形报上来再恢复。
        /// </summary>
        public void SetExternalDrag(bool on)
        {
            if (_extDrag == on) return;
            _extDrag = on;
            if (on) HideAll("外部拖入");
            else
            {
                // 松手后页面会重渲染，稍等一帧再要矩形，避免拿到重建前的旧位置。
                SchedulePageRefresh();
            }
        }

        /// <summary>
        /// 批量编辑模式：整卡点击只做「选中这一条」，附件热区必须整体撤掉。
        /// 热区是盖在缩略图上的独立原生窗口，点击根本到不了页面 ——
        /// 不撤掉的话点缩略图会去打开文件，而不是选中这条待办。
        /// </summary>
        public void SetNoHotzones(bool on)
        {
            if (_noHotzones == on) return;
            _noHotzones = on;
            if (on) HideAll("批量模式");
            else SchedulePageRefresh();
        }

        /// <summary>
        /// 主窗口进入「移动/缩放」模态循环（WM_ENTERSIZEMOVE）。
        /// 期间不做任何跟随：既藏起热区，也丢掉 LocationChanged 带来的全部重排开销。
        /// </summary>
        public void BeginOwnerMove()
        {
            // 上一段拖动还在「延后恢复」窗口里：直接取消恢复，热区保持藏着。
            try { if (_settleTimer != null) _settleTimer.Stop(); } catch { }
            _settlePending = false;
            if (_ownerMoving) return;
            _ownerMoving = true;
            try { if (_refreshTimer != null) _refreshTimer.Stop(); } catch { }
            HideAll("窗口移动");
            EnsureMoveWatch();
            try { _moveWatch.Start(); } catch { }
        }

        /// <summary>
        /// 主窗口结束移动（WM_EXITSIZEMOVE）。这里只登记「待恢复」，真正的
        /// Show + 摆位交给 320ms 后的 RestoreAfterMove（连续拖动时会被下一次拖动取消）。
        /// </summary>
        public void EndOwnerMove()
        {
            if (!_ownerMoving) return;
            _ownerMoving = false;
            try { if (_moveWatch != null) _moveWatch.Stop(); } catch { }
            // 关键：不在这里立刻 Show。连续快速拖动时，每段收尾都要把 5~20 个分层透明窗
            // 重新 Show + SetWindowPos + 重排，UI 线程被排满，下一段拖动就追不上鼠标。
            // 先记成「待恢复」，手停下来 320ms 再一次性恢复；期间又拖就取消。
            _settlePending = true;
            EnsureSettleTimer();
            try { _settleTimer.Stop(); _settleTimer.Start(); } catch { }
        }

        private void EnsureSettleTimer()
        {
            if (_settleTimer != null) return;
            _settleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(320)
            };
            _settleTimer.Tick += (s, ev) =>
            {
                try
                {
                    _settleTimer.Stop();
                    if (!_settlePending || _ownerMoving) return;
                    RestoreAfterMove();
                }
                catch { }
            };
        }

        private void RestoreAfterMove()
        {
            _settlePending = false;
            foreach (var kv in _live)
            {
                var e = kv.Value;
                try
                {
                    if (e.Win == null) continue;
                    e.PlacedX = double.NaN; e.PlacedY = double.NaN;
                    Place(e, e.CssX, e.CssY);
                }
                catch { }
            }
            SchedulePageRefresh();
        }

        /// <summary>
        /// 页面正在拖待办条（dragbegin/dragend）。期间热区整批收起，
        /// 连悬停计时器也不许把它们叫回来。
        /// </summary>
        public void SetPageDrag(bool on)
        {
            if (_pageDrag == on) return;
            _pageDrag = on;
            if (on)
            {
                try { if (_refreshTimer != null) _refreshTimer.Stop(); } catch { }
                HideAll("拖动待办");
            }
            else
            {
                SchedulePageRefresh();
            }
        }

        private void EnsureMoveWatch()
        {
            if (_moveWatch != null) return;
            _moveWatch = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(220)
            };
            _moveWatch.Tick += (s, ev) =>
            {
                try
                {
                    if (!_ownerMoving) { _moveWatch.Stop(); return; }
                    if ((GetAsyncKeyState(0x01) & 0x8000) != 0) return;   // 左键还按着＝正常拖动中
                    _log("窗口移动兜底恢复（没收到 WM_EXITSIZEMOVE）");
                    EndOwnerMove();
                }
                catch { }
            };
        }

        /// <summary>
        /// 列表滚动期间：把全部附件热区收起（一次滚动只做一次，滚停后统一恢复）。
        ///
        /// 热区是贴在「屏幕坐标」上的独立窗口，页面滚动时它们不会跟着列表走，
        /// 表现为摸起来就是「缩略图飘在原地 / 文件卡在列表里」。页面自己画的那张
        /// 缩略图就在下面同一位置，藏掉热区视觉上没有任何差别。
        ///
        /// 以前只在光标正下方藏一个，其余热区继续留在旧坐标 —— 既难看，滚轮
        /// 打到这些旧窗口上时又会被吃掉，变成「页面不滚、缩略图跳」。
        /// </summary>
        public void HideAllForScroll()
        {
            if (AnyPressed()) return;
            int n = 0;
            _perfWatch.Restart();
            foreach (var kv in _live)
            {
                var e = kv.Value;
                if (e.Win == null || e.Parked || !e.Win.IsVisible) continue;

                // 三个 setter 自带「状态没变就 return」，未按住时后两个是零成本。
                SetHover(e, false);
                SetPress(e, false);
                SetBadge(e, false);
                if (!e.Parked) { Park(e); n++; }
            }
            if (n > 0 && _perfWatch.ElapsedMilliseconds >= 8)
                _log("滚动收起热区 n=" + n + " 耗时=" + _perfWatch.ElapsedMilliseconds + "ms");
        }

        /// <summary>滚轮事件落在热区上：先藏掉这一个，后续滚轮就交给页面原生处理。</summary>
        private void HideForWheel(Entry e)
        {
            if (e == null || e.Win == null) return;
            SetHover(e, false);
            SetPress(e, false);
            SetBadge(e, false);
            Park(e);
        }

        /// <summary>页面里的删除角标只在「指针真的在小窗上」时显示。</summary>
        private void SetHover(Entry e, bool on)
        {
            if (e == null || e.Hover == on) return;
            e.Hover = on;
            try { _eval("window.api.attHover&&window.api.attHover('" + e.AttId + "'," + (on ? "true" : "false") + ");"); }
            catch { }
        }

        /// <summary>「按住了」的视觉反馈（缩小 + 主色描边）。</summary>
        private void SetPress(Entry e, bool on)
        {
            if (e == null || e.Press == on) return;
            e.Press = on;
            try { _eval("window.api.attPress&&window.api.attPress('" + e.AttId + "'," + (on ? "true" : "false") + ");"); }
            catch { }
        }

        private void SetBadge(Entry e, bool on)
        {
            if (e == null || e.BadgePress == on) return;
            e.BadgePress = on;
            try { _eval("window.api.attBadge&&window.api.attBadge('" + e.AttId + "'," + (on ? "true" : "false") + ");"); }
            catch { }
        }

        private void EnsureHoverTimer()
        {
            if (_hoverTimer != null) return;
            _hoverTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(110)
            };
            _hoverTimer.Tick += (s, ev) =>
            {
                try
                {
                    if (_live.Count == 0) return;
                    if (!_owner.IsVisible || _owner.WindowState == WindowState.Minimized) return;
                    if (_ownerMoving || _settlePending) return;   // 拖动/收尾期间热区是藏着的
                    if (_pageDrag) return;                        // 正在拖待办条：别把热区叫回来
                    if (_extDrag)
                    {
                        // 兜底：拖拽被 Esc 取消、或松手落在窗口外时页面可能收不到 dragend。
                        // 文件拖拽一定按着左键，键一松就说明结束了，必须把热区放回来。
                        if ((GetAsyncKeyState(0x01) & 0x8000) == 0) SetExternalDrag(false);
                        return;                      // 外部文件拖入中：热区全收，别抢 OLE 命中
                    }
                    if (AnyPressed()) return;       // 拖动中不显示角标
                    POINT p;
                    if (!GetCursorPos(out p)) return;

                    bool active = _owner.IsActive;
                    // 光标处最上层是不是本程序（主进程或 WebView2 渲染进程）。
                    // 页面区域的命中的是渲染进程的 HWND，所以判据必须包含子进程。
                    bool overSelf = CursorOverSelf(p);

                    foreach (var kv in _live)
                    {
                        var e = kv.Value;
                        if (e.Win == null) continue;
                        // 用摆位时缓存的屏幕矩形，省掉每 110ms 对每个窗各一次 GetWindowRect。
                        double l = e.ScrL, t = e.ScrT, r2 = e.ScrR, b2 = e.ScrB;
                        if (r2 <= l || b2 <= t)
                        {
                            var h = new WindowInteropHelper(e.Win).Handle;
                            if (h == IntPtr.Zero) continue;
                            RECT r;
                            if (!GetWindowRect(h, out r)) continue;
                            l = r.Left; t = r.Top; r2 = r.Right; b2 = r.Bottom;
                            e.ScrL = l; e.ScrT = t; e.ScrR = r2; e.ScrB = b2;
                        }
                        bool on = p.X >= l && p.X <= r2 && p.Y >= t && p.Y <= b2;

                        if (!active)
                        {
                            // 非激活：只为 hover 临时亮这一个热区，离开立刻收回去。
                            if (!on || !overSelf)
                            {
                                if (e.HoverOnly)
                                {
                                    SetHover(e, false);
                                    e.HoverOnly = false;
                                    e.Raised = false;
                                    Park(e);
                                }
                                else if (e.Hover) SetHover(e, false);
                                continue;
                            }
                            if (e.Parked || !e.Win.IsVisible)
                            {
                                e.PlacedX = double.NaN; e.PlacedY = double.NaN;
                                EnsureShownOnce(e);
                                Place(e, e.CssX, e.CssY);
                                Unpark(e);
                                e.HoverOnly = true;
                                e.Raised = false;
                                _log("悬停唤起热区 " + e.AttId);
                            }
                            if (!e.Raised) { RaiseAboveOwner(e); e.Raised = true; }
                            SetHover(e, true);
                            continue;
                        }

                        // 激活状态：隐藏着的热区（刚恢复/滚动中）只清 hover。
                        if (e.Parked || !e.Win.IsVisible) { SetHover(e, false); continue; }
                        SetHover(e, on);
                    }
                }
                catch { }
            };
            _hoverTimer.Start();
        }

        /// <summary>
        /// 新前台窗口是不是我们自己创建的热区。用于区分
        /// 「用户点了别的程序」（该隐藏热区）和「用户点的是热区」（不能隐藏）。
        /// </summary>
        private bool ForegroundIsHotspot()
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                foreach (var kv in _live)
                {
                    var e = kv.Value;
                    if (e.Win == null) continue;
                    var h = new WindowInteropHelper(e.Win).Handle;
                    if (h != IntPtr.Zero && h == fg) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>光标（屏幕物理像素）是否落在任一可见热区矩形内。</summary>
        private bool CursorInHotspot()
        {
            try
            {
                POINT p;
                if (!GetCursorPos(out p)) return false;
                foreach (var kv in _live)
                {
                    var e = kv.Value;
                    if (e.Win == null || e.Parked || !e.Win.IsVisible) continue;
                    var h = new WindowInteropHelper(e.Win).Handle;
                    if (h == IntPtr.Zero) continue;
                    RECT r;
                    if (!GetWindowRect(h, out r)) continue;
                    if (p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>是否有热区按着鼠标（拖动进行中）。</summary>
        private bool AnyPressed()
        {
            foreach (var kv in _live)
            {
                var e = kv.Value;
                try { if (e.Win != null && e.Win.IsMouseCaptured) return true; }
                catch { }
            }
            return false;
        }

        /// <summary>这一个热区正握着鼠标（按住/拖动中）：藏它或拆它都会丢捕获，松手事件就没了。</summary>
        private static bool IsBusy(Entry e)
        {
            if (e == null) return false;
            if (!string.IsNullOrEmpty(e.Mode)) return true;
            try { return e.Win != null && e.Win.IsMouseCaptured; }
            catch { return false; }
        }

        /// <summary>
        /// 主窗口移动/改尺寸后，透明热区必须立刻跟着换屏幕坐标。
        /// 旧实现这里是空方法，宿主窗口一移动，所有热区就停在旧位置；
        /// 页面悬停坐标对不上后，删除角标和附件拖动会表现为「完全没反应」。
        /// </summary>
        public void SyncToOwner()
        {
            // 移动期间整批热区是藏着的，逐个改 Left/Top 只会拖慢窗口跟手；
            // 松手后的「待恢复」窗口里也不能跟着动，否则会把 5 个分层窗拖回来。
            if (_ownerMoving || _settlePending) return;
            if (_live.Count == 0) return;
            foreach (var kv in _live)
            {
                var e = kv.Value;
                try
                {
                    if (e.Win != null && !e.Parked && e.Win.IsVisible)
                        Place(e, e.CssX, e.CssY);
                }
                catch { }
            }
            // 尺寸变化会让网页重新排版；等布局稳定后再让 JS 全量上报一次。
            SchedulePageRefresh();
        }

        /// <summary>
        /// 移动窗口时 LocationChanged 触发很密，合并成一次页面重报，避免打爆消息队列。
        /// </summary>
        private void SchedulePageRefresh()
        {
            if (_refreshTimer == null)
            {
                _refreshTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(60)
                };
                _refreshTimer.Tick += (s, e) =>
                {
                    _refreshTimer.Stop();
                    try { SafeEval("window.api.reportAttRects&&window.api.reportAttRects();"); }
                    catch { }
                };
            }
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        /// <summary>
        /// 摆放热区。全部走原生 SetWindowPos（物理像素）：
        /// WPF 的 Left/Top/Width/Height 每设一次都要过一遍布局 + SetWindowPos，
        /// 12 个热区就是 25~35ms —— 滚动停手恢复时这一下正好被用户看见。
        /// </summary>
        private void Place(Entry e, double cssX, double cssY)
        {
            try
            {
                if (e.Hwnd == IntPtr.Zero) return;
                double cw = e.CssW, ch = e.CssH;
                if (cw <= 0 || ch <= 0) return;

                var host = _webHost ?? (FrameworkElement)_owner;
                var screen = host.PointToScreen(new Point(cssX, cssY));
                var dpi = VisualTreeHelper.GetDpi(host);
                int px = (int)Math.Round(screen.X);
                int py = (int)Math.Round(screen.Y);
                int pw = (int)Math.Round(cw * dpi.DpiScaleX);
                int ph = (int)Math.Round(ch * dpi.DpiScaleY);
                if (pw < 1) pw = 1;
                if (ph < 1) ph = 1;

                e.ScrL = px; e.ScrT = py; e.ScrR = px + pw; e.ScrB = py + ph;

                // 位置/尺寸都没变就什么都不做 —— 这是滚动时最主要的开销来源
                if (Math.Abs(px - e.PlacedX) < 0.5 && Math.Abs(py - e.PlacedY) < 0.5 &&
                    Math.Abs(pw - e.PlacedW) < 0.5 && Math.Abs(ph - e.PlacedH) < 0.5)
                    return;

                e.PlacedX = px; e.PlacedY = py; e.PlacedW = pw; e.PlacedH = ph;
                SetWindowPos(e.Hwnd, IntPtr.Zero, px, py, pw, ph,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS | SWP_NOREDRAW);
            }
            catch { }
        }

        /// <summary>
        /// 第一次真正建 HWND。WPF 建一个这样的窗口实测 50~80ms，所以一辈子只做一次：
        /// 之后收起/恢复全都用原生 ShowWindow，不再碰 Window.Hide()/Show()。
        /// </summary>
        private void EnsureShownOnce(Entry e)
        {
            if (e.WinShown || e.Win == null) return;
            long t0 = Stopwatch.GetTimestamp();
            _initTicks = 0;
            e.Win.Show();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            e.WinShown = true;
            e.Hwnd = new WindowInteropHelper(e.Win).Handle;
            Park(e);   // 先收回去，摆好位置再露出来，避免在默认位置闪一下
            if (ms >= 20)
                _log("建热区窗 " + e.AttId + " 耗时=" + Math.Round(ms, 1) + "ms [init=" +
                     Math.Round(_initTicks * 1000.0 / Stopwatch.Frequency, 1) + "]");
        }

        /// <summary>
        /// 收起：把窗口挪到屏幕外，而不是 ShowWindow(SW_HIDE)。
        /// 实测 SW_HIDE 一个 ~3ms、再 ShowWindow 回来一个 ~10ms（DWM 要重新挂载分层表面）；
        /// 而 SetWindowPos 挪一个只要 ~1.8ms，来回都便宜，且挪到 -32000 后既点不到也滚不到。
        /// </summary>
        private void Park(Entry e)
        {
            if (e == null || e.Hwnd == IntPtr.Zero || e.Parked) return;
            e.Parked = true;
            e.PlacedX = double.NaN; e.PlacedY = double.NaN;
            try
            {
                SetWindowPos(e.Hwnd, IntPtr.Zero, -32000, -32000,
                    (int)e.PlacedW, (int)e.PlacedH,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW);
            }
            catch { }
        }

        /// <summary>恢复显示：Park 只是把窗口挪走，所以这里只要清掉标记，真正的定位由 Place() 完成。</summary>
        private void Unpark(Entry e)
        {
            if (e == null || !e.Parked) return;
            e.Parked = false;
        }

        /// <summary>
        /// WPF 的 owned window 不保证一直在 owner 上方。实测启动/激活后透明热区会被
        /// 主窗口压在下面，WindowFromPoint 命中 Chromium，拖缩略图就变成拖网页图片，
        /// 最终浏览器把图标当成新文件又 drop 回来（名字 ico_...）。
        /// </summary>
        private void RaiseAboveOwner(Entry e)
        {
            try
            {
                var w = e.Win;
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                // 只有第一次需要「落下去再抬起来」强制 WPF 重写扩展样式：
                // 之后每恢复一次都这么折腾＝每次多两次 SetWindowPos + DWM 重排，
                // 12 个窗就是 50ms 的白给开销。
                if (!e.EverRaised)
                {
                    w.Topmost = false;
                    w.Topmost = true;
                    e.EverRaised = true;
                }
                IntPtr after = new IntPtr(-1); // HWND_TOPMOST
                SetWindowPos(h, after, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch { }
        }

        private Point ToCss(Window w, Point inWindow)
        {
            try
            {
                var screen = w.PointToScreen(inWindow);
                return _webHost.PointFromScreen(screen);
            }
            catch { return new Point(0, 0); }
        }

        /// <summary>
        /// 点是否落在删除角标上。角标 CSS：top:3 / right:3 / 22×22，
        /// 所以判定区从方块右上角往内缩 3px 起算，外沿多给 1px 容差。
        /// </summary>
        private static bool OnBadge(Point p, double width)
        {
            double left = width - BADGE - BADGE_INSET;   // 47（72px 方块）
            double top = BADGE_INSET;                    // 3
            return p.X >= left - 1 && p.Y >= top - 1 &&
                   p.X <= width && p.Y <= top + BADGE + 1;
        }

        private bool InsideApp(Point css, double slack = 0)
        {
            try
            {
                return css.X >= -slack && css.Y >= -slack &&
                       css.X <= _webHost.ActualWidth + slack && css.Y <= _webHost.ActualHeight + slack;
            }
            catch { return false; }
        }

        /// <summary>
        /// 指针是否还在主窗口内（屏幕物理像素判断，绕开 WPF/WPF-DPI 坐标换算）。
        /// 「拖出到别的程序」必须用这个判据：用页面 CSS 坐标判会在窗口边缘误判，
        /// 一误判就 DoDragDrop，文件又被自己接住 → 多出一份。
        /// </summary>
        private bool CursorInsideOwner(int slackPx)
        {
            try
            {
                POINT p;
                if (!GetCursorPos(out p)) return true;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(_owner).Handle;
                RECT r;
                if (!GetWindowRect(hwnd, out r)) return true;
                return p.X >= r.Left - slackPx && p.X <= r.Right + slackPx &&
                       p.Y >= r.Top - slackPx && p.Y <= r.Bottom + slackPx;
            }
            catch { return true; }
        }

        /// <summary>
        /// 光标处最上层的窗口是不是本程序的（主窗口 / WebView2 子窗 / 任意热区）。
        /// 用于「非激活状态下的悬停预览」：只有我们真的在最上层才允许浮出热区，
        /// 否则会盖在别的程序上偷点击。
        /// </summary>
        private bool CursorOverSelf(POINT p)
        {
            try
            {
                IntPtr h = WindowFromPoint(p);
                if (h == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == _pid) return true;
                if (_ownPids.Contains(pid)) return true;
                // WebView2 的渲染进程会随页面重建换 pid：遇到不认识的 pid 时隔一小会儿重扫一次进程树。
                if ((DateTime.Now - _ownPidsAt).TotalMilliseconds >= 900)
                {
                    RefreshOwnPids();
                    return _ownPids.Contains(pid);
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>主进程 + 它派生出来的全部子进程（WebView2 家族）。</summary>
        private void RefreshOwnPids()
        {
            var children = new Dictionary<uint, List<uint>>();
            IntPtr snap = CreateToolhelp32Snapshot(0x00000002 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
            {
                try
                {
                    var pe = new PROCESSENTRY32W();
                    pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                    if (Process32FirstW(snap, ref pe))
                    {
                        do
                        {
                            List<uint> kids;
                            if (!children.TryGetValue(pe.th32ParentProcessID, out kids))
                            {
                                kids = new List<uint>();
                                children[pe.th32ParentProcessID] = kids;
                            }
                            kids.Add(pe.th32ProcessID);
                        } while (Process32NextW(snap, ref pe));
                    }
                }
                catch { }
                finally { try { CloseHandle(snap); } catch { } }
            }

            var set = new HashSet<uint>();
            var queue = new Queue<uint>();
            queue.Enqueue(_pid);
            while (queue.Count > 0)
            {
                List<uint> kids;
                if (!children.TryGetValue(queue.Dequeue(), out kids)) continue;
                foreach (var k in kids)
                    if (set.Add(k)) queue.Enqueue(k);
            }
            _ownPids = set;
            _ownPidsAt = DateTime.Now;
        }

        /// <summary>
        /// 主窗口是不是当前前台窗口。热区是 WS_EX_NOACTIVATE 独立窗，点它的瞬间
        /// 主窗口会收到 WA_INACTIVE，所以 _owner.IsActive 在 MouseLeftButtonDown 里永远不可靠；
        /// 「这次点击要不要顺带把窗口抬到前台」必须用 GetForegroundWindow 判。
        /// </summary>
        private bool OwnerIsForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                IntPtr hwnd = new WindowInteropHelper(_owner).Handle;
                if (hwnd == IntPtr.Zero) return false;
                IntPtr cur = fg;
                for (int i = 0; i < 6 && cur != IntPtr.Zero; i++)
                {
                    if (cur == hwnd) return true;
                    cur = GetAncestor(cur, 1 /*GA_PARENT*/);
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>转成 Windows 原生文件拖拽（拖到微信/资源管理器）。</summary>
        private void StartSystemDrag(Window w, Entry e)
        {
            e.Mode = "ext";
            e.Dragging = true;
            SetPress(e, false);
            SetBadge(e, false);
            SetHover(e, false);
            try { w.ReleaseMouseCapture(); } catch { }

            string path = e.Path;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                _log("拖出失败，文件不存在: " + path);
                return;
            }

            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            // 内部标记：万一把文件又拖回自己的窗口，页面要知道「这是我自己的附件」，别当成新文件再存一份
            try { data.SetData("TempNotesInternal", true); } catch { }
            // 页面里的兜底：Chromium 不一定把自定义剪贴板格式透到 DataTransfer.types，
            // 所以在发起系统拖拽前给页面打时间戳，**并把整段拖拽标记成「自己的」**。
            // 只靠 2.5 秒时间戳挡不住「拖出去停一会儿再拖回来」——那条路会走页面的导入逻辑，
            // 复制出一份一模一样的附件（用户实测）。__sysDrag 在 DoDragDrop 返回时清掉。
            try { SafeEval("window.__internalDragAt=Date.now(); window.__sysDrag=1;"); } catch { }
            try
            {
                DragDrop.DoDragDrop(w, data, DragDropEffects.Copy);
                _log("拖出完成: " + path);
            }
            catch (Exception ex) { _log("拖出异常: " + ex.Message); }
            finally
            {
                // 拖拽结束（成功/取消/异常都要清）：否则下一次真正从资源管理器拖文件进来会被误吞
                try { SafeEval("window.__sysDrag=0;"); } catch { }
            }
        }

        private Window CreateWindow(Entry e)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                // 保持 AllowTransparency：这是唯一能保证「完全不可见但能命中」的组合。
                // 曾试过「普通窗口 + WS_EX_LAYERED 统一 alpha=1」，WPF 后面会把扩展样式
                // 重写掉，alpha 失效后屏幕上就是一排黑块 —— 已回退，别再改。
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                // 不挂 Owner：owned window 的 z-order 会被 WPF 在激活时重排，
                // 实测会出现部分热区落到 Chromium 下面。改成独立置顶窗，
                // 主窗口 Deactivated 时统一隐藏，Closed 时统一关闭。
                Owner = null,
                // 只在主窗口激活期间存在（Deactivated 会 HideAll），可安全置顶。
                Topmost = true,
                Cursor = Cursors.Hand,
                AllowDrop = true,
                Content = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) }
            };

            // 透明热区本身也是独立 HWND；如果不开 AllowDrop，文件拖到缩略图上会显示禁止符号。
            // 落进缩略图就等同于落在这条笔记上。
            w.DragOver += (s, ev) =>
            {
                bool internalDrag = ev.Data.GetDataPresent("TempNotesInternal");
                // 从资源管理器把文件拖到缩略图上：先整批收起热区，
                // 后面的 dragover 就落到页面上，占位预览才能跟着指针走。
                if (!internalDrag && ev.Data.GetDataPresent(DataFormats.FileDrop)) SetExternalDrag(true);
                ev.Effects = (!internalDrag && ev.Data.GetDataPresent(DataFormats.FileDrop))
                    ? DragDropEffects.Copy : DragDropEffects.None;
                ev.Handled = true;
            };
            w.Drop += (s, ev) =>
            {
                try
                {
                    if (ev.Data.GetDataPresent("TempNotesInternal")) { ev.Handled = true; return; }
                    var files = ev.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0 && _onDropFiles != null)
                        _onDropFiles(files, e.AttId);
                }
                catch (Exception ex) { _log("缩略图拖入失败 " + ex.Message); }
                ev.Handled = true;
            };

            // 关键：去掉 WS_EX_NOACTIVATE 之外的激活能力。
            // 不设这个样式时，第一次点热区会把热区自己变成前台窗口 →
            // 主窗口 Deactivated → HideAll() 把热区全藏起来 → 鼠标捕获丢失 →
            // 用户看到的就是「缩略图点不动、拖不动」。
            w.SourceInitialized += (s, ev) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(w).Handle;
                    if (hwnd == IntPtr.Zero) return;
                    int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE,
                        ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
                catch { }
            };

            // 不要在这里 w.Hide()。
            // 热区是盖在 <img> 上的独立 HWND，页面收不到 mouseenter，
            // 所以一旦 MouseLeave 把窗口藏了，就没有任何路径能把它再叫回来
            // （用户表现：附件「点一下之后再也点不动，滚一下才恢复」）。
            // 热区的生命周期改成只跟页面上报的矩形走：Update() 里没上报的才关闭。

            // 悬停：页面里的删除角标不要常驻，指针真的在小窗上才显示。
            w.MouseEnter += (s, ev) => { if (!AnyPressed()) SetHover(e, true); };
            w.MouseLeave += (s, ev) => { SetHover(e, false); SetPress(e, false); SetBadge(e, false); };

            // 滚轮压在缩略图上：热区是独立 HWND，会吃掉滚动。
            // 先只藏掉发送者，再转第一格给页面；后面的滚轮就由页面原生处理。
            w.MouseWheel += (s, ev) =>
            {
                HideForWheel(e);
                SafeEval("window.api.hotWheel&&window.api.hotWheel(" + ev.Delta + ");");
                ev.Handled = true;
            };

            w.MouseLeftButtonDown += (s, ev) =>
            {
                // 绝对不能用 _owner.IsActive 判断「这次点击算不算数」：
                // 热区是 WS_EX_NOACTIVATE 独立窗，按下的瞬间 Windows 已经把 WA_INACTIVE
                // 发给主窗口了，IsActive 在这里必然是 false。以前按它判断的结果是
                // 每一次按下都被当成「非激活首点」吞掉 —— 角标、拖动、打开全部失效。
                // 需要抬前台时用 OwnerIsForeground（GetForegroundWindow）判，但点击照常生效。
                bool fgOurs = OwnerIsForeground();
                e.DownAt = ev.GetPosition(w);
                e.Dragging = false;
                e.OutSince = default(DateTime);
                e.Mode = OnBadge(e.DownAt, w.Width) ? "badge" : "";
                _log("附件按下 " + e.AttId + " 模式=" + (e.Mode == "badge" ? "删除角标" : "普通"));
                SetPress(e, e.Mode != "badge");
                SetBadge(e, e.Mode == "badge");
                w.CaptureMouse();
                if (!fgOurs)
                {
                    try { _owner.Activate(); } catch { }
                }
                ev.Handled = true;
            };

            w.MouseMove += (s, ev) =>
            {
                if (!w.IsMouseCaptured) return;

                var p = ev.GetPosition(w);
                var css = ToCss(w, p);

                if (e.Mode == "badge")
                {
                    // 按在删除角标上：不触发任何拖动。
                    // 按住后把指针移出角标＝放弃这次删除，角标立刻恢复成普通悬停态
                    // （红色按下态收回），松手时才不会误删。
                    bool on = OnBadge(p, w.Width);
                    if (on != e.BadgePress) SetBadge(e, on);
                    return;
                }
                if (e.Mode == "js")
                {
                    SafeEval("window.api.attDragMove('" + e.AttId + "'," +
                             css.X.ToString("0.#") + "," + css.Y.ToString("0.#") + ")");
                    // 只有真的离开应用窗口才升级成系统拖拽：
                    //  · 必须在窗口外 ≥24px 且持续 ≥140ms —— 左右拖排序时指针蹭到边缘不会误判
                    //  · 误判的代价是文件被 DoDragDrop 拖回自己窗口，OnWindowDrop 又存一份
                    //    （用户实测：附件没换位，反而多出一份、名字还带 (2)）
                    if (!CursorInsideOwner(36))
                    {
                        if (e.OutSince == default(DateTime)) e.OutSince = DateTime.Now;
                        if ((DateTime.Now - e.OutSince).TotalMilliseconds >= 140)
                        {
                            SafeEval("window.api.attDragEnd('" + e.AttId + "',null,null)");   // 取消站内拖动
                            StartSystemDrag(w, e);
                        }
                    }
                    else e.OutSince = default(DateTime);
                    return;
                }
                if (e.Mode == "ext") return;

                if (Math.Abs(p.X - e.DownAt.X) < 4 && Math.Abs(p.Y - e.DownAt.Y) < 4) return;

                // 一律先当站内拖动：横向拖来排序也必须能走这条路
                e.Mode = "js";
                e.Dragging = true;
                SetPress(e, false);
                SetBadge(e, false);
                SetHover(e, false);
                SafeEval("window.api.attDragBegin('" + e.AttId + "'," +
                         css.X.ToString("0.#") + "," + css.Y.ToString("0.#") + ")");
                return;

            };

            w.MouseLeftButtonUp += (s, ev) =>
            {
                try { w.ReleaseMouseCapture(); } catch { }
                SetPress(e, false);
                SetBadge(e, false);

                var p = ev.GetPosition(w);
                var css = ToCss(w, p);

                if (e.Mode == "badge")
                {
                    // 只有松手时指针仍落在角标上才算删除。
                    // 按住角标拖出去再松手＝取消（用户明确要求：移开就是取消，不能触发删除）。
                    if (OnBadge(p, w.Width))
                    {
                        if (_onDelete != null) _onDelete(e.AttId);
                        _log("点击删除角标 " + e.AttId);
                    }
                    else _log("取消删除（按住移开）" + e.AttId);
                    e.Dragging = false;
                    e.Mode = "";
                    try { if (CursorInsideOwner(0)) _owner.Activate(); } catch { }
                    return;
                }

                if (e.Mode == "js")
                {
                    SafeEval("window.api.attDragEnd('" + e.AttId + "'," +
                             css.X.ToString("0.#") + "," + css.Y.ToString("0.#") + ")");
                    _log("附件站内拖动结束 " + e.AttId);
                }
                else if (!e.Dragging)
                {
                    bool onBadge = OnBadge(e.DownAt, w.Width);
                    if (onBadge)
                    {
                        if (_onDelete != null) _onDelete(e.AttId);
                        _log("点击删除角标 " + e.AttId);
                    }
                    else if (!string.IsNullOrEmpty(e.Path))
                    {
                        if (_suppressOpen) _log("自测模式：不打开文件 " + e.Path);
                        else { try { System.Diagnostics.Process.Start(e.Path); } catch { } }
                    }
                }
                e.Dragging = false;
                e.Mode = "";
                // 点热区会让 Windows 把 WA_INACTIVE 发给主窗口（热区是 NOACTIVATE，
                // 不会自己变成前台，但主窗口的激活状态会被动掉）。点完把焦点还给主窗口，
                // 否则接下来打字会没反应。
                try { if (CursorInsideOwner(0)) _owner.Activate(); } catch { }
            };

            w.MouseRightButtonUp += (s, ev) =>
            {
                e.Dragging = false;
                e.Mode = "";
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                if (ctrl)
                {
                    if (_onReveal != null) _onReveal(e.AttId);
                }
                else
                {
                    if (_onCopy != null) _onCopy(e.AttId);
                }
                ev.Handled = true;
            };

            return w;
        }

        private void SafeEval(string js)
        {
            try { if (_eval != null) _eval(js); } catch (Exception ex) { _log("eval 失败 " + ex.Message); }
        }
    }
}

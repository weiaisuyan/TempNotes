using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using TempNotes;

namespace TempNotes2
{
    public partial class MainWindow : Window
    {
        private Store _store;
        private Bridge _bridge;
        private AttDragHost _attDragHost;

        // ---- 单实例 / 托盘 / 全局热键 ----
        private static System.Threading.Mutex _mutex;
        private System.Windows.Forms.NotifyIcon _tray;
        private System.Windows.Threading.DispatcherTimer _trayClickTimer;
        private HwndSource _hwndSource;
        private const int HOTKEY_ID = 0xB001;
        private bool _allowClose;
        private System.Windows.Threading.DispatcherTimer _hideToTrayTimer;
        private DateTime _trayIgnoreClickUntil = DateTime.MinValue;

        private const int SW_RESTORE = 9;
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private static readonly uint WM_SHOW_INSTANCE = RegisterWindowMessage("TempNotes2_ShowInstance");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        public MainWindow()
        {
            // 固定任务栏身份：不设的话 Windows 按 exe 路径归组，换一次 exe / 换一次目录
            // 任务栏就当成「另一个程序」，旧固定项失效、旁边再冒一个新图标。
            // 设成一个固定 AUMID 后，只要快捷方式指向同一个 AUMID，重编多少次都归到同一个按钮。
            try { SetCurrentProcessExplicitAppUserModelID("Suiji.TempNotes.Desktop"); } catch { }

            // 单实例：已有一个在跑就直接退出（第二个实例只负责把第一个唤起来）
            bool created;
            _mutex = new System.Threading.Mutex(true, "TempNotes2_SingleInstance", out created);
            if (!created)
            {
                ShutdownExisting();
                Application.Current.Shutdown();
                return;
            }

            InitializeComponent();
            SourceInitialized += (s, e) => EnsureCaptionStyle();
            Loaded += OnLoaded;

            // 窗口图标：运行时从 exe 同目录读取，避开 XAML 里 pack URI 的解析问题
            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(ico))
                {
                    // 必须自己挑最大帧：BitmapFrame.Create 只拿 ICO 的第一帧，
                    // 而 ico 的第一帧是 16×16，贴到 22px 的标题栏上就是「放大糊图」，
                    // 看着像旧版图标（任务栏用的是 exe 里的大图，所以两边不一样）。
                    var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        new Uri(ico),
                        System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    System.Windows.Media.Imaging.BitmapFrame frame = dec.Frames[0];
                    foreach (var f in dec.Frames)
                        if (f.PixelWidth > frame.PixelWidth) frame = f;

                    Icon = frame;
                    if (AppIconImg != null)
                    {
                        // 只对标题栏那张图启用高质量缩放（22px 目标），避免边缘发灰
                        System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                            AppIconImg, System.Windows.Media.BitmapScalingMode.HighQuality);
                        AppIconImg.Source = frame;
                    }
                }
            }
            catch { }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string lpString);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool ClipCursorRect(ref RECT r);
        [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool ClipCursorNone(IntPtr p);
        [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] p, uint n, uint size);
        [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdrSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

        private const int WM_INPUT = 0x00FF;
        // 窗口进入/退出「移动或缩放」模态循环。拖动标题栏、拖边框缩放都会发。
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_MOVE = 0x0003;
        // 按在标题栏上（还没开始移动）就先把附件热区藏掉：
        // 系统要等指针移过拖拽阈值才发 WM_ENTERSIZEMOVE，那之前的两三帧
        // 还在逐个 SetWindowPos 摆十几个分层窗，窗口就是从这里开始掉队的。
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCLBUTTONUP = 0x00A2;
        private const int HTCAPTION = 2;

        // ---- 拖窗帧率埋点：只在拖动期间累加，松手打一行日志，不参与任何行为 ----
        private readonly System.Diagnostics.Stopwatch _mvSw = new System.Diagnostics.Stopwatch();
        private long _mvFrames, _mvLastMs, _mvMaxGap, _mvGapSum;
        private int _mvMaxLag, _mvWx0, _mvWy0, _mvCx0, _mvCy0;
        private bool _mvOn;

        // ---- 低级鼠标钩子：用于把光标死死限制在「列表区域」内 ----
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE_H = 0x0200;

        private IntPtr _mouseHook = IntPtr.Zero;
        private LowLevelMouseProc _mouseHookProc;
        private int _clipTopScr, _clipBottomScr, _clipLeftScr, _clipRightScr;
        private bool _clipValid = false;

        private void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseHookProc = MouseHookCallback;   // 必须保活，否则被 GC 回收后钩子失效
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, IntPtr.Zero, 0);
            if (_mouseHook == IntPtr.Zero) LogMsg("鼠标钩子安装失败 err=" + Marshal.GetLastWin32Error());
        }

        private void UninstallMouseHook()
        {
            if (_mouseHook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook = IntPtr.Zero;

        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // 不吞任何鼠标事件。WH_MOUSE_LL 的 pt 与 WPF 的
                // GetWindowRect 在混合 DPI/多显示器下不是同一套坐标，
                // 用它做越界判断会误杀正常移动（实测 262 次/0.5s），
                // 表现就是「一个都拖不了」。
                //
                // 这里只重新声明系统剪裁矩形；真正的边界由 Windows 执行，
                // 程序不拦截、不移动光标。
                if (nCode >= 0 && _draggingSession && _clipValid &&
                    wParam.ToInt32() == WM_MOUSEMOVE_H)
                    ApplyCursorClip();
            }
            catch { }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void ApplyCursorClip()
        {
            if (!_draggingSession || !_clipValid) return;
            var r = new RECT
            {
                Left = _clipLeftScr,
                Top = _clipTopScr,
                Right = _clipRightScr,
                Bottom = _clipBottomScr
            };
            ClipCursorRect(ref r);
        }
        private const uint RIDEV_INPUTSINK = 0x00000100;
        // 反注册必须用这个标志；只把 usUsagePage/usUsage 置 0 是非法 TLC，
        // RegisterRawInputDevices 会返回 FALSE(err=87)，注册根本不会解除。
        private const uint RIDEV_REMOVE = 0x00000001;
        private const uint RID_INPUT = 0x10000003;
        // x64: RAWINPUTHEADER = dwType(4) + dwSize(4) + hDevice(8) + wParam(8) = 24
        private const uint RAWINPUT_HEADER_SIZE = 24;
        private int _rawHits = 0;
        private double _rawAccumY = 0;      // 鼠标原始位移累积（不受光标钳制影响）

        /// <summary>注册/注销鼠标原始输入：SetCursorPos/ClipCursor 都改不了它，能拿到"手推了多少"。</summary>
        private void RegisterRawMouse(bool on)
        {
            try
            {
                var rid = new RAWINPUTDEVICE[1];
                rid[0].usUsagePage = 0x01;
                rid[0].usUsage = 0x02;
                if (on)
                {
                    rid[0].dwFlags = RIDEV_INPUTSINK;
                    rid[0].hwndTarget = new WindowInteropHelper(this).Handle;
                }
                else
                {
                    // 旧写法：usUsagePage=0 / usUsage=0 / dwFlags=0 —— 实测 RegisterRawInputDevices
                    // 返回 FALSE(err=87)，等于从来没注销过：拖过一次卡片之后，进程会一直收到
                    // 全系统每一条鼠标的 WM_INPUT（RIDEV_INPUTSINK 连后台也收），直到退出程序。
                    rid[0].dwFlags = RIDEV_REMOVE;
                    rid[0].hwndTarget = IntPtr.Zero;
                }
                bool ok = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (_rawRegState != (on ? -1 : -2))
                    {
                        _rawRegState = on ? -1 : -2;
                        LogMsg("原始输入" + (on ? "注册" : "注销") + "失败 err=" + err);
                    }
                }
                else _rawRegState = on ? 1 : 0;
            }
            catch { }
        }

        /// <summary>原始输入状态：1=已注册 0=已注销 -1=注册失败 -2=注销失败（只在翻转时写日志）。</summary>
        private int _rawRegState = 0;

        private System.Windows.Threading.DispatcherTimer _clipTimer;
        private volatile bool _draggingSession;
        private int _clipTicks;

        /* 松手看门狗：和「光标硬边界」解耦。
           以前它寄生在 _clipTimer 里，窗口一失焦 Deactivated 就把整个定时器停掉，
           之后手在外面松开页面永远收不到 pointerup —— 列表一直滚、红边一直闪。
           现在它独立跑：只要本次拖动还没被页面正常收尾，就一直盯着左键，
           一抬起就补一条 mouseup 消息给页面。 */
        private System.Windows.Threading.DispatcherTimer _upWatch;
        private volatile bool _upWatchOn;

        private void StartUpWatch()
        {
            if (_upWatch == null)
            {
                _upWatch = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(30) };
                _upWatch.Tick += (s, e) =>
                {
                    if (!_upWatchOn) { _upWatch.Stop(); return; }
                    try
                    {
                        if ((GetAsyncKeyState(0x01) & 0x8000) == 0)
                        {
                            _upWatchOn = false;
                            _upWatch.Stop();
                            LogMsg("看门狗：检测到左键已松开，补发 mouseup");
                            if (Web != null && Web.CoreWebView2 != null)
                                Web.CoreWebView2.PostWebMessageAsString("mouseup");
                        }
                    }
                    catch { }
                };
            }
            _upWatchOn = true;
            _upWatch.Start();
        }

        private void StopUpWatch()
        {
            _upWatchOn = false;
            if (_upWatch != null && _upWatch.IsEnabled) _upWatch.Stop();
        }

        internal static Action<string> LogMsg = delegate { };

        /// <summary>
        /// 拖动排序期间把鼠标压在窗口内。
        ///
        /// 不用 ClipCursor：WebView2 拖动时会自己调 ClipCursor，把我们的矩形覆盖掉
        /// （实测只有 x 生效、y 失效）。改成 16ms 定时器检查 + SetCursorPos 拉回，
        /// 不依赖任何系统状态，稳定可靠。
        /// </summary>
        private double _intentY = double.NaN;
        private int _lockStartY = 0;        // 按下时光标的 CSS Y
        private int _lockStartX = 0;        // 按下时光标的屏幕 X
        private int _lockStartScrY = 0;     // 按下时光标的屏幕 Y（光标下界，防卡片上移盖输入框）
        private int _grabY = 0;
        private int _listTopCss = 0;
        private int _listBottomCss = 660;
        private int _cardH = 80;
        private double _startRawY = 0;

        private void StartDragWatch()
        {
            // 自动化验证专用：不装鼠标钩子、不用 SetCursorPos 拉光标。
            // 页面里用合成 DOM 事件跑拖动时，真实光标可能停在别的程序上，
            // 钩子会把用户的鼠标拽进窗口。
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--no-dragwatch") >= 0) return;

            _draggingSession = true;
            _clipTicks = 0;
            _intentY = double.NaN;
            _rawAccumY = 0;
            StartUpWatch();

            // 记下按下时光标位置，作为「意图」的基准
            try
            {
                POINT p0; GetCursorPos(out p0);
                var webTL0 = Web.PointToScreen(new System.Windows.Point(0, 0));
                double dpi0 = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (dpi0 <= 0) dpi0 = 1;
                _lockStartY = (int)Math.Round((p0.Y - webTL0.Y) / dpi0);
                _lockStartX = p0.X;
                _lockStartScrY = p0.Y;
            }
            catch { _lockStartY = 0; }

            _clipValid = false;
            RegisterRawMouse(true);
            InstallMouseHook();

            // 光标边界必须和卡片可移动范围共用同一套公式，否则卡片先停、
            // 光标还能继续走，就会出现“光标离开待办块”。
            //
            // 卡片 top 的合法范围是：
            //   [listTop, listBottom - min(cardH, 56)]
            // 手抓在卡片内的 grabY 处，所以光标 Y 的合法范围整体下移 grabY。
            // Web.PointToScreen 给出 WebView 客户区左上角的物理屏幕坐标，
            // 再乘 DPI 即可把 JS 的 CSS 像素换成 ClipCursor 需要的屏幕像素。
            try
            {
                var webTL = Web.PointToScreen(new System.Windows.Point(0, 0));
                var webBR = Web.PointToScreen(new System.Windows.Point(Web.ActualWidth, Web.ActualHeight));
                double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (dpi <= 0) dpi = 1;

                int keep = Math.Min(_cardH, 56);
                // 卡片明显高于 80px 时，手必须能被允许比列表顶更高：
                // SortableJS 按「幽灵几何中心」判断是否越过目标卡片中线，而幽灵被钳在列表顶时，
                // 高卡片的中心仍在本区第一张卡片中线**之下** ⇒ 怎么拖都插不到第一位，
                // 只闪红边（用户实测：183px 的卡片、抓在上部 26px 处，中心差 40px）。
                // 每高出 80 的部分放 0.6 倍，正好够中心压过第一张卡的中线。
                double topSlack = Math.Max(0, (_cardH - 80) * 0.6);
                double topCss = _listTopCss + _grabY - topSlack;
                double bottomCss = _listBottomCss - keep + _grabY;

                _clipLeftScr = (int)Math.Round(webTL.X + 1);
                _clipRightScr = (int)Math.Round(webBR.X - 1);
                _clipTopScr = (int)Math.Round(webTL.Y + topCss * dpi);
                _clipBottomScr = (int)Math.Round(webTL.Y + bottomCss * dpi);

                // 卡片只露出一部分时（列表已经滚过一段，第一张卡有一截在列表上沿之外），
                // 公式算出的上界会落在按下点「下面」。旧写法把按下点并进矩形当上界 ——
                // 光标被死死钉在按下点，往上一点都动不了；没有 mousemove，边缘自动滚动
                // 也起不来，用户看到的就是「往上拖完全拖不动，先往下再往上也不行」。
                // 现在这一侧放宽到 WebView 可视区边缘：卡片仍由 JS 钳在列表里，
                // 手可以继续走，靠边缘自动滚动把列表滚回去（滚到顶再闪红边）。
                POINT pc; GetCursorPos(out pc);
                if (pc.Y < _clipTopScr) _clipTopScr = (int)Math.Round(webTL.Y + 1);
                if (pc.Y > _clipBottomScr) _clipBottomScr = (int)Math.Round(webBR.Y - 1);

                // 外层兜底：无论 DPI/窗口边框怎么算，都不许超过程序窗口。
                RECT wr;
                bool gotRect = false;
                var hwnd0 = new WindowInteropHelper(this).Handle;
                if (GetWindowRect(hwnd0, out wr))
                {
                    gotRect = true;
                    _clipLeftScr = Math.Max(_clipLeftScr, wr.Left + 2);
                    _clipTopScr = Math.Max(_clipTopScr, wr.Top + 2);
                    _clipRightScr = Math.Min(_clipRightScr, wr.Right - 3);
                    // 超高卡片（带一堆图片那种）：抓点在卡片很下面时，能拖多低完全取决于
                    // 光标能走多低。夹在窗口底会把行程砍掉一大截 —— 392px 卡片、抓在 350px 处
                    // 只剩 64px 可走，用户感觉就是「往下拖不动」。
                    // 所以这类卡片允许光标越过窗口下沿、一直走到屏幕底（松手后 ClipCursor 会放开）。
                    int screenBottom = wr.Bottom - 3;
                    if (_cardH > 160)
                    {
                        try
                        {
                            // Screen.Bounds 已经是物理像素，别再乘 dpi
                            screenBottom = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height - 2;
                            if (screenBottom < wr.Bottom - 3) screenBottom = wr.Bottom - 3;
                        }
                        catch { }
                    }
                    _clipBottomScr = Math.Min(_clipBottomScr, screenBottom);
                    if (_clipRightScr <= _clipLeftScr + 8 || _clipBottomScr <= _clipTopScr + 8)
                    {
                        _clipLeftScr = wr.Left + 2; _clipTopScr = wr.Top + 2;
                        _clipRightScr = wr.Right - 3; _clipBottomScr = screenBottom;
                    }
                }
                _clipValid = gotRect &&
                             pc.X >= _clipLeftScr && pc.X <= _clipRightScr &&
                             pc.Y >= _clipTopScr && pc.Y <= _clipBottomScr;
                if (!_clipValid) LogMsg("拖动起点在窗口外，本次不启用光标硬边界");
                LogMsg((_clipValid ? "光标硬边界 " : "光标硬边界未启用 ") +
                       _clipLeftScr + "," + _clipTopScr + " - " +
                       _clipRightScr + "," + _clipBottomScr +
                       "  css=" + topCss.ToString("0") + ".." + bottomCss.ToString("0"));
                if (_clipValid) ApplyCursorClip();
            }
            catch { _clipValid = false; _clipTopScr = 0; _clipBottomScr = 2000; }

            if (_clipTimer == null)
            {
                _clipTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(8) };

                _clipTimer.Tick += (s2, e2) =>
                {
                    if (!_draggingSession) { _clipTimer.Stop(); return; }
                    _clipTicks++;

                    try
                    {
                        // 每 8ms 重新声明一次系统剪裁矩形。WebView2 可能在同一帧
                        // 覆盖 ClipCursor，这个定时器负责把它压回来；不碰光标本身。
                        ApplyCursorClip();

                        // vcursor 仍按「按下位置 + 原始位移」发，但 app.js 现在优先用真实光标
                        // `_lastClientY`，它只在没有真实光标时兜底。
                        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                        if (dpi <= 0) dpi = 1;
                        double vY = _lockStartY + (_rawAccumY / dpi);

                        if (double.IsNaN(_intentY)) _intentY = vY;
                        // 往外推：快速跟上；松劲：缓慢回落
                        bool pushing = Math.Abs(vY - _intentY) > 0.5;
                        _intentY += (pushing ? 0.85 : 0.45) * (vY - _intentY);

                        if (Web != null && Web.CoreWebView2 != null)
                            Web.CoreWebView2.PostWebMessageAsString("vcursor|" + (int)Math.Round(_intentY));

                        // 左键已在外面松开时兜底，防拖死
                        if (_clipTicks > 8 && (GetAsyncKeyState(0x01) & 0x8000) == 0)
                        {
                            _draggingSession = false;
                            _clipTimer.Stop();
                            ClipCursorNone(IntPtr.Zero);
                            RegisterRawMouse(false);
                            UninstallMouseHook();
                            if (Web != null && Web.CoreWebView2 != null)
                                Web.CoreWebView2.PostWebMessageAsString("mouseup");
                        }
                    }
                    catch { }
                };
            }
            _clipTimer.Start();
        }

        private void StopCursorClamp() { StopCursorClamp(false); }

        /// <summary>
        /// keepWatchdog=true 用在「窗口失焦」：光标约束要立刻放开（不然鼠标被钉住），
        /// 但松手看门狗必须继续跑，否则手在外面松开时页面收不到收尾消息。
        /// </summary>
        private void StopCursorClamp(bool keepWatchdog)
        {
            if (_draggingSession) LogMsg("拖动结束：已释放光标约束，clipValid=" + _clipValid);
            _draggingSession = false;
            // 页面拖动的收尾消息可能丢（失焦/被原生层吞），这里兜底把热区放回来。
            // SetPageDrag 自身幂等，正常 dragend 已经清过时这里零成本。
            try { if (_attDragHost != null) _attDragHost.SetPageDrag(false); } catch { }
            _clipValid = false;
            if (_clipTimer != null && _clipTimer.IsEnabled) _clipTimer.Stop();
            if (!keepWatchdog) StopUpWatch();
            try { ClipCursorNone(IntPtr.Zero); } catch { }
            try { RegisterRawMouse(false); } catch { }
            try { UninstallMouseHook(); } catch { }
        }

        private void BtnTrash_Click(object sender, RoutedEventArgs e) { Eval("window.api.openTrash && window.api.openTrash();"); }
        private void BtnSettings_Click(object sender, RoutedEventArgs e) { Eval("window.api.openSettings && window.api.openSettings();"); }

        /// <summary>标题栏「语法速查」：用系统默认浏览器打开本地的 md-guide.html。</summary>
        private void BtnGuide_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string p = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "md-guide.html");
                if (!System.IO.File.Exists(p)) { LogMsg("找不到语法速查页面: " + p); return; }
                System.Diagnostics.Process.Start(p);
            }
            catch (Exception ex) { LogMsg("打开语法速查失败 " + ex.Message); }
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e) { SystemCommands.MinimizeWindow(this); }

        private void BtnMax_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) { Close(); }

        /// <summary>
        /// WindowStyle=None 的窗口没有 WS_CAPTION，DWM 不会播放最小化/最大化/还原
        /// 动画（只剩淡入淡出，看起来像「闪一下」）。WindowChrome 的 WM_NCCALCSIZE
        /// 已经把客户区铺满整个窗口，补回 WS_CAPTION 不会画出系统标题栏，
        /// 只是让 ShowWindow(SW_MINIMIZE/SW_MAXIMIZE/SW_RESTORE) 走系统原生动画。
        /// </summary>
        /// <summary>
        /// 诊断用：只在命令行带 --msglog 时挂上，把主窗口收到的「文本注入类」消息
        /// （WM_CHAR / WM_PASTE / WM_IME_CHAR / WM_SETTEXT）连同当时的焦点窗口类名写进 bridge.log。
        /// 用途：判断第三方输入法的语音输入到底把文字发到了哪里 —— 主窗口收到了却没进
        /// WebView2，就得由我们自己转发；主窗口压根没收到，说明它发给了别的窗口。
        /// </summary>
        private void InstallMessageLog()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (Array.IndexOf(args, "--msglog") < 0) return;
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src == null) return;
                src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    switch (msg)
                    {
                        case 0x0302:   // WM_PASTE
                        case 0x0102:   // WM_CHAR
                        case 0x0286:   // WM_IME_CHAR
                        case 0x000C:   // WM_SETTEXT
                        case 0x0109:   // WM_IME_STARTCOMPOSITION
                            {
                                var sb = new System.Text.StringBuilder(256);
                                GetClassName(GetFocus(), sb, sb.Capacity);
                                long wp = wParam.ToInt64();
                                string extra = (msg == 0x0102 || msg == 0x0286) && wp >= 32 && wp < 65536
                                    ? " ('" + (char)wp + "')" : "";
                                LogMsg("MSG " + MsgName(msg) + " wParam=" + wp + extra + " focus=" + sb);
                                break;
                            }
                    }
                    return IntPtr.Zero;
                });
                LogMsg("消息日志已开启（--msglog）");
            }
            catch (Exception ex) { LogMsg("消息日志安装失败 " + ex.Message); }
        }

        private static string MsgName(int m)
        {
            switch (m)
            {
                case 0x0302: return "WM_PASTE";
                case 0x0102: return "WM_CHAR";
                case 0x0286: return "WM_IME_CHAR";
                case 0x000C: return "WM_SETTEXT";
                case 0x0109: return "WM_IME_STARTCOMPOSITION";
                default: return "0x" + m.ToString("X4");
            }
        }

        private void EnsureCaptionStyle()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int style = GetWindowLong(hwnd, GWL_STYLE);
                if ((style & WS_CAPTION) == 0)
                    SetWindowLong(hwnd, GWL_STYLE, style | WS_CAPTION);
                // 圆角：用 Win11 自带的 DWM 圆角（固定档位，约 8px）。
                // 自绘窗口区域那套（SetWindowRgn，12 DIP）试过，用户要求改回 DWM 原生。
                try
                {
                    int pref = DWMWCP_ROUND;
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                }
                catch { }
                LogMsg("窗口样式 style=0x" + style.ToString("X8") + " -> 0x" +
                       GetWindowLong(hwnd, GWL_STYLE).ToString("X8"));
            }
            catch (Exception ex) { LogMsg("补 WS_CAPTION 失败 " + ex.Message); }
        }

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            // 站内拖动（附件排序/跨笔记移动）被误升级成系统拖拽时，不让它显示成「复制进来」
            bool internalDrag = e.Data.GetDataPresent("TempNotesInternal");
            e.Effects = (!internalDrag && e.Data.GetDataPresent(DataFormats.FileDrop))
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>
        /// 文件拖进窗口 → 新建一条笔记，附件挂上去。
        /// （引用模式：不复制文件，只记原路径；原文件删了会变死链，这是既定取舍）
        /// </summary>
        private async void OnWindowDrop(object sender, DragEventArgs e)
        {
            try
            {
                // 自己拖出去的附件又落回自己窗口：必须忽略。
                // 否则「拖动排序」会变成「新建一条笔记 + 再存一份文件」，名字还会带 (2)(3)。
                if (e.Data.GetDataPresent("TempNotesInternal"))
                {
                    e.Handled = true;
                    return;
                }
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                try { LogMsg("原生文件拖入 " + files.Length + " 个"); } catch { }

                // 落点决定挂到哪条笔记：卡片上 → 原笔记；输入框/空白 → 新建。
                // JS 的 elementFromPoint 是唯一可靠命中原生 WebView DOM 的方式。
                string targetId = "";
                try
                {
                    if (Web != null && Web.CoreWebView2 != null)
                    {
                        var pos = e.GetPosition(Web);
                        string js = "window.api.nativeDropTarget(" +
                            pos.X.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "," +
                            pos.Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + ")";
                        string raw = await Web.CoreWebView2.ExecuteScriptAsync(js);
                        targetId = (raw ?? "").Trim().Trim('"');
                    }
                }
                catch { targetId = ""; }

                _bridge.AddDroppedPaths(files, targetId);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  拖入失败 " + ex.Message + Environment.NewLine); } catch { }
            }
        }

        private void AddFolderRef(Note n, string dir)
        {
            try
            {
                var a = new Attachment
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = "file",
                    OrigName = Path.GetFileName(dir),
                    StoredPath = dir,
                    Size = 0,
                    Linked = true
                };
                n.Attachments.Add(a);
            }
            catch { }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            try
            {
                bool max = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Minimized)
                {
                    // 最小化时热区是 Topmost 独立窗，不藏会浮在别的程序上面挡点击。
                    try { if (_attDragHost != null) _attDragHost.HideAll("窗口最小化"); } catch { }
                }
                if (MaxIcon != null)
                    MaxIcon.Data = System.Windows.Media.Geometry.Parse(
                        max ? "M4,4 H12 V12 H4 Z M6,4 V2 H14 V10 H12"   /* 还原：两个叠框 */
                            : "M3.6,3.6 H12.4 V12.4 H3.6 Z");
                if (BtnMax != null) BtnMax.ToolTip = max ? "还原" : "最大化";
                RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
                Eval("window.api.setMaximized && window.api.setMaximized(" + (max ? "true" : "false") + ");");
                var hwndState = new WindowInteropHelper(this).Handle;
                if (hwndState != IntPtr.Zero)
                    LogMsg("窗口状态 " + WindowState + " style=0x" + GetWindowLong(hwndState, GWL_STYLE).ToString("X8"));
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowClose)
            {
                // 默认：关窗口 = 退出程序（连托盘一起退）。
                // 只有设置里打开「关闭窗口时收进托盘」才拦下来，改成常驻后台。
                bool toTray = _store != null && _store.Settings != null && _store.Settings.CloseToTray;
                if (toTray)
                {
                    e.Cancel = true;
                    HideToTrayAnimated();
                    return;
                }
            }
            try { StopCursorClamp(); } catch { }
            try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnClosing(e);
        }

        /// <summary>数据目录切换后：重挂缩略图虚拟主机 + 重新载入数据并推给页面（不用重启）。</summary>
        private void ApplyDataRoot(string root)
        {
            try
            {
                var cw = Web == null ? null : Web.CoreWebView2;
                if (cw != null)
                {
                    try { cw.ClearVirtualHostNameToFolderMapping("thumbs.local"); } catch { }
                    var thumbs = _store.ThumbDir;
                    if (Directory.Exists(thumbs))
                        cw.SetVirtualHostNameToFolderMapping("thumbs.local", thumbs, CoreWebView2HostResourceAccessKind.Allow);
                }
                _bridge.PushAll();
                _bridge.PushState();
            }
            catch (Exception ex) { LogMsg("刷新数据目录失败 " + ex.Message); }
        }

        /// <summary>在页面里执行一段 JS（Bridge 回推数据用）。</summary>
        private void Eval(string js)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { Web.CoreWebView2.ExecuteScriptAsync(js); } catch { }
                }));
            }
            catch { }
        }

        // ================= 缺失补回：加载 / 热键 / 托盘 / 自启 / 按钮 =================

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int WM_HOTKEY = 0x0312;

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string logPath = Path.Combine(root, "bridge.log");

            Action<string> log = m =>
            {
                try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + Environment.NewLine); }
                catch { }
            };

            try
            {
                LogMsg = log;
                EnsureCaptionStyle();   // SourceInitialized 时日志还没接上，这里再补一次并留下记录
                InstallMessageLog();    // 只在带 --msglog 时生效
                log("版本 2790 正文预览展开 / 表格换行与右缘对齐 / 程序名改为随记");
            _store = new Store();
                _store.Load();
                log("Store 载入，笔记 " + _store.Db.Notes.Count + " 条，根目录 " + _store.Root);

                var udf = Path.Combine(root, "wv2data");
                // 曾试过 --force-renderer-accessibility 想修豆包语音输入（UIA 注入那条假设），
                // 实测无效：msedgewebview2.exe 命令行确认参数已生效，但语音输入仍进不来。
                // 页面探针显示按语音键后只有 2 个 keydown、之后零事件 —— 豆包走的是
                // WebView2 接收不到的通道，不是 a11y 的事。参数已回滚，别再试。
                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await Web.EnsureCoreWebView2Async(env);

                var cw = Web.CoreWebView2;
                string webDir = Path.Combine(root, "web");
                cw.SetVirtualHostNameToFolderMapping("app.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
                var thumbs = _store.ThumbDir;
                if (Directory.Exists(thumbs))
                    cw.SetVirtualHostNameToFolderMapping("thumbs.local", thumbs, CoreWebView2HostResourceAccessKind.Allow);

                cw.Settings.AreDefaultContextMenusEnabled = false;
                cw.Settings.IsStatusBarEnabled = false;
                cw.Settings.AreDevToolsEnabled = true;
                // 必须保持 true：WebView2 是一个独立 HWND，把它设成 false 后
                // 它会自己吃掉 WM_DROPFILES/OLE 拖放并显示「禁止」光标，
                // 事件不会冒泡到 WPF 窗口的 Drop，文件就永远拖不进来。
                // （2693 曾改成 false 想省掉 Base64，结果 2693~2695 全都拖不进文件。）
                Web.AllowExternalDrop = true;

                AllowDrop = true;
                DragOver += OnWindowDragOver;
                Drop += OnWindowDrop;

                _bridge = new Bridge(_store, Eval, log);
                _bridge.OnDataRootChanged = ApplyDataRoot;

                // 两机共用一个 OneDrive 目录时：切回本窗口就拉一次磁盘，别一直用启动时那份旧数据。
                // 只合并、不强制覆盖（同 id 取 UpdatedAt 新的），删除不同步（本期已知取舍）。
                Activated += (s5, e5) =>
                {
                    try
                    {
                        if (_store.PullFromDisk())
                        {
                            _bridge.PushAll();
                            _bridge.PushState();
                            _bridge.PushTrash();
                            log("磁盘数据有更新：已按 id 合并并重绘");
                        }
                    }
                    catch (Exception ex5) { log("合并磁盘失败 " + ex5.Message); }
                };

                _attDragHost = new AttDragHost(this, Web, _bridge.PathOfAtt, log, Eval,
                    attId => { try { _bridge.Handle("delatt|" + attId); } catch { } },
                    attId => { try { _bridge.Handle("copyfile|" + attId); } catch { } },
                    attId => { try { _bridge.Handle("reveal|" + attId); } catch { } },
                    (files, attId) =>
                    {
                        try { _bridge.AddDroppedPaths(files, _bridge.NoteIdOfAtt(attId)); }
                        catch (Exception ex) { log("缩略图接收文件失败 " + ex.Message); }
                    },
                    // --no-open：自动化验证专用，点缩略图不真的去启动图片查看器
                    Array.IndexOf(Environment.GetCommandLineArgs(), "--no-open") >= 0);
                _bridge.OnAttRects = _attDragHost.Update;
                _bridge.OnAttHide = _attDragHost.HideAllForScroll;
                _bridge.OnExtDrag = on => { try { _attDragHost.SetExternalDrag(on); } catch { } };
                _bridge.OnBatchMode = on => { try { _attDragHost.SetNoHotzones(on); } catch { } };
                _bridge.OnHotzones = on => { try { _attDragHost.SetNoHotzones(on); } catch { } };

                _bridge.OnDragGrip = (gy, ch, lt, lb) =>
                {
                    _grabY = gy;
                    _cardH = ch > 20 ? ch : 80;
                    _listTopCss = lt;
                    _listBottomCss = lb > 100 ? lb : 660;
                };
                _bridge.OnDragBegin = () =>
                {
                    try { if (_attDragHost != null) _attDragHost.SetPageDrag(true); } catch { }
                    StartDragWatch();
                };
                _bridge.OnDragEnd = () =>
                {
                    try { if (_attDragHost != null) _attDragHost.SetPageDrag(false); } catch { }
                    StopCursorClamp();
                };
                _bridge.OnTopmost = on =>
                {
                    Topmost = on;
                    _store.Settings.Topmost = on;
                    _store.SaveSettings();
                };
                _bridge.OnAutoStart = on =>
                {
                    _store.Settings.AutoStart = on;
                    _store.SaveSettings();
                    SetAutoStart(on, log);
                };
                _bridge.OnSetBackground = color =>
                {
                    try
                    {
                        var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color);
                        Background = brush;
                        RootBorder.Background = brush;
                        var mc = ((System.Windows.Media.SolidColorBrush)brush).Color;
                        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(mc.A, mc.R, mc.G, mc.B);
                    }
                    catch { }
                };

                cw.WebMessageReceived += (s2, e2) =>
                {
                    try { _bridge.Handle(e2.TryGetWebMessageAsString()); }
                    catch (Exception ex) { log("消息处理异常 " + ex.Message); }
                };

                // 失焦只放开光标约束，看门狗继续盯着左键 —— 不能把收尾也一起丢掉
                Deactivated += (s6, e6) => StopCursorClamp(true);

                InitHotkeyAndTray(log);
                if (_store.Settings.Topmost) Topmost = true;

                cw.NavigationCompleted += async (s4, e4) =>
                {
                    log("页面载入完成");
                    var args0 = Environment.GetCommandLineArgs();

                    // --eval <js>：在页面里执行任意脚本并把结果写进 bridge.log（行首 "EVAL "）。
                    // 调试拖动/交互时用来读真实状态，不用靠猜。可以有多个 --eval。
                    for (int ei = 0; ei + 1 < args0.Length; ei++)
                    {
                        if (args0[ei] != "--eval") continue;
                        await System.Threading.Tasks.Task.Delay(2000);
                        try
                        {
                            string js = args0[ei + 1];
                            // 支持 --eval @文件路径：命令行里塞带引号的 JS 会被 Start-Process 拆碎
                            if (js.StartsWith("@") && File.Exists(js.Substring(1)))
                                js = File.ReadAllText(js.Substring(1), System.Text.Encoding.UTF8);
                            string rr = await cw.ExecuteScriptAsync(js);
                            log("EVAL " + rr);
                        }
                        catch (Exception ex) { log("EVAL失败 " + ex.Message); }
                    }

                    int si = Array.IndexOf(args0, "--shot");
                    if (si >= 0 && si + 1 < args0.Length)
                    {
                        await System.Threading.Tasks.Task.Delay(2600);
                        try
                        {
                            string dbg = await cw.ExecuteScriptAsync(
                                "(function(){var c=document.querySelector('.note');var r=c?c.getBoundingClientRect():null;" +
                                "var ca=document.querySelector('.composer-area');var cr=ca?ca.getBoundingClientRect():null;" +
                                "var lw=document.querySelector('.list-wrap');var lr=lw?lw.getBoundingClientRect():null;" +
                                "return JSON.stringify({ver:window.__appjsVer||'none'," +
                                "notes:document.querySelectorAll('.note').length, vh:window.innerHeight," +
                                "card0:r?[Math.round(r.left),Math.round(r.top),Math.round(r.width),Math.round(r.height)]:null," +
                                "ring0:(function(){var g=document.querySelector('.note .ring');var gr=g?g.getBoundingClientRect():null;" +
                                "return gr?[Math.round(gr.left),Math.round(gr.top),Math.round(gr.width),Math.round(gr.height)]:null;})()," +
                                "composerBottom:cr?Math.round(cr.bottom):null," +
                                "listRect:lr?[Math.round(lr.top),Math.round(lr.bottom)]:null});})()");
                            log("截图前 DOM " + dbg);

                            // --shotbottom：先把列表滚到底，用于检查已完成态和附件区
                            if (Array.IndexOf(args0, "--shotbottom") >= 0)
                            {
                                await cw.ExecuteScriptAsync(
                                    "(function(){var w=document.querySelector('.list-wrap');if(w)w.scrollTop=w.scrollHeight;" +
                                    "var n=document.querySelectorAll('.note.done').length;return n;})()");
                                await System.Threading.Tasks.Task.Delay(700);
                            }
                            using (var fs = File.Create(args0[si + 1]))
                                await cw.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                            log("截图已保存 " + args0[si + 1]);
                        }
                        catch (Exception ex) { log("截图失败 " + ex.Message); }
                    }
                };

                cw.Navigate("https://app.local/index.html");
                log("已导航到 index.html");
            }
            catch (Exception ex)
            {
                log("启动失败 " + ex);
                try { File.WriteAllText(Path.Combine(root, "error.log"), ex.ToString()); } catch { }
            }
        }

        /// <summary>把已有的实例唤到前台（第二个实例启动时用）。</summary>
        private static void ShutdownExisting()
        {
            try
            {
                var hwnd = FindWindow(null, "随记");
                if (hwnd != IntPtr.Zero && WM_SHOW_INSTANCE != 0)
                {
                    // 让已有实例在它自己的 UI 线程里做显示动画；
                    // 直接 ShowWindow + SetForegroundWindow 会「啪」一下出现，没有过渡。
                    PostMessage(hwnd, WM_SHOW_INSTANCE, IntPtr.Zero, IntPtr.Zero);
                    SetForegroundWindow(hwnd);
                    return;
                }

                var procs = System.Diagnostics.Process.GetProcessesByName(
                    System.Diagnostics.Process.GetCurrentProcess().ProcessName);
                foreach (var pr in procs)
                {
                    if (pr.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
                    if (pr.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(pr.MainWindowHandle, 9);
                    SetForegroundWindow(pr.MainWindowHandle);
                    break;
                }
            }
            catch { }
        }

        private void InitHotkeyAndTray(Action<string> log)
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                _hwndSource = HwndSource.FromHwnd(helper.Handle);
                if (_hwndSource != null) _hwndSource.AddHook(WndProc);
                bool ok = RegisterHotKey(helper.Handle, HOTKEY_ID, 3, 0x4E);   // Ctrl+Alt+N
                log("全局热键 Ctrl+Alt+N 注册 " + (ok ? "成功" : "失败"));
            }
            catch (Exception ex) { log("热键注册异常 " + ex.Message); }

            try
            {
                _tray = new System.Windows.Forms.NotifyIcon
                {
                    Text = "随记",
                    Visible = true,
                    Icon = MakeTrayIcon()
                };
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("显示", null, (s1, e1) => RestoreFromTray());
                menu.Items.Add("退出", null, (s1, e1) => { _allowClose = true; Close(); });
                _tray.ContextMenuStrip = menu;
                _trayClickTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(220)
                };
                _trayClickTimer.Tick += (s1, e1) =>
                {
                    _trayClickTimer.Stop();
                    ToggleTrayWindowAnimated();
                };
                _tray.MouseClick += (s1, e1) =>
                {
                    if (e1.Button != System.Windows.Forms.MouseButtons.Left) return;
                    // 双击的第二次单击可能晚于 DoubleClick 到达；否则会把刚显示的窗口又收起。
                    if (DateTime.UtcNow < _trayIgnoreClickUntil) return;
                    _trayClickTimer.Stop();
                    _trayClickTimer.Start();
                };
                _tray.DoubleClick += (s1, e1) =>
                {
                    // 双击直接显示；同时取消单击等待，避免「显示后立刻又收起」。
                    _trayClickTimer.Stop();
                    _trayIgnoreClickUntil = DateTime.UtcNow.AddMilliseconds(400);
                    ShowFromTrayAnimated();
                };
                log("托盘已创建");
            }
            catch (Exception ex) { log("托盘创建失败 " + ex.Message); }
        }

        private static System.Drawing.Icon MakeTrayIcon()
        {
            // 托盘图标直接用 app.ico（和标题栏、任务栏同一个），
            // 以前是临时画的「绿圆+白勾」，和程序图标不是一套，看着像两个软件（用户 2026-11-10 反馈）。
            try
            {
                string ico = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(ico))
                {
                    using (var f = new System.Drawing.Icon(ico, 32, 32))
                        return (System.Drawing.Icon)f.Clone();
                }
            }
            catch { }

            var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(79, 191, 149)))
                    g.FillEllipse(b, 2, 2, 28, 28);
                using (var p = new System.Drawing.Pen(System.Drawing.Color.White, 3))
                {
                    g.DrawLine(p, 10, 16, 15, 21);
                    g.DrawLine(p, 15, 21, 23, 11);
                }
            }
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        private void RestoreFromTray()
        {
            ShowFromTrayAnimated();
        }

        private void ToggleTrayWindowAnimated()
        {
            if (IsVisible && WindowState != WindowState.Minimized)
                HideToTrayAnimated();
            else
                ShowFromTrayAnimated();
        }

        private void ShowFromTrayAnimated()
        {
            try
            {
                // 收起动画还没走完就又点开：取消「动画结束后 Hide」，直接显示
                if (_hideToTrayTimer != null)
                {
                    _hideToTrayTimer.Stop();
                    _hideToTrayTimer = null;
                }

                // 先让 HWND 可见（仍处于 minimized 状态），再交给系统还原，
                // DWM 才会从任务栏按钮播放标准的还原动画。
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized)
                    SystemCommands.RestoreWindow(this);

                var hwnd = new WindowInteropHelper(this).Handle;
                Activate();
                if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
                LogMsg("显示窗口（系统还原动画）");
            }
            catch { try { Show(); } catch { } }
        }

        private void HideToTrayAnimated()
        {
            try
            {
                if (!IsVisible) return;
                // 系统「最小化/最大化动画」被关闭时直接隐藏，不硬等
                if (!SystemParameters.MinimizeAnimation) { Hide(); return; }

                // 先最小化，让 DWM 播完系统的最小化动画，再隐藏窗口（收进托盘）
                SystemCommands.MinimizeWindow(this);
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(260)
                };
                _hideToTrayTimer = timer;
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    if (!ReferenceEquals(_hideToTrayTimer, timer)) return;
                    _hideToTrayTimer = null;
                    try { Hide(); } catch { }
                    LogMsg("收起窗口（系统最小化动画）");
                };
                timer.Start();
            }
            catch { try { Hide(); } catch { } }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_INPUT && _draggingSession)
            {
                try
                {
                    uint size = 0;
                    uint hdr = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE));
                    GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, RAWINPUT_HEADER_SIZE);
                    if (size > 0 && size < 512)
                    {
                        IntPtr buf = Marshal.AllocHGlobal((int)size);
                        try
                        {
                            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, RAWINPUT_HEADER_SIZE) == size)
                            {
                                int dyR = Marshal.ReadInt32(buf, 40);   // lLastY
                                _rawAccumY += dyR;

                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                catch { }
                handled = false;
                return IntPtr.Zero;
            }

            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                if (IsVisible && WindowState != WindowState.Minimized && IsActive)
                    SystemCommands.MinimizeWindow(this);
                else
                    RestoreFromTray();
                handled = true;
            }
            else if (msg == WM_MOVE && _mvOn)
            {
                long now = _mvSw.ElapsedMilliseconds;
                long gap = now - _mvLastMs;
                _mvLastMs = now;
                _mvFrames++;
                if (_mvFrames > 1)
                {
                    _mvGapSum += gap;
                    if (gap > _mvMaxGap) _mvMaxGap = gap;
                }
                try
                {
                    RECT wr; POINT pc;
                    if (GetWindowRect(hwnd, out wr) && GetCursorPos(out pc))
                    {
                        int lagX = Math.Abs((pc.X - _mvCx0) - (wr.Left - _mvWx0));
                        int lagY = Math.Abs((pc.Y - _mvCy0) - (wr.Top - _mvWy0));
                        int lag = Math.Max(lagX, lagY);
                        if (lag > _mvMaxLag) _mvMaxLag = lag;
                    }
                }
                catch { }
            }
            else if (msg == WM_NCLBUTTONDOWN && wParam.ToInt32() == HTCAPTION)
            {
                // 点标题栏的这一刻，系统还没进移动模态循环。两件事必须提前做：
                // 1) 释放上一段「拖待办」可能残留的光标约束。clip 还在时，
                //    8ms 定时器会对正在拖的窗口反复 ClipCursor —— 窗口越拖越掉队，
                //    日志里表现为「移动帧正常但最大滞后上千 px」。
                // 2) 提前收起附件热区，别让十几个分层窗的 Show/SetWindowPos
                //    落在起手那几帧上（之前要等 WM_ENTERSIZEMOVE 才收）。
                bool hadClip = _draggingSession;
                try { StopCursorClamp(false); } catch { }
                try { if (_attDragHost != null) _attDragHost.BeginOwnerMove(); } catch { }
                if (hadClip) LogMsg("拖窗开始：释放了残留的光标约束");
            }
            else if (msg == WM_ENTERSIZEMOVE)
            {
                // 拖动期间把附件热区整批收起：它们是分层透明窗，
                // 每帧跟着重排会让整个窗口拖起来发沉、不跟手。
                try { if (_attDragHost != null) _attDragHost.BeginOwnerMove(); } catch { }
                try
                {
                    _mvOn = true; _mvFrames = 0; _mvLastMs = 0;
                    _mvMaxGap = 0; _mvGapSum = 0; _mvMaxLag = 0;
                    _mvSw.Restart();
                    RECT w0; POINT p0;
                    GetWindowRect(hwnd, out w0);
                    GetCursorPos(out p0);
                    _mvWx0 = w0.Left; _mvWy0 = w0.Top;
                    _mvCx0 = p0.X; _mvCy0 = p0.Y;
                }
                catch { _mvOn = false; }
            }
            else if (msg == WM_EXITSIZEMOVE)
            {
                try { if (_attDragHost != null) _attDragHost.EndOwnerMove(); } catch { }
                if (_mvOn)
                {
                    _mvOn = false;
                    long dur = _mvSw.ElapsedMilliseconds;
                    long avg = _mvFrames > 1 ? _mvGapSum / (_mvFrames - 1) : 0;
                    LogMsg("拖窗 " + dur + "ms 移动帧=" + _mvFrames + " 平均=" + avg +
                           "ms 最大=" + _mvMaxGap + "ms 最大滞后=" + _mvMaxLag + "px");
                }
            }
            else if (WM_SHOW_INSTANCE != 0 && msg == (int)WM_SHOW_INSTANCE)
            {
                ShowFromTrayAnimated();
                handled = true;
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        /// <summary>开机自启：写 HKCU Run，无 UAC。</summary>
        private static void SetAutoStart(bool on, Action<string> log)
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string name = "TempNotes";
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, true))
                {
                    if (k == null) return;
                    if (on)
                    {
                        string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                        k.SetValue(name, "\"" + exe + "\"");
                    }
                    else k.DeleteValue(name, false);
                }
                log("开机自启 = " + on);
            }
            catch (Exception ex) { log("设置自启失败 " + ex.Message); }
        }

        private void BtnPick_Click(object sender, RoutedEventArgs e)
        {
            try { _bridge.Handle("pick"); } catch { }
        }

        private void BtnBatch_Click(object sender, RoutedEventArgs e)
        {
            Eval("window.api.toggleBatch && window.api.toggleBatch();");
        }
    }
}

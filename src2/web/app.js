/* ============================================================
   随记 v2 · 前端  (build 2026-11-08-1410)
   JS → C#：chrome.webview.postMessage("cmd|a|b…")
   C# → JS：window.api.*
   ============================================================ */
(function () {
  "use strict";

var APPJS_VER = "2026-11-10-2812";
  window.__appjsVer = APPJS_VER;

  /* ---------- DOM ---------- */
  var listEl = document.getElementById("list");
  var wrapEl = document.querySelector(".list-wrap");
  var topBtn = document.getElementById("totop");
  var doneBtn = document.getElementById("todone");
  var inputEl = document.getElementById("composer");
  var composerWrap = document.querySelector(".composer-wrap");
  var composerHint = document.getElementById("composerHint");
  var composerClear = document.getElementById("composerClear");
  var hintEl = document.getElementById("hint");
  var searchBar = document.getElementById("searchbar");
  var batchBar = document.getElementById("batchbar");
  var colorPop = document.getElementById("colorpop");
  var settingsPanel = document.getElementById("settings");
  var trashPanel = document.getElementById("trashpanel");
  var trashList = document.getElementById("trashlist");

  /* ---------- 状态 ---------- */
  var notes = [];
  var trash = [];
  var selectedId = null;
  var editingId = null;
  var sortable = null;
  var sortables = [];
  var hintTimer = null;
  var batchMode = false;
  var batchSel = {};
  var query = "";
var settings = { palette: 9, topmost: false, autoStart: false, rightDone: true, closeToTray: false };
  var lastOrderSnap = null;
  var undoStopTimer = null;

  /* ---------- 通信 ---------- */
  function send(m) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(m);
      return true;
    }
    return false;
  }

  /* 首次加载时 DOMContentLoaded 可能早于 WebView2 注入 chrome.webview，
     直接 send 会静默丢失（表现为 C# 收不到 ready、列表空白），所以启动消息带重试。 */
  function sendRetry(m, tries) {
    tries = tries || 0;
    if (send(m)) return;
    if (tries > 60) return;
    setTimeout(function () { sendRetry(m, tries + 1); }, 50);
  }

  /* v2812：把「用户正在编辑 / 正在拖拽」告诉 C#。
     C# 那边每秒会扫一次磁盘，但绝不能在用户打字、拖卡片、输入法组词时重绘，
     否则光标会跳、拖动会断。这里只在状态真的变化时才发一条消息，开销可忽略。 */
  var _busySent = false;
  function _isEditingNow() {
    var a = document.activeElement;
    if (!a) return false;
    if (a.tagName === "TEXTAREA" || a.tagName === "INPUT") return true;
    try { return !!(a.closest && a.closest("[contenteditable='true']")); } catch (e) { return false; }
  }
  function _syncBusy() {
    var busy = false;
    try { busy = _isEditingNow() || !!_sortDragging || !!_attDrag; } catch (e) { busy = _isEditingNow(); }
    if (busy === _busySent) return;
    _busySent = busy;
    try { send("busy|" + (busy ? "1" : "0")); } catch (e2) { }
  }
  function _syncBusySoon() { setTimeout(_syncBusy, 40); }
  document.addEventListener("focusin", _syncBusySoon, true);
  document.addEventListener("focusout", _syncBusySoon, true);
  document.addEventListener("compositionstart", _syncBusySoon, true);
  document.addEventListener("compositionend", _syncBusySoon, true);
  document.addEventListener("mouseup", _syncBusySoon, true);
  document.addEventListener("keyup", function () { setTimeout(_syncBusy, 250); }, true);
  window.addEventListener("blur", function () { setTimeout(_syncBusy, 120); });

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", function (e) {
      var mm = String(e.data == null ? "" : e.data);

      if (mm.indexOf("vcursor|") === 0) {
        var vv = parseFloat(mm.split("|")[1]);
        if (!isNaN(vv)) _virtualY = vv;
        return;
      }
      // C# 发现左键已经松开（可能松在窗口外，Chromium 收不到 mouseup），
      // 补一次合成事件让 SortableJS 正常收尾，否则拖动状态会卡死。
      // 注意：本项目的 Sortable 走 PointerEvent（supportPointer=true），
      // 它监听的是 pointerup，不是 mouseup —— 只补 mouseup 收不了尾，
      // 这正是「松手了还在滑、底边红框一直闪」的根因。
      if (mm === "mouseup") {
        if (_sortDragging) forceFinishSortDrag();
        else {
          try {
            document.dispatchEvent(new MouseEvent("mouseup", { bubbles: true, cancelable: true }));
          } catch (err) {}
        }
      }
    });
  }

  /* ---------- 小工具 ---------- */
  function on(id, fn) { var el = document.getElementById(id); if (el) el.onclick = fn; return el; }
  function showHint(t, ms) {
    if (!hintEl) return;

    hintEl.classList.add("show");
    hintEl.textContent = t;

    // 用 Web Animations API：每次都是新建一个动画实例 → 一定会重播。
    // 之前靠 class 重排触发 CSS animation，连点几次后浏览器会合并样式变更，动画就不播了。
    try {
      if (hintEl._anim) { hintEl._anim.cancel(); hintEl._anim = null; }
      hintEl._anim = hintEl.animate(
        [
          { opacity: 0, transform: "translateX(-50%) translateY(12px) scale(.95)" },
          { opacity: 1, transform: "translateX(-50%) translateY(0) scale(1)" }
        ],
        { duration: 340, easing: "cubic-bezier(.22,1,.36,1)", fill: "none" }
      );
    } catch (e) {}

    if (hintTimer) clearTimeout(hintTimer);
    hintTimer = setTimeout(function () {
      hintEl.classList.remove("show");
    }, ms || 1800);
  }

  function esc(s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;" }[c];
    });
  }
  function fmtTime(ms) {
    if (!ms) return "";
    var d = new Date(ms);
    return String(d.getHours()).padStart(2,"0") + ":" + String(d.getMinutes()).padStart(2,"0");
  }
  function fmtStamp(ms) {
    if (!ms) return "";
    var d = fmtDay(ms), t = fmtTime(ms);
    return d ? (d + " " + t) : t;
  }

  function searchTerms(q) {
    return String(q == null ? "" : q)
      .normalize("NFKC")
      .toLowerCase()
      .replace(/[\u0300-\u036f]/g, "")
      .trim()
      .split(/\s+/)
      .filter(function (x, i, arr) { return x && arr.indexOf(x) === i; });
  }
  function noteHaystack(n) {
    var parts = [n.text || ""];
    (n.atts || []).forEach(function (a) { if (a && a.name) parts.push(a.name); });
    return parts.join("\n").normalize("NFKC").toLowerCase().replace(/[\u0300-\u036f]/g, "");
  }
  function noteMatches(n, terms) {
    if (!terms || !terms.length) return true;
    var hay = noteHaystack(n);
    for (var i = 0; i < terms.length; i++) if (hay.indexOf(terms[i]) < 0) return false;
    return true;
  }
  function rxEsc(s) {
    return String(s).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }
  /* ---------- Markdown 渲染（常用子集） ----------
     顺序很重要：先整段转义，再把 ``` 围栏块 / `行内代码` 抽成占位符，
     否则里面的 * _ # > 会被后面的大胆/斜体/块级规则吃掉。
     支持：# 标题 1-6、**粗** __粗__、*斜* _斜_、~~删除~~、`代码`、
           ```围栏代码块```、> 引用、- / * / + 无序、1. 有序、
           - [ ] / - [x] 任务项、--- 分隔线、[文字](http…) 链接。
     搜索态（query 非空）仍然走 hlText 的纯文本高亮 —— 高亮要按字符定位，
     套在解析后的 HTML 上会错位。 */
  function mdHtml(t) {
    var stash = [];
    function keep(html) {
      var k = "\u0000" + stash.length + "\u0000";
      stash.push(html);
      return k;
    }
    var s = esc(t == null ? "" : t);

    // 1) 围栏代码块
    s = s.replace(/```([a-zA-Z0-9_+-]*)\n([\s\S]*?)```/g, function (m, lang, code) {
      var body = code.replace(/\n$/, "");
      return keep('<pre class="md-pre"><code>' + body + "</code></pre>");
    });
    // 2) 行内代码
    s = s.replace(/`([^`\n]+)`/g, function (m, c) {
      return keep('<code class="md-inline">' + c + "</code>");
    });

    function inline(x) {
      return x
        .replace(/\*\*([^*\n]+)\*\*/g, "<b>$1</b>")
        .replace(/__([^_\n]+)__/g, "<b>$1</b>")
        .replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<i>$2</i>")
        .replace(/(^|[^_])_([^_\n]+)_/g, "$1<i>$2</i>")
        .replace(/~~([^~\n]+)~~/g, "<del>$1</del>")
        .replace(/&lt;(https?:\/\/[^\s&]+)&gt;/g,
          '<a class="md-a" data-href="$1" title="$1">$1</a>')
        .replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g,
          '<a class="md-a" data-href="$2" title="$2">$1</a>');
    }

    var lines = s.split("\n");
    var out = [], list = null, buf = [];
    function closeList() {
      if (!list) return;
      var tag = list.task ? "ul" : list.tag;
      out.push("<" + tag + ' class="' + (list.task ? "md-tasks" : "md-list") + '">' +
        list.items.join("") + "</" + tag + ">");
      list = null;
    }
    function flushPara() {
      if (!buf.length) return;
      out.push("<p>" + buf.join("<br>") + "</p>");
      buf = [];
    }
    function ensureList(tag, task, nested) {
      if (nested && list) { list.nest = true; return; }
      if (!list || list.tag !== tag || list.task !== task) {
        closeList();
        list = { tag: tag, task: task, items: [], nest: false };
      } else list.nest = false;
    }
    function pushLi(html) {
      if (!list) return false;
      if (list.nest && list.items.length) {
        // 缩进 2 空格以上的项挂进上一个 <li>，只支持一层（再深也只当同一层）
        var prev = list.items[list.items.length - 1];
        if (/<\/ul><\/li>$/.test(prev)) {
          // 上一条已经带嵌套列表 → 继续往那个 <ul> 里追加，别开第二个
          list.items[list.items.length - 1] = prev.replace(/<\/ul><\/li>$/, html + "</ul></li>");
        } else {
          list.items[list.items.length - 1] =
            prev.replace(/<\/li>$/, '<ul class="md-list">' + html + "</ul></li>");
        }
        return true;
      }
      list.items.push(html);
      return true;
    }

    /* ---- GFM 表格 ---- */
    function isSepRow(row) {
      var t = String(row).trim();
      if (t.indexOf("-") < 0) return false;
      return /^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?$/.test(t);
    }
    function rowCells(row) {
      var t = String(row).trim();
      if (t.charAt(0) === "|") t = t.slice(1);
      if (t.charAt(t.length - 1) === "|") t = t.slice(0, -1);
      return t.split("|").map(function (c) { return c.trim(); });
    }
    function alignOf(c) {
      var l = c.charAt(0) === ":", r = c.charAt(c.length - 1) === ":";
      return l && r ? "center" : r ? "right" : l ? "left" : "";
    }
    function tableHtml(headRow, sepRow, bodyRows) {
      var hs = rowCells(headRow), al = rowCells(sepRow).map(alignOf);
      var h = "<thead><tr>";
      for (var q = 0; q < hs.length; q++) {
        h += "<th" + (al[q] ? ' style="text-align:' + al[q] + '"' : "") + ">" +
          inline(hs[q]) + "</th>";
      }
      h += "</tr></thead><tbody>";
      for (var r = 0; r < bodyRows.length; r++) {
        var cs = rowCells(bodyRows[r]);
        h += "<tr>";
        for (var c2 = 0; c2 < hs.length; c2++) {
          h += "<td" + (al[c2] ? ' style="text-align:' + al[c2] + '"' : "") + ">" +
            inline(cs[c2] || "") + "</td>";
        }
        h += "</tr>";
      }
      return '<div class="md-tblwrap"><table class="md-tbl">' + h + "</tbody></table></div>";
    }

    for (var i = 0; i < lines.length; i++) {
      var raw = lines[i].replace(/\s+$/, "");
      if (!raw.trim()) { flushPara(); closeList(); continue; }

      // 单独占一行的代码块占位符：必须当块级元素直接输出。
      // 塞进 <p> 里的话浏览器会自动闭合 <p>，留下一个空段落带 margin，块之间凭空多一条空隙。
      var mph = /^\u0000(\d+)\u0000$/.exec(raw.trim());
      if (mph) { flushPara(); closeList(); out.push(stash[+mph[1]] || ""); continue; }

      // 缩进（2 空格 = 嵌套一层）
      var ind = raw.match(/^\s*/)[0].replace(/\t/g, "  ").length;
      var ln = ind ? raw.replace(/^\s+/, "") : raw;
      var nested = ind >= 2;

      // 表格：当前行含 |，下一行是分隔行
      if (ln.indexOf("|") >= 0 && i + 1 < lines.length && isSepRow(lines[i + 1])) {
        flushPara(); closeList();
        var tbody = [], tj = i + 2;
        while (tj < lines.length) {
          var nl = lines[tj];
          if (!nl.trim() || nl.indexOf("|") < 0) break;
          tbody.push(nl); tj++;
        }
        out.push(tableHtml(ln, lines[i + 1], tbody));
        i = tj - 1;
        continue;
      }

      if (!nested) {
        var mh = /^(#{1,6})\s+(.*?)\s*#*$/.exec(ln);
        if (mh) {
          flushPara(); closeList();
          var lv = mh[1].length;
          out.push("<h" + lv + ' class="md-h">' + inline(mh[2]) + "</h" + lv + ">");
          continue;
        }
        if (/^(-{3,}|\*{3,}|_{3,})$/.test(ln.trim())) {
          flushPara(); closeList();
          out.push('<hr class="md-hr">');
          continue;
        }
        var mq = /^&gt;\s?(.*)$/.exec(ln);
        if (mq) {
          flushPara(); closeList();
          out.push('<blockquote class="md-q">' + inline(mq[1]) + "</blockquote>");
          continue;
        }
      }

      var mt = /^[-*+]\s+\[([ xX])\]\s+(.*)$/.exec(ln);
      if (mt) {
        flushPara();
        ensureList("ul", true, nested && list && list.task);
        var done = mt[1] !== " ";
        pushLi('<li class="md-task' + (done ? " done" : "") + '">' +
          '<i class="md-box">' + (done ? "✓" : "") + "</i><span>" + inline(mt[2]) + "</span></li>");
        continue;
      }
      var mu = /^[-*+]\s+(.*)$/.exec(ln);
      if (mu) {
        flushPara();
        ensureList("ul", false, nested && list && !list.task);
        pushLi("<li>" + inline(mu[1]) + "</li>");
        continue;
      }
      var mo = /^(\d+)[.)]\s+(.*)$/.exec(ln);
      if (mo) {
        flushPara();
        ensureList("ol", false, nested && list && list.tag === "ol");
        pushLi("<li>" + inline(mo[2]) + "</li>");
        continue;
      }
      closeList();
      buf.push(inline(ln));
    }
    flushPara(); closeList();

    var html = out.join("");
    html = html.replace(/\u0000(\d+)\u0000/g, function (m, idx) { return stash[+idx] || ""; });
    return html;
  }

  function hlText(t, q) {
    var terms = String(q == null ? "" : q).trim().split(/\s+/)
      .filter(function (x, i, arr) { return x && arr.indexOf(x) === i; })
      .sort(function (a, b) { return b.length - a.length; });
    if (!terms.length) return esc(t);
    var re = new RegExp("(" + terms.map(rxEsc).join("|") + ")", "gi");
    var s = String(t), out = "", last = 0, m;
    while ((m = re.exec(s)) !== null) {
      if (m[0].length === 0) { re.lastIndex++; continue; }
      out += esc(s.slice(last, m.index));
      out += '<mark class="hl">' + esc(m[0]) + '</mark>';
      last = m.index + m[0].length;
    }
    return out + esc(s.slice(last));
  }

  function fmtDay(ms) {
    if (!ms) return "";
    var d = new Date(ms), n = new Date();
    if (d.getFullYear()===n.getFullYear() && d.getMonth()===n.getMonth() && d.getDate()===n.getDate()) return "";
    return (d.getMonth()+1) + "/" + d.getDate();
  }
  function find(id) {
    for (var i = 0; i < notes.length; i++) if (notes[i].id === id) return notes[i];
    return null;
  }

  /* ---------- 拖动活动区：上 = 输入框下沿，下 = 列表底 ---------- */
  function dragBounds() {
    var lt = 0, lb = window.innerHeight;
    try {
      var ca = document.querySelector(".composer-area");
      if (ca) lt = Math.round(ca.getBoundingClientRect().bottom);
      else if (wrapEl) lt = Math.round(wrapEl.getBoundingClientRect().top);
    } catch (e) { }
    try {
      if (wrapEl) lb = Math.round(wrapEl.getBoundingClientRect().bottom);
    } catch (e2) { }
    if (lb <= lt) lb = lt + 200;
    return { top: lt, bottom: lb };
  }

  /* ---------- 拖动时卡片允许的活动范围：只在自己的 zone 内 ---------- */
  function dragZoneBounds() {
    var bd = dragBounds();
    if (!_dragZoneEl) return bd;
    try {
      var r = _dragZoneEl.getBoundingClientRect();
      var top = Math.max(bd.top, Math.round(r.top));
      var bottom = Math.min(bd.bottom, Math.round(r.bottom));
      if (bottom - top < 40) return bd;    // zone 太矮就别钳了，避免卡死
      return { top: top, bottom: bottom };
    } catch (e) { return bd; }
  }

  /* ---------- 拖动：边缘滚动（距离衰减 + 时间衰减）---------- */
  var _sortDragging = false;
  var _edgeRaf = 0;
  var _scrollVel = 0;        // 当前滚动速度 px/s（带平滑，避免生硬的开关感）
  var _lastTs = 0;
  var _lastClientY = null;   // 最近一次真实 mousemove 的 Y（滚动意图的主依据）
  var _lastClientX = null;   // 同上，X（合成 pointerup 需要真实坐标，不能用 0,0）
  var _virtualY = null;      // C# 送的原始位移推出来的 Y（只在没有真实光标时兜底）
  var _mvN = 0;
  var _grabY = 0;            // 按下时鼠标在卡片内的 Y 偏移（抓取点）
  var _cardH = 0;
  var _dragDoneState = false;
  var _dragZoneEl = null;
  var _lastFbTop = null;
  var _qTimer = null;     // 搜索防抖计时器
  var _pressY = null;
  var _dragStartCenterY = null;   // 按下时卡片中心在视口里的 Y（自动滚动的距离基准）
  var _dragStartFbTop = null;     // 按下时幽灵的视口 top（用来防止贴边卡片被钳制拽走）
  var _dragHomeParent = null;     // 按下时卡片在哪个容器（拖到别的区要还原到这里）
  var _dragHomeNext = null;       // 按下时卡片的下一个兄弟（还原顺序用）
  var _dragHomeOrder = null;      // 按下时该区所有卡片的 id 顺序（回弹时按它重排，见 restoreHomeOrder）
  var _dragItem = null;           // 本次被拖的卡片（原生层吞掉 mouseup 时兜底复位）
  var _dragInst = null;           // 本次拖动用的 Sortable 实例（强制收尾要清它的内部状态）
  var _dragStartedAt = 0;         // 本次拖动开始时间（太早的 buttons=0 不算松手）
  var _dragMvN = 0;               // 本次拖动的 mousemove 帧数
  var _dragMvLast = 0;            // 上一帧时间
  var _dragMvMaxGap = 0;          // 最大帧间隔（判断「拖不动」是不是页面收不到移动）
  var _dragListTop = 0, _dragListBottom = 0, _dragListH = 0;   // 拖动开始时缓存，避免每帧读布局
  var _limitCls = "";      // 当前边界反馈："" | "limit-top" | "limit-bottom"
  var _limitDbgDone = false;   // 本次拖动是否已记录「顶到上边界」的几何诊断（只记一次）
  var _boundsDumped = false;   // 本次拖动是否已记录活动区/窗口尺寸诊断
  var _dbgN = 0;      // 按下瞬间的意图坐标（滚动的起点基准）   // 拖动中 fallback 的最后位置，用于松手回弹   // 本次拖动所在的 zone（卡片不许离开它）   // 本次被拖卡片是否已完成            // 被抓卡片高度

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", function (e) {
      var m = String(e.data == null ? "" : e.data);
      if (m.indexOf("vcursor|") === 0) {
        var parts = m.split("|");
        var yy = parseFloat(parts[1]);
        _virtualY = isNaN(yy) ? _virtualY : yy;
      }
    });
  }

  /* 滚动意图只看「真实光标」，Raw Input 的 _virtualY 只做兜底。
     Raw Input 的位移是设备计数，和屏幕像素不是 1:1（DPI/指针加速都会放大），
     一旦让它抢优先级，手在正常移动、列表却不动。真实光标已经足够：
     指针停在边缘区时 rAF 会按最后位置持续滚，不需要「继续往外推」的额外信号。 */
  function pointerY() {
    if (_lastClientY != null) return _lastClientY;
    if (_virtualY != null) return _virtualY;
    var fb = document.querySelector(".sortable-fallback");
    if (fb) {
      var b = fb.getBoundingClientRect();
      return b.top + b.height / 2;
    }
    return null;
  }

  /* 自动滚动 = react-beautiful-dnd（Atlassian）那一套，业内最成熟的方案。
     模型：以「被拖卡片在视口里的中心点」到滚动容器上下沿的距离为准，
       · 距边 > 25% 容器高        → 不滚
       · 距边 25% → 5% 之间       → 二次缓动加速（t²）
       · 距边 < 5%                → 满速 28px/帧 ≈ 1680px/s
     本项目额外两条约束：
       · 方向门槛：只按「拖动方向那一侧」的边算（从最顶上往下拖不会被判成要往上滚）
       · 距离基准用「手的位置」——卡片贴边后就钳住了，不能再拿它判断意图 */
  var RB_SCROLL = { startFrom: 0.25, maxAt: 0.05, maxPxPerFrame: 18, minV: 40 };
  // 边缘滚动的「启动延迟」：卡片本来就在边上时，指针按下就已经贴着手位，
  // dist 只有几像素甚至为负 → 直接满速，用户感觉是「一按住稍微一拖就飞出去」。
  // 现在要求指针在触发区里持续待够 EDGE_HOLD_MS 才真的开始滚（真要继续往外推）。
  var EDGE_HOLD_MS = 120;
  var _edgeHoldAt = 0;      // 指针进入触发区的时刻（0 = 当前不在触发区）
  var _edgeHoldDir = 0;     // 进入时的方向（换方向要重新计时）

  function scrollTargetSpeed() {
    if (!wrapEl) return 0;

    var r = { top: _dragListTop, bottom: _dragListBottom, height: _dragListH };
    // 滚动意图必须用「手的位置」，不能用被钳制后的卡片位置：
    // 卡片贴到列表底就停住了，卡片中心不再变化 → 永远判不出「还在往下推」→ 列表不滚 → 表现为拖不动。
    var cy = pointerY();
    if (cy == null) return 0;

    // 方向必须按「手在滚动容器里的上半/下半」判断，**不能**用「相对按下点的位移」：
    // 把卡片从列表顶部拖到底部、再往回拖时，手会一路退回按下点，
    // dir = cy - _dragStartCenterY 恰好归零；而光标被 ClipCursor 钉在按下点附近上不去，
    // dir 永远到不了负值 ⇒ 表现就是「往上拖只在当前这一屏里能动，列表再也不滚」。
    // （用户实测：第一条带附件的卡片拖到底部后，只能拖到本屏顶部，再往上不滚。）
    var mid = (r.top + r.bottom) / 2;
    var dir = (cy < mid) ? -1 : 1;

    var h = r.height || 1;
    var startFrom = h * RB_SCROLL.startFrom;      // 654px 的列表 → 163px
    var maxAt = h * RB_SCROLL.maxAt;              // → 33px
    // 距离必须减掉「抓取点偏移 grabY」：
    // 卡片被钳在列表上沿时，手的位置是 listTop+grabY，而不是 listTop。
    // 以前直接用 cy-r.top，抓得越靠下（带附件的高卡片很常见）算出的距离越大，
    // 抓到 160px 以下时永远进不了触发区 —— 表现就是「往上拖根本不滚 / 拖不动」。
    // 下沿同理：卡片底边贴住 listBottom-keep 时，手在 listBottom-keep+grabY。
    var grab = _grabY || 0;
    var keep = Math.min(_cardH || 56, 56);
    // 下沿的「贴边手位」可能算到窗口外面去（抓得越靠下越离谱）：
    // 光标被 ClipCursor 关在窗口里，到不了那个位置 → 距离永远很大 → 滚速恒为 0，
    // 表现就是「抓着卡片下半部分往下拖，卡片顶到窗口底就拖不动了」。
    // 所以贴边手位必须再夹一次到「窗口内可达」的位置。
    var upEdge = Math.max(r.top + grab, 8);
    var downEdge = Math.min(r.bottom - keep + grab, (window.innerHeight || 800) - 8);
    if (downEdge < upEdge) downEdge = upEdge;
    var dist = (dir > 0) ? (downEdge - cy) : (cy - upEdge);
    if (dist < 0) dist = 0;                       // 指针已越过贴边手位：按贴边算，别让负距离放大判定
    if (dist > startFrom) { _edgeHoldAt = 0; _edgeHoldDir = 0; return 0; }
    // 方向换了要重新计时：从上边界滑到下边界时不能沿用一个早就在跑的时间戳
    if (_edgeHoldDir !== dir) { _edgeHoldDir = dir; _edgeHoldAt = 0; }
    if (!_edgeHoldAt) { _edgeHoldAt = Date.now(); return 0; }   // 刚进触发区：先不动
    if (Date.now() - _edgeHoldAt < EDGE_HOLD_MS) return 0;      // 还在启动延迟内

    var maxV = RB_SCROLL.maxPxPerFrame * 60;
    var sign = (dir > 0) ? 1 : -1;
    if (dist <= maxAt) return sign * maxV;

    var pz = 1 - (dist - maxAt) / (startFrom - maxAt);   // 0（刚进触发区）→ 1（贴边）
    var v = pz * pz * maxV;
    if (v < RB_SCROLL.minV) v = RB_SCROLL.minV;          // 别卡在边界上不动
    return sign * v;
  }

  function scrollStep(ts) {
    if (!_sortDragging) { _edgeRaf = 0; _scrollVel = 0; _lastTs = 0; return; }
    _edgeRaf = requestAnimationFrame(scrollStep);
    if (!_lastTs) { _lastTs = ts; return; }
    var dt = (ts - _lastTs) / 1000;
    _lastTs = ts;
    if (dt <= 0) return;
    if (dt > 0.1) dt = 0.1;                    // 掉帧/切后台别一步跳很远

    var target = scrollTargetSpeed();
    // 只做很轻的平滑（0.08s），保证「位置 → 速度」是即时、可预期的；
    // rbd 本身是每帧直接取值，过度平滑会让人放不准。
    var k = 1 - Math.pow(0.0015, dt / 0.08);
    _scrollVel += (target - _scrollVel) * k;
    if (Math.abs(_scrollVel) < 3) { _scrollVel = 0; return; }
    if (!wrapEl) return;

    var maxTop = Math.max(0, wrapEl.scrollHeight - wrapEl.clientHeight);
    var want = wrapEl.scrollTop + _scrollVel * dt;
    if (want <= 0) { want = 0; _scrollVel = 0; }
    else if (want >= maxTop) { want = maxTop; _scrollVel = 0; }
    wrapEl.scrollTop = want;
  }

  function startEdgeScroll() {
    if (_edgeRaf) return;
    _scrollVel = 0; _lastTs = 0;
    _edgeHoldAt = 0; _edgeHoldDir = 0;
    _edgeRaf = requestAnimationFrame(scrollStep);
  }
  function stopEdgeScroll() {
    if (_edgeRaf) { cancelAnimationFrame(_edgeRaf); _edgeRaf = 0; }
    _scrollVel = 0; _lastTs = 0;
    _edgeHoldAt = 0; _edgeHoldDir = 0;
  }

  /* ---------- 拖附件 / 外部文件时的边缘自动滚动 ----------
     拖待办条本来就有边缘滚动；拖附件和从资源管理器拖文件进来以前完全没有，
     指针顶到列表上下沿也不会滚，想放到这一屏以外的待办就没办法了。

     速度模型和上面 rbd 那套一致（越贴边越快，二次缓动），只是去掉了
     「按拖动方向判定」那一条：拖文件时手指可能已经越过边界，方向判定会误伤。 */
  var _edgeDragRaf = 0, _edgeDragVel = 0, _edgeDragLastTs = 0;
  var _edgeDragPt = null;         // {x,y} 指针（视口坐标）
  var _edgeDragOn = false;
  var _edgeDragRefresh = null;    // 滚完一帧后重算落点（内容动了，指针没动）

  /* 驱动是否还有效：拖附件 / 拖外部文件。拖拽结束（或事件丢失）后一定要停，
     否则这个 rAF 会一直拿旧指针位置去写 scrollTop，和用户的滚动互相拉扯。 */
  function edgeDragValid() {
    return !!(_attDrag || _extOn);
  }

  function edgeDragSpeed(x, y) {
    if (!wrapEl) return 0;
    var r = wrapEl.getBoundingClientRect();
    if (x < r.left || x > r.right + 10) return 0;   // 指针不在列表这一列，不滚
    var h = r.height || 1;
    var startFrom = h * 0.18;
    var maxAt = h * 0.05;
    var dTop = y - r.top, dBot = r.bottom - y;
    if (dTop > startFrom && dBot > startFrom) return 0;
    var sign = (dTop <= dBot) ? -1 : 1;
    var dist = Math.max(0, Math.min(dTop, dBot));
    var maxV = 1800;                                 // px/s
    if (dist <= maxAt) return sign * maxV;
    var pz = 1 - (dist - maxAt) / (startFrom - maxAt);
    return sign * Math.max(pz * pz * maxV, 60);
  }

  function edgeDragStep(ts) {
    if (!_edgeDragOn || !_edgeDragPt || !wrapEl) {
      _edgeDragRaf = 0; _edgeDragVel = 0; _edgeDragLastTs = 0; return;
    }
    if (!edgeDragValid()) { edgeDragStop(); return; }
    // 外部拖入如果已经 700ms 没有 dragover（Esc 取消、落到窗口外），
    // 说明拖拽其实结束了，只是页面没收到 dragend —— 立刻收手，别拿旧指针拉滚动。
    if (!_attDrag && _extOn && _lastDragOverAt && (Date.now() - _lastDragOverAt > 700)) {
      cleanupDropUi();
      edgeDragStop();
      return;
    }
    _edgeDragRaf = requestAnimationFrame(edgeDragStep);
    if (!_edgeDragLastTs) { _edgeDragLastTs = ts; return; }
    var dt = (ts - _edgeDragLastTs) / 1000;
    _edgeDragLastTs = ts;
    if (dt <= 0) return;
    if (dt > 0.1) dt = 0.1;

    var target = edgeDragSpeed(_edgeDragPt.x, _edgeDragPt.y);
    var k = 1 - Math.pow(0.0015, dt / 0.08);
    _edgeDragVel += (target - _edgeDragVel) * k;
    if (Math.abs(_edgeDragVel) < 3) { _edgeDragVel = 0; return; }

    var maxTop = Math.max(0, wrapEl.scrollHeight - wrapEl.clientHeight);
    var want = wrapEl.scrollTop + _edgeDragVel * dt;
    var clamped = false;
    if (want <= 0) { want = 0; _edgeDragVel = 0; clamped = true; }
    else if (want >= maxTop) { want = maxTop; _edgeDragVel = 0; clamped = true; }
    if (Math.abs(want - wrapEl.scrollTop) < 0.5) return;
    wrapEl.scrollTop = want;
    if (!clamped && _edgeDragRefresh) {
      try { _edgeDragRefresh(); } catch (e) { }
    }
  }

  function edgeDragStart(refresh, x, y) {
    _edgeDragRefresh = refresh;
    if (!_edgeDragPt) _edgeDragPt = { x: 0, y: 0 };   // 复用一个对象，别每次 mousemove 都造
    _edgeDragPt.x = x; _edgeDragPt.y = y;
    _edgeDragOn = true;
    if (!_edgeDragRaf) {
      _edgeDragVel = 0; _edgeDragLastTs = 0;
      _edgeDragRaf = requestAnimationFrame(edgeDragStep);
    }
  }
  function edgeDragStop() {
    _edgeDragOn = false;
    _edgeDragRefresh = null;
    _edgeDragPt = null;
    if (_edgeDragRaf) { cancelAnimationFrame(_edgeDragRaf); _edgeDragRaf = 0; }
    _edgeDragVel = 0; _edgeDragLastTs = 0;
  }

  /* ---------- 拖动：被拖卡片位置钳制 ---------- */
  var _fbTopAdj = 0;          // 当前给 fallback 额外加的纵向偏移（只写 style.top）
  var _fbCur = null;          // 本帧的 fallback 元素，换元素时把偏移复位
  var _fbBase = 0;            // SortableJS 写的原始 style.top（px）
  var _fbShift = 0;           // 折叠超高卡片时用 marginTop 做的整体位移（负值=上移）
  var _fbFixedLeft = 0;       // 幽灵钉死的那一列（视口 left）

  /** 从 SortableJS 写的 transform 里取纵向位移（它只用 matrix/translate3d 移动卡片） */
  /* 把 Sortable 写在 transform 里的横向位移清零（纵向不动）。
     比「每帧反写 style.left」干净：这是同一个属性、同一个任务里覆盖，
     不会出现「rAF 还没跑，浏览器已经把卡片画歪了一帧」的那种轻微左右漂。 */
  function lockFbX(fb) {
    if (!fb) return;
    var tf = fb.style.transform || "";
    var m = /matrix\(([^)]+)\)/.exec(tf);
    if (m) {
      var v = m[1].split(",").map(parseFloat);
      if (v.length >= 6 && v[4] !== 0) {
        v[4] = 0;
        fb.style.transform = "matrix(" + v.join(",") + ")";
      }
      return;
    }
    var m2 = /translate3d\(([^)]+)\)/.exec(tf);
    if (m2) {
      var p3 = m2[1].split(",");
      if (p3.length >= 2) fb.style.transform = "translate3d(0px," + p3[1] + "," + (p3[2] || "0px") + ")";
      return;
    }
    var m3 = /translate\(([^)]+)\)/.exec(tf);
    if (m3) {
      var p2 = m3[1].split(",");
      if (p2.length >= 2) fb.style.transform = "translate(0px," + p2[1] + ")";
    }
  }

  function fbTransformX(fb) {
    var tf = fb.style.transform || "";
    var m = /matrix\(([^)]+)\)/.exec(tf);
    if (m) {
      var v = m[1].split(",").map(parseFloat);
      return v.length >= 6 ? v[4] : 0;
    }
    var m2 = /translate(?:3d)?\(([^)]+)\)/.exec(tf);
    if (m2) {
      var p2 = m2[1].split(",").map(parseFloat);
      return p2.length >= 2 ? p2[0] : 0;
    }
    return 0;
  }

  function fbTransformY(fb) {
    var tf = fb.style.transform || "";
    var m = /matrix\(([^)]+)\)/.exec(tf);
    if (m) {
      var v = m[1].split(",").map(parseFloat);
      return v.length >= 6 ? v[5] : 0;
    }
    var m2 = /translate(?:3d)?\(([^)]+)\)/.exec(tf);
    if (m2) {
      var p2 = m2[1].split(",").map(parseFloat);
      return p2.length >= 2 ? p2[1] : 0;
    }
    return 0;
  }

  function forceFallbackPos(fb, rect) {
    if (!fb) return rect || null;

    var rb0 = rect || fb.getBoundingClientRect();
    if (fb !== _fbCur) {
      _fbCur = fb; _fbTopAdj = 0;
      var t0 = parseFloat(fb.style.top);
      _fbBase = isNaN(t0) ? 0 : t0;
      // 幽灵的「左边那一列」= 它布局位置（style.left，不含 transform），之后一直钉住
      var lf = parseFloat(fb.style.left);
      _fbFixedLeft = isNaN(lf) ? rb0.left : lf;
      try { fb.style.left = _fbFixedLeft + "px"; } catch (e0) { }
      lockFbX(fb);
      // 第一次看到幽灵时记录它「按下那一刻」的视口位置。
      if (_dragStartFbTop == null) _dragStartFbTop = rb0.top;
      _fbShift = 0;
    }

    lockFbX(fb);
    var h = rb0.height || 1;
    // 原始位置只由 SortableJS 的 transform 决定 —— 不读上一次自己写过的 top，
    // 否则「先钳制、再往回拖」会差 2px（会被人眼看到）。
    var logicalTop = _fbBase + fbTransformY(fb) + _fbShift;
    // 下界不再要求「整张卡片都在可视区」：贴底那张卡本来就只有一部分露出来，
    // 要求整张可见等于按下的瞬间把它往上拽 20~40px，手一往下拖就顶死（用户实测拖不动）。
    // 现在只保证至少 56px 还看得见，卡片可以跟着手继续往下走。
    var keepVisible = Math.min(h, 56);
    // 上界：允许幽灵的逻辑位置再往上半个卡片高（视觉由下面的 clip-path 裁掉）。
    // 原因：幽灵被钳在 _dragListTop 时，它的中心正好等于本区第一张卡片中心
    // （第一张卡片顶部通常就在列表顶边），SortableJS 判「没越过中线」⇒ 怎么拖都
    // 插不到第一位，用户看到的就是「往上拖只闪红边、第一条不让位」。
    // 卡片越高越容易撞上这条线，低卡没事 ⇒ 表现为「偶发」。
    var topLimit = _dragListTop - Math.round(h * 0.6);  // 往上不许盖输入框
    var bottomLimit = _dragListBottom - keepVisible;    // 往下至少留 56px 可见
    // 按下时已经超出这条线的卡片（贴边/半张露出来）不许被「纠正」回去：
    // 否则先跳一下、再往下拖时永远顶在这条线上 —— 用户看到的就是「拖不动」。
    if (_dragStartFbTop != null) {
      if (_dragStartFbTop < topLimit) topLimit = _dragStartFbTop;
      if (_dragStartFbTop > bottomLimit) bottomLimit = _dragStartFbTop;
    }
    var wantTop = logicalTop;
    if (wantTop < topLimit) wantTop = topLimit;
    else if (wantTop > bottomLimit) wantTop = bottomLimit;
    var adj = wantTop - logicalTop;
    if (adj !== _fbTopAdj) {
      fb.style.top = (_fbBase + adj) + "px";
      _fbTopAdj = adj;
    }
    // 水平锁死：幽灵只许上下走（横向由 lockFbX 把 transform 的 x 清零 + left 固定）
    var wantLeft = _fbFixedLeft;

    var rb = {
      top: wantTop, bottom: wantTop + h,
      left: wantLeft, right: wantLeft + (rb0.width || 0), width: rb0.width, height: h
    };

    // 上面就是视觉钳制：卡片被推出列表上/下边界时停在边界上。
    // 只改 style.top（SortableJS 只写 transform），不跟它抢 transform 所有权。
    // 以前用 SetCursorPos 把光标拉回来 → 光标每 8ms 在边界和手之间来回跳，
    // 卡片跟着跳；现在光标完全交给用户，卡片自己停在边上。
    //
    // 硬裁剪兜底：卡片比列表还高时，任何情况下都不画到列表外
    //    （上 = 输入框下沿，下 = 列表底）。只写 clip-path，不碰 transform。
    try {
      var cutTop = Math.max(0, Math.round(_dragListTop - rb.top));
      var cutBottom = Math.max(0, Math.round(rb.bottom - _dragListBottom));
      var cp = (cutTop > 0 || cutBottom > 0)
        ? "inset(" + cutTop + "px 0px " + cutBottom + "px 0px)" : "";
      if ((fb.style.clipPath || "") !== cp) fb.style.clipPath = cp;
    } catch (e0) {}

    // ③ 只锁水平（tx=0），垂直不碰
    var tf = fb.style.transform || "";
    var mt = /matrix\(([^)]+)\)/.exec(tf);
    if (mt) {
      var v = mt[1].split(",").map(parseFloat);
      if (v.length >= 6 && v[4] !== 0) {
        fb.style.transform = "matrix(" + v[0] + "," + v[1] + "," + v[2] + "," +
                             v[3] + ",0," + v[5] + ")";
      }
    } else {
      var mt2 = /translate(?:3d)?\(([^)]+)\)/.exec(tf);
      if (mt2) {
        var p2 = mt2[1].split(",").map(parseFloat);
        var ty2 = p2.length >= 2 ? p2[1] : 0;
        fb.style.transform = "translate3d(0px," + ty2 + "px,0)";
      }
    }
    return rb;
  }

  /* ---------- 筛选态排序：把可见项的新顺序填回它们在完整列表里的原槽位 ----------
     筛选时 DOM 只有匹配项，直接按 DOM 重建整表会丢没匹配到的笔记。
     这里只重排「匹配到的那些」占用的位置，其余项原地不动。
     数据对不上（长度/状态不符）返回 null，调用方放弃这次写入。 */
  function mergeVisibleOrder(ids, map) {
    var seen = {};
    ids.forEach(function (id) { seen[id] = 1; });
    var newBy = { 0: [], 1: [] }, slots = { 0: [], 1: [] };
    ids.forEach(function (id) {
      var nn = map[id];
      if (nn) newBy[nn.status === 1 ? 1 : 0].push(nn);
    });
    notes.forEach(function (n, i) {
      if (seen[n.id]) slots[n.status === 1 ? 1 : 0].push(i);
    });
    if (newBy[0].length !== slots[0].length || newBy[1].length !== slots[1].length) return null;
    var merged = notes.slice();
    [0, 1].forEach(function (st) {
      for (var q = 0; q < slots[st].length; q++) merged[slots[st][q]] = newBy[st][q];
    });
    return merged;
  }

  /* ---------- 松手时：卡片从「拖拽中最后的位置」滑到「预览落点」 ----------
     revert=true 是「拖到另一个区、松手回原位」：路程长、要慢一点、起步要软，
     所以换一条别那么冲的曲线；普通落位仍然是干脆的 swift-out。 */
  function slideToSlot(el, fromTop, revert) {
    if (!el) return;
    if (fromTop == null) {
      var py = pointerY();
      if (py == null) return;
      fromTop = py - (_grabY || 0);
    }
    requestAnimationFrame(function () {
      try {
        var toTop = el.getBoundingClientRect().top;
        var d = toTop - fromTop;
        if (window.__noteDbg) send("dbg|slide d=" + Math.round(d) + " rev=" + (revert ? 1 : 0));
        if (Math.abs(d) < 1) return;
        el.style.zIndex = "5";
        var a = el.animate(
          [
            { transform: "translateY(" + (-d) + "px)" },
            { transform: "none" }
          ],
          revert
            ? { duration: 620, easing: "cubic-bezier(.33,.02,.2,1)" }
            : { duration: 540, easing: "cubic-bezier(.16,1,.3,1)" }
        );
        a.onfinish = function () { try { el.style.zIndex = ""; } catch (e) { } };
        a.oncancel = function () { try { el.style.zIndex = ""; } catch (e) { } };
      } catch (e) { }
    });
  }

  /* 指针是不是已经落在「另一个区」里：是 = 这次拖放不算数，卡片回原位。
     不能再用「离开本区矩形」判断：未完成区在最上面，往上拖必然越过它的上沿，
     以前那样会被误判成跨区 → 一松手就弹回原位（用户看到的「往上拖拖不动」）。
     现在只看指针是否真的压在另一个区上；上方空白/输入框/下方空白都不算。 */
  function pointerOutOfZone(myZone) {
    try {
      if (!myZone || !listEl) return false;
      var py = pointerY();
      if (py == null) return false;
      var zs = listEl.querySelectorAll(".zone");
      var inMine = false, topMost = Infinity, bottomMost = -Infinity;
      for (var i = 0; i < zs.length; i++) {
        var z = zs[i];
        var r = z.getBoundingClientRect();
        if (r.top < topMost) topMost = r.top;
        if (r.bottom > bottomMost) bottomMost = r.bottom;
        if (py >= r.top && py <= r.bottom) {
          if (z === myZone) inMine = true;
          else return true;                    // 压在别的区上 → 这次拖放不算数
        }
      }
      // 指针落在所有 zone 之外时：
      // · 下方（列表底部那 64px 留白）→ 这次拖放不成立，落到本区末尾（用户 2026-11-09 定案）
      // · 上方 → **不算无效**：往上拖本来就是要插到第一位；而且高卡片拖到顶时，
      //   指针会被允许跑到列表上方（C# 的 topSlack 放宽了光标上界），
      //   这里若仍判无效，用户看到的就是「只闪红边、第一条不让位」。
      if (!inMine && py > bottomMost) return true;
      return false;
    } catch (e) { return false; }
  }

  /* 把被拖的卡片按「按下时的顺序」放回原容器。
     只 insertBefore(item, _dragHomeNext) 会落到错的序号上：那个「下一个兄弟」
     在拖动过程中也被 SortableJS 挪过位置，索引早就不是原位了
     （用户实测：从顶部拖到底部松手，卡片停在第 5 位左右，不是原位）。
     这里按按下时抓的 id 快照重排；不在快照里的节点（拖动中途新建/筛选换过 DOM 的）
     追加到末尾，保证一个都不丢。 */
  function restoreHomeOrder(item) {
    if (!item || !_dragHomeParent) return;
    if (!_dragHomeOrder || !_dragHomeOrder.length) {
      try { _dragHomeParent.insertBefore(item, _dragHomeNext); } catch (e) { }
      return;
    }
    var byId = {};
    _dragHomeParent.querySelectorAll(".note").forEach(function (c) {
      byId[c.getAttribute("data-id")] = c;
    });
    var selfId = item.getAttribute("data-id");
    if (selfId && !byId[selfId]) byId[selfId] = item;
    _dragHomeOrder.forEach(function (id) {
      var el = byId[id];
      if (el) { try { _dragHomeParent.appendChild(el); } catch (e) { } }
    });
    _dragHomeParent.querySelectorAll(".note").forEach(function (c) {
      if (_dragHomeOrder.indexOf(c.getAttribute("data-id")) < 0) {
        try { _dragHomeParent.appendChild(c); } catch (e) { }
      }
    });
  }

  /* ---------- 拖动插入指示线 ----------
     超长卡片（带一堆图片那种）拖起来会把指针整个盖住，用户根本看不见自己拖到哪，
     只能靠猜 —— 表现为「好几个位置都换不了位」。这里画一条线，落点算法和
     SortableJS 的指针判定完全一致，用户看到线在哪儿就知道松手会插到哪儿。 */
  var _dropLineEl = null;
  var _dropLineOn = false;

  function ensureDropLine() {
    if (_dropLineEl && _dropLineEl.parentNode) return _dropLineEl;
    var d = document.createElement("div");
    d.id = "__dropline";
    document.body.appendChild(d);
    _dropLineEl = d;
    return d;
  }

  function hideDropLine() {
    if (_dropLineEl) _dropLineEl.style.opacity = "0";
    _dropLineOn = false;
  }

  function updateDropLine() {
    if (!_sortDragging || !_dragItem || !_dragZoneEl) { hideDropLine(); return; }
    var py = pointerY();
    if (py == null) { hideDropLine(); return; }
    var zone = _dragZoneEl;
    var cards = zone.querySelectorAll(".note");
    var target = null, before = true;
    for (var i = 0; i < cards.length; i++) {
      var c = cards[i];
      if (c === _dragItem) continue;
      var r = c.getBoundingClientRect();
      if (py < r.top + r.height / 2) { target = c; before = true; break; }
      target = c; before = false;
    }
    if (!target) { hideDropLine(); return; }
    var tr = target.getBoundingClientRect();
    var wr = zone.getBoundingClientRect();
    var line = ensureDropLine();
    line.style.left = Math.round(wr.left + 8) + "px";
    line.style.width = Math.max(60, Math.round(wr.width - 16)) + "px";
    line.style.top = Math.round((before ? tr.top : tr.bottom) - 1) + "px";
    line.style.opacity = "1";
    _dropLineOn = true;
  }

  /* ---------- 跨区视觉拦截：离开自己那一区就变淡 + 禁止标记 ---------- */
  function updateOutZoneHint() {
    try {
      var fb = document.querySelector(".sortable-fallback");
      if (!fb) return;
      if (!_dragZoneEl) { fb.classList.remove("out-zone"); return; }
      // 和松手判定用同一套逻辑，视觉与结果才不会打架
      fb.classList.toggle("out-zone", pointerOutOfZone(_dragZoneEl));
    } catch (e) {}
  }

  /* 已经顶到边上、而且这个方向也没法再滚了 → 红边闪动，卡片本身不动 */
  function updateLimitHint(rb) {
    if (!wrapEl) return;
    // 提示条贴到「列表可视区」的上/下沿，而不是窗口边缘 —— 贴窗口会盖住输入框和标题栏
    try {
      var gTop = document.querySelector(".edge-glow.top");
      var gBot = document.querySelector(".edge-glow.bottom");
      if (gTop) gTop.style.top = Math.round(_dragListTop) + "px";
      if (gBot) gBot.style.bottom = Math.max(0, Math.round(window.innerHeight - _dragListBottom)) + "px";
    } catch (e5) { }
    var lim = "";
    try {
      var maxTop = wrapEl.scrollHeight - wrapEl.clientHeight;
      // 判定基准：指针到边界，或**卡片边缘**到边界，任一成立就提示。
      // · 只看指针（上一版）：抓在卡片下部往上拖时，卡片顶端早就顶到列表上沿了，
      //   指针却还在下面几百像素 → 红边死活不亮（用户 2026-11-09 反馈）。
      // · 只看卡片（更早的版本）：抓在卡片下部时卡片顶端越界了、红边闪了，
      //   但指针还在第 2 张卡上，Sortable 不换位 → 红边「骗人」。
      // 现在两个都算，各司其职：红边 = 已经到头了；#__dropline = 真正会插在哪里。
      var py2 = pointerY();
      if (py2 != null) {
        var cardTopY = py2 - (_grabY || 0);
        var cardBotY = cardTopY + (_cardH || 0);
        if ((py2 <= _dragListTop + 24 || cardTopY <= _dragListTop + 1) && wrapEl.scrollTop <= 0) lim = "limit-top";
        else if ((py2 >= _dragListBottom - 24 || cardBotY >= _dragListBottom - 1) && wrapEl.scrollTop >= maxTop - 1) lim = "limit-bottom";
      } else {
        var rb2 = rb || { top: 0, bottom: 0 };
        if (rb2.top <= _dragListTop + 1 && wrapEl.scrollTop <= 0) lim = "limit-top";
        else if (rb2.bottom >= _dragListBottom - 1 && wrapEl.scrollTop >= maxTop - 1) lim = "limit-bottom";
      }
    } catch (e) { }
    if (lim === "limit-top" && !_limitDbgDone) {
      _limitDbgDone = true;
      try {
        var fbd = document.querySelector(".sortable-fallback");
        var fdr = fbd ? fbd.getBoundingClientRect() : null;
        var z0 = _dragZoneEl;
        var first0 = z0 ? z0.querySelector(".note") : null;
        var r10 = first0 ? first0.getBoundingClientRect() : null;
        var idx0 = -1;
        try {
          if (z0 && _dragItem) {
            idx0 = Array.prototype.indexOf.call(z0.querySelectorAll(".note"), _dragItem);
          }
        } catch (e3) { }
        send("dbg|limittop py=" + Math.round(py) + " grab=" + Math.round(_grabY) +
          " listTop=" + Math.round(_dragListTop) +
          " startFbTop=" + (_dragStartFbTop == null ? "null" : Math.round(_dragStartFbTop)) +
          " fbTop=" + (fdr ? Math.round(fdr.top) : "null") +
          " fbC=" + (fdr ? Math.round(fdr.top + fdr.height / 2) : "null") +
          " h=" + (fdr ? Math.round(fdr.height) : "null") +
          " firstTop=" + (r10 ? Math.round(r10.top) : "null") +
          " firstC=" + (r10 ? Math.round(r10.top + r10.height / 2) : "null") +
          " idx=" + idx0 + " scrollTop=" + Math.round(wrapEl.scrollTop));
      } catch (e4) { }
    }
    if (lim === _limitCls) return;
    _limitCls = lim;
    wrapEl.classList.remove("limit-top", "limit-bottom");
    document.body.classList.remove("limit-top", "limit-bottom");
    if (lim) {
      wrapEl.classList.add(lim);
      document.body.classList.add(lim);
    }
  }

  function clearLimitHint() {
    _limitCls = "";
    if (wrapEl) wrapEl.classList.remove("limit-top", "limit-bottom");
    document.body.classList.remove("limit-top", "limit-bottom");
  }

  function startClampLoop() {
    _fbTopAdj = 0; _fbCur = null; _fbShift = 0;
    var dead = 0;                      // 连续多少帧没有幽灵元素
    var tick = function () {
      if (!_sortDragging) return;
      try {
        var fb = document.querySelector(".sortable-fallback");
        if (fb) {
          dead = 0;
          var rb0 = fb.getBoundingClientRect();  // 钳制前：指针把卡片推到了哪
          var rb = forceFallbackPos(fb, rb0);    // 返回钳制后的实际位置
          _lastFbTop = rb ? rb.top : rb0.top;    // 松手回弹的起点必须是「看得见的位置」
          updateLimitHint(rb);
        } else if (++dead > 12) {
          // 拖动中幽灵必须一直在。连续 12 帧没了 = 原生层把 mouseup 吞了
          // （热区抢走、窗口失焦、ClipCursor 边界），Sortable 永远收不了尾：
          // 列表自己一直滚、底边红框一直闪。这里强制复位。
          hardEndSortDrag();
          return;
        }
      } catch (e) {}
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }

  /* 结束一次「原生层把 pointerup 吞掉了」的拖动。
     正确做法是补一个真正的 pointerup —— 本项目 Sortable 的 supportPointer=true，
     它监听的是 pointerup 而不是 mouseup，只补 mouseup 它根本不收尾。
     补发后 Sortable 自己走 onEnd，DOM/数据/动画都按正常路径结束；
     再留一道 220ms 兜底，万一还没收尾就强制复位。 */
  function forceFinishSortDrag() {
    if (!_sortDragging) return;
    var x = (_lastClientX == null) ? 10 : _lastClientX;
    var y = (_lastClientY == null) ? (_dragListTop + 10) : _lastClientY;
    try {
      var pe = null;
      try {
        pe = new PointerEvent("pointerup", {
          bubbles: true, cancelable: true, view: window,
          pointerId: 1, pointerType: "mouse", isPrimary: true,
          button: 0, buttons: 0, clientX: x, clientY: y
        });
      } catch (e0) { pe = null; }
      if (pe) document.dispatchEvent(pe);
      document.dispatchEvent(new MouseEvent("mouseup", {
        bubbles: true, cancelable: true, view: window,
        button: 0, buttons: 0, clientX: x, clientY: y
      }));
    } catch (e1) { }
    setTimeout(function () {
      if (_sortDragging) hardEndSortDrag();
    }, 220);
  }

  /* 最后一道兜底：停滚、灭红框、幽灵摘掉、卡片放回原位，
     并且把 Sortable 的内部状态（_dragEl）清干净 ——
     不清的话下一次 mousedown 会被 _prepareDragStart 直接忽略，
     用户看到的就是「一个都拖不动了」。 */
  function hardEndSortDrag() {
    var it = _dragItem;
    var inst = _dragInst;
    var fromTop = _lastFbTop;
    _sortDragging = false; document.body.classList.remove("sorting");
    _dragItem = null;
    _dragInst = null;
    _dragZoneEl = null;
    _lastFbTop = null;
    try { stopEdgeScroll(); } catch (e0) { }
    try { clearLimitHint(); } catch (e1) { }
    try { hideDropLine(); } catch (e1b) { }
    try {
      var fbs = document.querySelectorAll(".sortable-fallback");
      for (var i = 0; i < fbs.length; i++) {
        if (fbs[i].parentNode) fbs[i].parentNode.removeChild(fbs[i]);
      }
    } catch (e5) { }
    try {
      if (it) {
        if (_dragHomeParent && _dragHomeOrder && _dragHomeOrder.length)
          restoreHomeOrder(it);      // 同 onEnd 的 outZone 分支：按按下时的 id 快照回位
        it.classList.remove("sortable-fallback", "ghost", "chosen", "dragging");
        it.style.transform = "";
        it.style.top = "";
        it.style.left = "";
        it.style.position = "";
        it.style.width = "";
        it.style.height = "";
        it.style.margin = "";
        it.style.zIndex = "";
        it.style.pointerEvents = "";
      }
    } catch (e3) { }
    if (inst) {
      try { if (inst._loopId) clearInterval(inst._loopId); } catch (e6) { }
      try { if (inst._dragStartTimer) clearTimeout(inst._dragStartTimer); } catch (e7) { }
      try { inst._offMoveEvents(); } catch (e8) { }
      try { inst._offUpEvents(); } catch (e9) { }
      try { inst._nulling(); } catch (e10) { }
    }
    _dragHomeParent = null;
    _dragHomeNext = null;
    _dragHomeOrder = null;
    try { send("dragend"); } catch (e2) { }
    try { reportAllAtts(true); } catch (e4) { }
    slideToSlot(it, fromTop, true);
    send("dbg|hardend 强制复位 inst=" + (inst ? 1 : 0));
  }

  /* ---------- 配色 ---------- */
  var PALETTES = [
    { i:1,  n:"纯中性石墨", bg:"#101010", card:"#3A3A3A", line:"#525252", text:"#EDEDED", muted:"#A8A8A8", accent:"#6EA8FF" },
    { i:2,  n:"提亮中性灰", bg:"#171717", card:"#3E3E3E", line:"#585858", text:"#F0F0F0", muted:"#B0B0B0", accent:"#6EA8FF" },
    { i:3,  n:"极黑高反差", bg:"#050505", card:"#313131", line:"#4E4E4E", text:"#F5F5F5", muted:"#B5B5B5", accent:"#6EA8FF" },
    { i:4,  n:"石板灰",     bg:"#14171A", card:"#393E43", line:"#50575E", text:"#EEF1F3", muted:"#A9B2B9", accent:"#5B9DFF" },
    { i:5,  n:"暖炭棕",     bg:"#14100C", card:"#403830", line:"#5A4C40", text:"#F2EDE6", muted:"#B5A695", accent:"#E0A062" },
    { i:6,  n:"深咖",       bg:"#17110D", card:"#41372F", line:"#5C4E42", text:"#F5EFE8", muted:"#BCAC9A", accent:"#D9975A" },
    { i:7,  n:"暖沙米",     bg:"#1A1611", card:"#403A31", line:"#5C5347", text:"#F6F1E9", muted:"#C0B5A4", accent:"#E3B071" },
    { i:8,  n:"中亮暖灰",   bg:"#1C1A17", card:"#423C35", line:"#5E564C", text:"#F5F2ED", muted:"#BFB7AB", accent:"#E0A96A" },
    { i:9,  n:"深墨绿",     bg:"#060606", card:"#2E3C37", line:"#44574F", text:"#EAF3EF", muted:"#A2B8B0", accent:"#4FBF95" },
    { i:10, n:"深橄榄",     bg:"#12140E", card:"#3B4034", line:"#545A47", text:"#F0F3E8", muted:"#B4BAA4", accent:"#9DBF5A" },
    { i:11, n:"深青灰",     bg:"#0D1214", card:"#323D41", line:"#4A575D", text:"#EAF1F3", muted:"#A3B4BA", accent:"#3FB2C4" },
    { i:12, n:"深紫罗兰",   bg:"#100D14", card:"#40394A", line:"#584E64", text:"#F1EDF6", muted:"#B3A9BE", accent:"#A87BE0" },
    { i:13, n:"深酒红",     bg:"#140F10", card:"#443437", line:"#5D464B", text:"#F5EDEE", muted:"#BFA9AC", accent:"#E07A80" },
    { i:14, n:"玫瑰灰",     bg:"#151013", card:"#44393F", line:"#5D4B54", text:"#F6EEF2", muted:"#C0ABB4", accent:"#E28AA8" },
    { i:15, n:"深蓝灰",     bg:"#0E1014", card:"#343D4E", line:"#4E5A6C", text:"#EEF1F7", muted:"#AAB4C3", accent:"#5B9DFF" },
    { i:16, n:"深靛蓝",     bg:"#0E1018", card:"#363C50", line:"#4C536B", text:"#EDF0F8", muted:"#A7B0C6", accent:"#6E86E8" }
  ];
  // 优先级/标记色板（v2739）：10 色，取自 Notion 深色模式那套色相的适配版。
  // 顺序按色相走（红→灰），用户记规律：越靠前越"热"。末位灰=弱标记。
  /* 老笔记里存的是旧色板的 hex（比如 #E5534B / #C9A227）。换板后它们不在新 10 色里，
     渲染时按 RGB 距离吸附到最近的新色 —— 只影响显示，不改 notes.json。 */
  function normColor(c) {
    if (!c) return c;
    if (NOTE_COLORS.indexOf(c.toUpperCase()) >= 0) return c;
    var m = /^#?([0-9a-f]{6})$/i.exec(c);
    if (!m) return c;
    var v = parseInt(m[1], 16), r = (v >> 16) & 255, g = (v >> 8) & 255, b = v & 255;
    var best = NOTE_COLORS[0], bd = 1e9;
    for (var i = 0; i < NOTE_COLORS.length; i++) {
      var h = parseInt(NOTE_COLORS[i].slice(1), 16);
      var dr = ((h >> 16) & 255) - r, dg = ((h >> 8) & 255) - g, db = (h & 255) - b;
      var d = dr * dr + dg * dg + db * db;
      if (d < bd) { bd = d; best = NOTE_COLORS[i]; }
    }
    return best;
  }

  var NOTE_COLORS = [
    "#E2645C",  // 红
    "#E08A4B",  // 橙
    "#D9B94E",  // 黄
    "#7FAF7A",  // 绿
    "#5FA8A0",  // 青
    "#5B9DFF",  // 蓝
    "#8E7BE0",  // 紫
    "#D07BA8",  // 粉
    "#A9866B",  // 棕
    "#8FA3AE"   // 灰
  ];

  /* ---------- 拖到边界的提示色（前端本地偏好，存 localStorage） ----------
     默认暖红 #FF6F61。CSS 侧用 --edge-r/g/b 三个分量拼 rgba，
     这样渐变、边框、外发光都跟着一个色走，不用改多处。 */
  var EDGE_COLORS = ["#FF6F61", "#FFA94D", "#FFD43B", "#51CF66", "#4DABF7", "#B197FC", "#FF8FB1"];

  function applyEdgeColor(hex) {
    if (!/^#[0-9a-fA-F]{6}$/.test(hex)) return;
    var v = parseInt(hex.slice(1), 16);
    var st = document.documentElement.style;
    st.setProperty("--edge-r", String((v >> 16) & 255));
    st.setProperty("--edge-g", String((v >> 8) & 255));
    st.setProperty("--edge-b", String(v & 255));
    try { localStorage.setItem("edgeHintColor", hex); } catch (e) { }
  }

  function edgeColorNow() {
    var c = null;
    try { c = localStorage.getItem("edgeHintColor"); } catch (e) { }
    return (c && /^#[0-9a-fA-F]{6}$/.test(c)) ? c.toUpperCase() : EDGE_COLORS[0];
  }

  function renderEdgeColors() {
    var host = document.getElementById("edgeColors");
    if (!host) return;
    var cur = edgeColorNow();
    host.innerHTML = EDGE_COLORS.map(function (c) {
      return '<i data-c="' + c + '" style="background:' + c + '"' +
        (c.toUpperCase() === cur ? ' class="on"' : '') + '></i>';
    }).join("");
    host.querySelectorAll("i").forEach(function (el) {
      el.onclick = function () {
        applyEdgeColor(el.getAttribute("data-c"));
        renderEdgeColors();
      };
    });
  }

  // 启动时恢复上次选的颜色（没有就用默认）
  (function () {
    try { applyEdgeColor(edgeColorNow()); } catch (e) { }
  })();

  /* ---------- 提示闪烁节奏（慢 / 标准 / 快 / 不闪） ---------- */
  var EDGE_PULSES = [
    { k: "slow", n: "慢", v: "edgePulse 2.4s ease-in-out infinite" },
    { k: "std", n: "标准", v: "edgePulse 1.6s ease-in-out infinite" },
    { k: "fast", n: "快", v: "edgePulse 0.75s ease-in-out infinite" },
    { k: "vfast", n: "极快", v: "edgePulse 0.35s ease-in-out infinite" },
    { k: "off", n: "不闪", v: "none" }
  ];

  function edgePulseNow() {
    var k = null;
    try { k = localStorage.getItem("edgePulse"); } catch (e) { }
    for (var i = 0; i < EDGE_PULSES.length; i++) if (EDGE_PULSES[i].k === k) return EDGE_PULSES[i];
    return EDGE_PULSES[1];      // 默认「标准」
  }

  function applyEdgePulse(k) {
    var item = EDGE_PULSES[1];
    for (var i = 0; i < EDGE_PULSES.length; i++) if (EDGE_PULSES[i].k === k) item = EDGE_PULSES[i];
    document.documentElement.style.setProperty("--edge-pulse-anim", item.v);
    try { localStorage.setItem("edgePulse", item.k); } catch (e) { }
  }

  function renderEdgePulses() {
    var host = document.getElementById("edgePulses");
    if (!host) return;
    var cur = edgePulseNow().k;
    host.innerHTML = EDGE_PULSES.map(function (p) {
      return '<b data-k="' + p.k + '"' + (p.k === cur ? ' class="on"' : '') + '>' + p.n + '</b>';
    }).join("");
    host.querySelectorAll("b").forEach(function (el) {
      el.onclick = function () {
        applyEdgePulse(el.getAttribute("data-k"));
        renderEdgePulses();
      };
    });
  }

  (function () { try { applyEdgePulse(edgePulseNow().k); } catch (e) { } })();

  function applyPalette(i) {
    var p = null;
    for (var k = 0; k < PALETTES.length; k++) if (PALETTES[k].i === i) p = PALETTES[k];
    if (!p) p = PALETTES[8];
    var st = document.documentElement.style;
    st.setProperty("--bg", p.bg);
    st.setProperty("--card", p.card);
    st.setProperty("--line", p.line);
    st.setProperty("--text", p.text);
    st.setProperty("--muted", p.muted);
    st.setProperty("--accent", p.accent);
    send("__setbg|" + p.bg);
    send("__setaccent|" + p.accent);
  }
  function renderPalettes() {
    var host = document.getElementById("palettes");
    if (!host) return;
    host.innerHTML = PALETTES.map(function (p) {
      return '<div class="pal' + (p.i === settings.palette ? " on" : "") + '" data-p="' + p.i + '" ' +
             'style="background:linear-gradient(135deg,' + p.bg + ' 0%,' + p.card + ' 60%,' + p.accent + ' 100%)" ' +
             'title="' + p.n + '">' + p.i + "</div>";
    }).join("");
    host.querySelectorAll(".pal").forEach(function (el) {
      el.onclick = function () {
        var i = parseInt(el.getAttribute("data-p"), 10);
        settings.palette = i;
        applyPalette(i);
        send("palette|" + i);
        renderPalettes();
      };
    });
  }

  /* ---------- 附件 ---------- */
  var SVG_VIDEO = '<svg viewBox="0 0 40 40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"><rect x="4" y="9" width="32" height="22" rx="3.5"/><path d="m16.5 15.5 8 4.5-8 4.5z" fill="currentColor" stroke="none"/></svg>';
  var SVG_IMAGE = '<svg viewBox="0 0 40 40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round" stroke-linecap="round"><rect x="4" y="8" width="32" height="24" rx="3.5"/><circle cx="13" cy="16" r="2.6"/><path d="m7 29 8-8.5 5.5 5 4.5-4.5 6.5 8"/></svg>';
  var SVG_DOC = '<svg viewBox="0 0 40 40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round" stroke-linecap="round"><path d="M11 3.5h13.5L34 13v22.5a2.5 2.5 0 0 1-2.5 2.5h-20A2.5 2.5 0 0 1 9 35.5v-29A2.5 2.5 0 0 1 11 3.5z"/><path d="M24.5 3.5V13H34"/></svg>';

  var SVG_X = '<svg viewBox="0 0 10 10" aria-hidden="true"><path d="M1.2 1.2 L8.8 8.8 M8.8 1.2 L1.2 8.8" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" fill="none"/></svg>';
  var SVG_PLAY = '<svg viewBox="0 0 10 10" aria-hidden="true">' +
    '<path d="M3 1.9 L8.5 5 L3 8.1 Z" fill="currentColor" stroke="currentColor" ' +
    'stroke-width="1.1" stroke-linejoin="round"/></svg>';

  /* 文件名：主体可以省略，后缀单独一段、永远可见（缩略图和文件图标一致） */
  function attNameHtml(name) {
    var s = String(name == null ? "" : name);
    var dot = s.lastIndexOf(".");
    var base = s, ext = "";
    if (dot > 0 && s.length - dot <= 9) { base = s.slice(0, dot); ext = s.slice(dot); }
    return '<div class="nm"><span class="bn">' + esc(base) + '</span>' +
           (ext ? '<span class="ex">' + esc(ext) + '</span>' : '') + '</div>';
  }

  function attHtml(a) {
    var inner;
    if (a.thumb) {
      inner = '<img class="thumb" draggable="false" src="' + esc(a.thumb) + '" alt="">';
      if (a.kind === "video") inner += '<span class="play">' + SVG_PLAY + '</span>';
      return '<div class="att has-thumb" data-att="' + esc(a.id) + '" data-kind="' + esc(a.kind || "file") + '" title="' + esc(a.name) + '">' +
             inner + attNameHtml(a.name) +
             '<div class="x" data-act="delatt">' + SVG_X + '</div></div>';
    }
    if (a.fileIcon) inner = '<img class="sysico" draggable="false" src="' + esc(a.fileIcon) + '" alt="">';
    // 还没抽到首帧时不要摆一个 40px 的大播放器图标，改成深色小块 + 小播放标
    else if (a.kind === "video") inner = '<span class="vidph">' + SVG_PLAY + '</span>';
    else if (a.kind === "image") inner = '<span class="ico">' + SVG_IMAGE + '</span>';
    else inner = '<span class="ico">' + SVG_DOC + '</span>';

    return '<div class="att" data-att="' + esc(a.id) + '" data-kind="' + esc(a.kind || "file") + '" title="' + esc(a.name) + '">' +
           '<div class="thumbbox">' + inner + '</div>' +
           attNameHtml(a.name) +
           '<div class="x" data-act="delatt">' + SVG_X + '</div></div>';
  }

  function reportOneAtt(el) {
    if (_sortDragging) return;
    if (!el.getAttribute("data-att")) return;      // 外部拖入的占位槽不是真附件
    var r = el.getBoundingClientRect();
    if (r.width < 8 || r.height < 8) return;
    send("attrects|" + el.getAttribute("data-att") + "," +
      Math.round(r.left) + "," + Math.round(r.top) + "," +
      Math.round(r.width) + "," + Math.round(r.height));
  }

  /* 滚动/重渲染后把所有可见附件热区重算一遍。
     以前滚动时发的是空串 —— C# 会把所有透明窗口藏起来，指针就直接落到卡片上，
     表现就是「滚动之后角标点不动、附件也拖不动，非得先把鼠标在每个附件上晃一遍」。 */
  var _attRaf = 0;
  var _attHideTimer = 0;
  var _attHidden = false;
  var _attRects = "";
  var _attForce = false;
  var _scrollBusy = false;
  var _scrollRaf = 0;
  var _lastScrollAt = 0;
  var _topBtnOn = false;
  var _doneBtnOn = false;
  var _prevNoteIds = {};
  var _dropNewPending = false;
  var _dropFlashId = null;
  var _dropPreviewEl = null;
  var _dropTargetEl = null;
  var _lastDragOverAt = 0;
  var _dragFileCount = 1;
var _dragKind = "file";   // "file" | "text"：这一轮拖进来的是文件还是文字/链接
  var _topScrollRaf = 0;
  var _wheelTarget = 0;      // 热区转发的滚轮：累计目标位置
  var _wheelRaf = 0;
  var _wheelAnim = false;
  var _wheelAt = 0;

  /* 热区转发滚轮的补间：每帧吃掉剩余距离的 28%，连续滚轮会自然叠加。 */
  function wheelStep() {
    _wheelRaf = 0;
    if (!wrapEl) { _wheelAnim = false; return; }
    // 超过 280ms 没有新的滚轮就收手：绝不能让这条补间在用户已经改用手滚/其它方式时
    // 还把 scrollTop 往旧目标上拽（那样表现就是「滚一小段就卡住/被拉回去」）。
    if (Date.now() - _wheelAt > 280) { _wheelAnim = false; return; }
    var cur = wrapEl.scrollTop;
    var d = _wheelTarget - cur;
    if (Math.abs(d) < 0.5) {
      wrapEl.scrollTop = _wheelTarget;
      _wheelAnim = false;
      return;
    }
    wrapEl.scrollTop = cur + d * 0.28;
    _wheelRaf = requestAnimationFrame(wheelStep);
  }

  /* 一段滚动从开始到停手的整个过程里，热区层必须「冻结」：
     1) 一开头让 C# 把全部附件热区收起来（只发一次 atthide）；
     2) 滚动中不扫描 DOM、不发 attrects、不恢复热区 —— 每次恢复都是十几个
        分层窗 Show + 重新置顶，慢速滚动（一格一停）时几乎每格都要来一轮，
        这是滚轮卡顿的主因；
     3) 停手 400ms 后按新位置一次性恢复；鼠标一动可以提前恢复，hover 不迟钝。 */
  function beginScrollBurst() {
    if (_sortDragging || _scrollBusy) return;
    _scrollBusy = true;
    _attHidden = true;
    // 必须让 C# 把热区全收起来，否则滚动会「卡住」：
    // 热区是盖在缩略图上的原生窗，滚轮落在它身上就进不了 Chromium —— 只能靠
    // C# 转发一格过来。以前缩略图那一大片区域几乎每次滚轮都被吃掉，
    // 于是滚动变成「一格一格转发」，稍微有一点抖动就表现为滚不动 / 滚一小段就停。
    // 收起之后整块区域都还给页面，走的是 Chromium 原生平滑滚动，手感才对。
    // 代价只有一次「把窗口挪出屏幕」（一个 ~1.8ms），不是以前那种 Hide/Show 的几十毫秒。
    send("atthide");
  }
  function endScrollBurst() {
    if (!_scrollBusy) return;
    _scrollBusy = false;
    _attHidden = false;
    // 热区被收起过，即使矩形没变也必须重新上报一次，否则不会重新出现。
    reportAllAtts(true);
  }
  function scheduleAttRestore() {
    if (_attHideTimer) clearTimeout(_attHideTimer);
    _attHideTimer = setTimeout(function () {
      _attHideTimer = 0;
      endScrollBurst();
    }, 260);
  }

  function reportAllAtts(force) {
    if (_sortDragging) return;
    if (force) _attForce = true;
    // 滚动中扫出来的矩形下一帧就过期了，还会把刚收起来的热区全 Show 回来。
    // 先记下「欠一次强制上报」，停手后由 endScrollBurst() 补。
    if (_scrollBusy) return;
    _attHidden = false;
    if (_attRaf) return;
    _attRaf = requestAnimationFrame(function () {
      _attRaf = 0;
      if (_attHidden) return;
      var mustSend = _attForce;
      _attForce = false;
      var parts = [];
      var btnRects = [];
      var wrapR = wrapEl ? wrapEl.getBoundingClientRect() : null;
      if (topBtn && topBtn.classList.contains("on")) btnRects.push(topBtn.getBoundingClientRect());
      if (doneBtn && doneBtn.classList.contains("on")) btnRects.push(doneBtn.getBoundingClientRect());
      listEl.querySelectorAll(".att").forEach(function (el) {
        // 外部拖入的占位槽（.att-slot）没有 data-att：它不是真附件，
        // 报上去会让 C# 建一个 id=null 的热区，白吃掉这块区域的点击。
        if (!el.getAttribute("data-att")) return;
        var r = el.getBoundingClientRect();
        if (r.width < 8 || r.height < 8) return;
        if (r.bottom < 0 || r.top > window.innerHeight) return;
        // 附件跑到「列表可视区」之外时不要建热区：热区是原生 Topmost 独立窗口，
        // 会比 HTML 层级还高。超长卡片被拖到标题栏区域后，它的缩略图会把
        // 「批量编辑」「设置」这些按钮整个盖住，点下去等于点在热区上（用户 2026-11-09 实测）。
        if (wrapR && (r.top < wrapR.top - 2 || r.bottom > wrapR.bottom + 2)) return;
        // 右下角圆钮是 HTML 层；附件热区是原生 Topmost 窗口，会盖住按钮。
        // 只要矩形相交就不建热区（不能只判中心点：按钮边缘仍会被热区吃掉）。
        for (var bi = 0; bi < btnRects.length; bi++) {
          var br = btnRects[bi];
          var pad = 2;
          if (r.left < br.right + pad && r.right > br.left - pad &&
              r.top < br.bottom + pad && r.bottom > br.top - pad) return;
        }
        parts.push(el.getAttribute("data-att") + "," +
          Math.round(r.left) + "," + Math.round(r.top) + "," +
          Math.round(r.width) + "," + Math.round(r.height));
      });
      var payload = parts.join(";");
      // 滚动结束后位置常常没变；内容相同就不要再穿过 WebView2 消息通道。
      if (!mustSend && payload === _attRects) return;
      _attRects = payload;
      send("attrects|" + payload);
    });
  }

  function updateTopButton() {
    if (!wrapEl || !topBtn) return;
    var on = !_sortDragging && !batchMode && wrapEl.scrollTop > 140;
    if (on === _topBtnOn) return;
    _topBtnOn = on;
    topBtn.classList.toggle("on", on);
    topBtn.tabIndex = on ? 0 : -1;
    topBtn.setAttribute("aria-hidden", on ? "false" : "true");
    // 按钮出现/消失会改变它遮挡的附件区域，必须让 C# 重新报一次热区，
    // 否则按钮刚出现时仍会被上一次的 Topmost 热区盖住。
    reportAllAtts(true);
  }

  function updateDoneButton() {
    if (!wrapEl || !doneBtn) return;
    var zone = listEl ? listEl.querySelector(".zone-done") : null;
    var on = false;
    if (zone && !_sortDragging && !batchMode) {
      var wr = wrapEl.getBoundingClientRect();
      var zr = zone.getBoundingClientRect();
      // 已完成区还没进入视口下沿时，给一个入口；滚过去后自动收起
      on = zr.top > wr.bottom - 56;
    }
    if (on === _doneBtnOn) return;
    _doneBtnOn = on;
    doneBtn.classList.toggle("on", on);
    doneBtn.tabIndex = on ? 0 : -1;
    doneBtn.setAttribute("aria-hidden", on ? "false" : "true");
    reportAllAtts(true);
  }

  function cancelTopScroll() {
    if (_topScrollRaf) {
      cancelAnimationFrame(_topScrollRaf);
      _topScrollRaf = 0;
    }
  }

  function flashButton(btn) {
    if (!btn) return;
    btn.classList.add("done");
    setTimeout(function () { btn.classList.remove("done"); }, 900);
  }

  function animateScrollTo(target, done, durMs) {
    if (!wrapEl) return;
    cancelTopScroll();
    var start = wrapEl.scrollTop;
    var dist = target - start;
    if (Math.abs(dist) < 1) {
      updateTopButton(); updateDoneButton();
      if (done) done();
      return;
    }
    var t0 = performance.now();
    var dur = durMs || Math.min(680, 260 + Math.abs(dist) * 0.12);
    function step(now) {
      var p = Math.min(1, (now - t0) / dur);
      var e = 1 - Math.pow(1 - p, 3);
      wrapEl.scrollTop = start + dist * e;
      if (p < 1) {
        _topScrollRaf = requestAnimationFrame(step);
        return;
      }
      _topScrollRaf = 0;
      wrapEl.scrollTop = target;
      updateTopButton(); updateDoneButton();
      reportAllAtts(true);
      if (done) done();
    }
    _topScrollRaf = requestAnimationFrame(step);
  }

  function scrollToTop() {
    if (!wrapEl) return;
    if (wrapEl.scrollTop <= 0) {
      flashButton(topBtn);
      showHint("已在顶部", 1200);
      return;
    }
    animateScrollTo(0, function () {
      flashButton(topBtn);
      showHint("已到顶部", 1200);
    });
  }

  function scrollToDone() {
    if (!wrapEl || !listEl) return;
    var zone = listEl.querySelector(".zone-done");
    if (!zone) { showHint("还没有已完成的待办", 1600); return; }
    var top = zone.getBoundingClientRect().top - wrapEl.getBoundingClientRect().top + wrapEl.scrollTop - 6;
    animateScrollTo(Math.max(0, top), function () {
      flashButton(doneBtn);
      showHint("已到已完成", 1200);
    });
  }

  function flashNote(id) {
    if (!listEl || !id) return;
    var el = null;
    try {
      el = listEl.querySelector('.note[data-id="' + String(id).replace(/"/g, "") + '"]');
    } catch (e) { return; }
    if (!el) return;
    el.classList.remove("pop");
    void el.offsetWidth;   // 强制重排，保证连点两次也能重播
    el.classList.add("pop");
    setTimeout(function () { el.classList.remove("pop"); }, 1150);
  }

  /* ---------- 渲染 ---------- */
  var _addPending = false;   // 按 Enter 新建：等这次 setNotes 回来做「滑到顶 + 闪一下」
  var _flipPrev = null;
  function visibleNotes() {
    if (!query) return notes;
    var terms = searchTerms(query);
    return notes.filter(function (n) { return noteMatches(n, terms); });
  }

  function render(animate) {
    var arr = visibleNotes();

    if (animate) {
      _flipPrev = {};
      listEl.querySelectorAll(".note").forEach(function (c) {
        var ab = c.querySelector(".atts");
        _flipPrev[c.getAttribute("data-id")] = {
          top: c.getBoundingClientRect().top,
          h: c.offsetHeight,
          n: ab ? ab.querySelectorAll(".att").length : 0,
          ah: ab ? ab.offsetHeight : 0
        };
      });
    } else _flipPrev = null;

    // 未完成 / 已完成拆成两个独立容器：两个 SortableJS 实例 group 互不相通，
    // 拖动时根本过不去（单列表 + onMove 在 forceFallback 下拦不住，实测仍会插入）。
    var cardHtml = function (n) {
      var done = n.status === 1;
      var atts = (n.atts && n.atts.length)
        ? '<div class="atts">' + n.atts.map(attHtml).join("") + '</div>' : "";

      return '<div class="note' + (done?" done":"") + (n.id===selectedId?" selected":"") +
             '" data-id="' + esc(n.id) + '" data-done="' + (done?"1":"0") + '">' +
               (function () {
                 var cc = normColor(n.color);
                 return '<div class="' + (cc ? "bar has" : "bar") + '" data-act="color" ' +
                        (cc ? 'style="background:' + esc(cc) + '"' : '') + '></div>';
               })() +
               '<div class="ring' + (done?" on":"") + '" data-act="toggle"></div>' +
               '<div class="chk' + (batchSel[n.id]?" on":"") + '" data-act="check">' + (batchSel[n.id]?"✓":"") + '</div>' +
               (done ? "" :
                 '<div class="pin' + (n.pinned ? " on" : "") + '" data-act="pin" title="' +
                 (n.pinned ? "取消置顶" : "置顶") + '">' +
                   '<svg viewBox="0 0 16 16"><path d="M8 2l4 4H9.6v4H6.4V6H4z"/>' +
                   '<path d="M3.2 12.2h9.6v1.4H3.2z"/></svg>' +
                 '</div>') +
               '<div class="body">' +
                 '<div class="text' + (query ? "" : " md") + '" data-act="text">' +
                   (query ? hlText(n.text, query) : mdHtml(n.text)) + '</div>' + atts +
                 '<div class="foot">' +
                   '<span class="upd">' + fmtStamp(n.updated) + '</span>' +
                   '<div class="acts"><span class="made">建 ' + fmtStamp(n.created) + '</span>' +
                   '<span class="exp" data-act="expand" style="display:none">预览</span>' +
                   '<span data-act="edit">编辑</span>' +
                   '<span class="del" data-act="delete">删除</span></div>' +
                 '</div>' +
               '</div>' +
             '</div>';
    };

    // 三个独立容器：置顶 / 待办 / 已完成。每个 zone 一个 Sortable 实例、group 名字不同，
    // 所以「非置顶拖不进置顶区」是 group 隔离天然保证的，不需要额外拦截。
    var pinArr  = arr.filter(function (n) { return n.status !== 1 && n.pinned; });
    var todoArr = arr.filter(function (n) { return n.status !== 1 && !n.pinned; });
    var doneArr = arr.filter(function (n) { return n.status === 1; });

    listEl.innerHTML =
      (pinArr.length
        ? '<div class="zone zone-pin" data-zone="pin">' + pinArr.map(cardHtml).join("") + '</div>'
        : '') +
      '<div class="zone" data-zone="todo">' + todoArr.map(cardHtml).join("") + '</div>' +
      (doneArr.length
        ? '<div class="zone zone-done" data-zone="done">' + doneArr.map(cardHtml).join("") + '</div>'
        : '');

    var old = document.querySelector(".empty");
    if (old) old.remove();
    if (!arr.length) {
      var e = document.createElement("div");
      e.className = "empty";
      e.textContent = query ? ('没有匹配「' + query + '」的笔记') : "还没有笔记。上面输入框打点字，按 Enter 就有了。";
      wrapEl.appendChild(e);
    }

    bind();
    initSortable();
    reportAllAtts(true);
    updateTopButton();

    if (query) {
      searchBar.classList.add("on");
      searchBar.textContent = "匹配 " + arr.length + " 条 · Enter 新建 · Esc 退出搜索";
    } else searchBar.classList.remove("on");

    // 0 ↔ N：卡片长/缩都不要跳。
    //  · 0 → N：附件区从 0 高度涨起来；
    //  · N → 0：先把卡片钉回旧高度，再过渡到新的矮高度 —— 两次赋值都发生在
    //    同一帧里，浏览器只会画一次，用户看到的第一帧仍然是旧高度，所以不跳。
    // 这段动画靠「布局逐帧变化」带动下面的卡片，所以这一帧不能再叠加 FLIP。
    var boxAnim = false;
    if (_flipPrev) {
      listEl.querySelectorAll(".note").forEach(function (c) {
        var p = _flipPrev[c.getAttribute("data-id")];
        if (!p) return;
        var ab = c.querySelector(".atts");
        var n = ab ? ab.querySelectorAll(".att").length : 0;
        if (p.n === 0 && n > 0) {
          attBoxGrow(ab);
          boxAnim = true;
        } else if (p.n > 0 && n === 0) {
          var nh = c.offsetHeight;
          if (p.h && Math.abs(p.h - nh) > 2) {
            c.style.overflow = "hidden";
            c.style.height = p.h + "px";
            void c.offsetHeight;
            c.style.transition = "height " + ATT_BOX_MS + "ms " + ATT_BOX_EASE;
            c.style.height = nh + "px";
            (function (card) {
              setTimeout(function () {
                card.style.transition = "";
                card.style.height = "";
                card.style.overflow = "";
              }, ATT_BOX_MS + 40);
            })(c);
            boxAnim = true;
          }
        }
      });
    }

    if (_flipPrev && !boxAnim) {
      listEl.querySelectorAll(".note").forEach(function (c) {
        var oldTop = _flipPrev[c.getAttribute("data-id")];
        if (!oldTop) return;
        var d = oldTop.top - c.getBoundingClientRect().top;
        if (Math.abs(d) < 1) return;
        try {
          // 跨区移动（待办 ⇄ 已完成、置顶 ⇄ 待办）的「飞过去」动画。
          // 2026-11-10 用户反馈：两个方向都「太快」。原因是固定 620ms + easeOutQuad
          //（起步即最高速），距离一远（比如从待办区顶部飞到已完成区底部，上千像素）
          // 前 100ms 就跑掉一大半路程，看着像瞬移。
          // 改法：①时长随距离增长（420~1200ms），近处不变慢、远处不瞬移；
          //       ②缓动换成 ease-in-out 一类（cubic-bezier(.45,0,.25,1)），
          //         起步和收尾都慢，中间最快——两头都不突兀。
          var dist = Math.abs(d);
          var dur = Math.round(Math.max(420, Math.min(1200, 300 + dist * 0.55)));
          c.animate([{ transform: "translateY(" + d + "px)" }, { transform: "none" }],
            { duration: dur, easing: "cubic-bezier(.45,0,.25,1)" });
        } catch (e) { }
      });
    }
    _flipPrev = null;
    syncScrollbarGutter();
    syncAllExpand();
  }

  /* 滚动条宽度补偿：窗口宽度固定时，滚动条一出现就会把卡片挤窄 6px。
     有滚动条 → padding-right 4（4+6=10）；没有 → padding-right 10（10+0=10）。
     两种状态下卡片可用宽度一致，滚动条也不会压住卡片右缘。 */
  function syncScrollbarGutter() {
    var lw = wrapEl || document.querySelector(".list-wrap");
    if (!lw) return;
    var barW = lw.offsetWidth - lw.clientWidth;
    var want = (barW > 0 ? 8 : 14) + "px";   // 4/10 → 8/14：卡片右边缘再左移 4px
    if (lw.style.paddingRight !== want) lw.style.paddingRight = want;
  }
  // 窗口缩放会改变换行高度 → 滚动条可能出现/消失，下一帧补一次
  window.addEventListener("resize", function () { requestAnimationFrame(syncScrollbarGutter); });

  /* 滚动条出现/消失不一定伴随 render（搜索过滤、卡片展开、窗口缩放都会），
     所以再挂一个 ResizeObserver：内容或容器尺寸一变就重算 padding。
     函数内部有「值没变就不写」的判断，不会自己触发自己。 */
  (function initScrollbarGutterWatch() {
    if (!wrapEl || !listEl || !window.ResizeObserver) return;
    try {
      var ro = new ResizeObserver(function () { syncScrollbarGutter(); });
      ro.observe(listEl);
      ro.observe(wrapEl);
    } catch (e) { }
  })();

  /* ---------- 右键 = 左键点待办圈（按下压感 / 移出取消 / 松手生效） ---------- */
  var _rpress = null;

  function rpHit(st, x, y) {
    function inside(el, pad) {
      if (!el) return false;
      var r = el.getBoundingClientRect();
      return x >= r.left - pad && x <= r.right + pad && y >= r.top - pad && y <= r.bottom + pad;
    }
    // 圈贴在卡片左侧外沿，算同一次交互的命中区
    return inside(st.card, 0) || inside(st.ring, 4);
  }
  function rpPaint(on) {
    if (!_rpress) return;
    _rpress.card.classList.toggle("pressing", on);
    if (_rpress.ring) _rpress.ring.classList.toggle("pressing", on);
  }
  function startRightPress(card, id) {
    cancelRightPress();
    _rpress = { card: card, ring: card.querySelector(".ring"), id: id, live: true };
    rpPaint(true);
  }
  function cancelRightPress() {
    if (!_rpress) return;
    var st = _rpress;
    _rpress = null;
    st.card.classList.remove("pressing");
    if (st.ring) st.ring.classList.remove("pressing");
  }
  document.addEventListener("mousemove", function (e) {
    if (!_rpress) return;
    // 右键已经在窗口外松开：直接作废
    if (!(e.buttons & 2)) { cancelRightPress(); return; }
    if (!_rpress.live) { return; }
    if (!rpHit(_rpress, e.clientX, e.clientY)) {
      // 移出卡片/圈 = 这次右键作废（松手也不生效），压感立刻收回
      _rpress.live = false;
      rpPaint(false);
    }
  }, true);
  document.addEventListener("mouseup", function (e) {
    if (e.button !== 2 || !_rpress) return;
    var st = _rpress;
    var ok = st.live && rpHit(st, e.clientX, e.clientY);
    cancelRightPress();
    if (ok) send("toggle|" + st.id);
  }, true);
  window.addEventListener("blur", function () { cancelRightPress(); });

  function bind() {
    listEl.querySelectorAll(".note").forEach(function (card) {
      var id = card.getAttribute("data-id");

      // 批量模式下：卡片上任何位置（正文、缩略图、删除角标、待办圈、勾选框）
      // 都只做一件事 —— 选中/取消这一条。用捕获阶段，先于附件/角标自己的点击处理。
      card.addEventListener("click", function (e) {
        if (!batchMode) return;
        e.preventDefault();
        e.stopPropagation();
        toggleBatchSel(id);
      }, true);

      card.addEventListener("mousedown", function (e) {
        if (e.button !== 0 || batchMode || editingId === id) return;
        // 抓取点必须在这里记：SortableJS 的 onStart 里拿不到鼠标位置（originalEvent 不稳定），
        // 记成 0 的话活动区上界会退化成列表上沿，卡片会压到输入框上。
        try {
          var cr0 = card.getBoundingClientRect();
          _grabY = Math.max(0, Math.min(cr0.height, e.clientY - cr0.top));
          _cardH = cr0.height;
        } catch (e9) {}
        selectedId = id;
        listEl.querySelectorAll(".note").forEach(function (c) {
          c.classList.toggle("selected", c.getAttribute("data-id") === id);
        });
      });

      // 右键 = 和「左键点待办圈」完全同一套交互，不是一按就切换：
      //   按下 → 整卡下压 + 圈进入按下态（缩一下 + 强调色描边）
      //   松手时指针仍在卡/圈范围内 → 生效（走 toggle 通道，重排和完成态动画与点圈一致）
      //   中途移出卡+圈 → 立刻取消，松手也不生效（和按住圈移出去一样）
      // 设置里可以关掉。
      card.addEventListener("mousedown", function (e) {
        if (e.button !== 2) return;
        if (!settings.rightDone || batchMode || editingId === id) return;
        if (e.target.closest(".att")) return;                       // 附件有自己的右键菜单
        if (e.target.closest("[contenteditable='true']")) return;   // 正在编辑文字
        e.preventDefault();
        e.stopPropagation();
        startRightPress(card, id);
      });
      // 系统右键菜单必须拦掉；真正的切换在 mouseup 里做
      card.addEventListener("contextmenu", function (e) {
        if (!settings.rightDone || batchMode || editingId === id) return;
        if (e.target.closest(".att")) return;
        if (e.target.closest("[contenteditable='true']")) return;
        e.preventDefault();
        e.stopPropagation();
      });

      card.addEventListener("mouseenter", function () { cardHoverOn(card); });
      card.addEventListener("mouseleave", function () { cardHoverOff(card); });

      card.addEventListener("dblclick", function (e) {
        // 只排除附件和操作按钮，卡片其余任何位置（文字、空白、色条）双击都复制
        if (e.target.closest(".att")) return;
        if (e.target.closest(".acts")) return;     // 编辑 / 删除
        if (e.target.closest(".ring")) return;     // 待办圈
        if (e.target.closest(".chk")) return;      // 批量勾选框
        if (e.target.closest("[contenteditable='true']")) return;   // 正在编辑
        if (editingId === id) return;
        var n = find(id);
        if (!n) return;
        var txt = n.text || "";
        if (txt.trim().length) { send("copy|" + txt); return; }        // 有文字 → 复制文字
        if (n.atts && n.atts.length) { send("copyatts|" + id); return; } // 没文字但有附件 → 复制全部附件
        showHint("这条没有可复制的内容");                                 // 两样都没有（正常会被自动删除）
      });

      // 待办圈：按住它绝不能触发卡片拖动。
      // filter 选择器在某些路径下不够（SortableJS 有时仍会先接管 pointer），
      // 这里在圈上直接把事件源头拦掉 —— 冒泡都到不了列表容器。
      var ringEl = card.querySelector(".ring");
      if (ringEl) {
        ["mousedown", "pointerdown", "touchstart"].forEach(function (evName) {
          ringEl.addEventListener(evName, function (e) {
            // 右键要冒泡到卡片上走「右键 = 点圈」那套交互，不能在这里被拦掉
            if (e.button === 2) return;
            e.stopPropagation();
          }, true);
        });
        // 拖动手势本身也要挡（防止 WebView2 里 pointer 已经进入拖动状态）
        ringEl.addEventListener("dragstart", function (e) {
          e.preventDefault();
          e.stopPropagation();
        }, true);
      }

      /* 置顶角标：和待办圈一样的四层拦截 —— 它是个按钮，按住它绝不能拖动整条笔记。
         只加 filter/onStart 不够（实测还能拖到窗口外），必须在捕获阶段把按下事件拦下来，
         让 Sortable 的容器监听根本收不到。 */
      var pinEl = card.querySelector(".pin");
      if (pinEl) {
        ["mousedown", "pointerdown", "touchstart"].forEach(function (evName) {
          pinEl.addEventListener(evName, function (e) {
            if (e.button === 2) return;
            e.stopPropagation();
          }, true);
        });
        pinEl.addEventListener("dragstart", function (e) {
          e.preventDefault();
          e.stopPropagation();
        }, true);
      }

      // Markdown 链接：点开用系统默认浏览器，绝不能让 WebView2 自己导航
      //（那会把整个笔记界面导航走，等于白屏）。
      card.querySelectorAll(".md-a").forEach(function (a) {
        a.addEventListener("click", function (e) {
          e.preventDefault();
          e.stopPropagation();
          var h = a.getAttribute("data-href") || "";
          if (h) send("openurl|" + encodeURIComponent(h));
        }, true);
      });

      card.querySelectorAll("[data-act]").forEach(function (el) {
        el.addEventListener("click", function (e) {
          e.stopPropagation();
          var act = el.getAttribute("data-act");
          if (act === "toggle") { if (!batchMode) send("toggle|" + id); }
          else if (act === "delete") { send("delete|" + id); showHint("已移入回收站"); }
          else if (act === "edit") { if (!batchMode) startEdit(id); }
          else if (act === "check") { toggleBatchSel(id); }
          else if (act === "color") { openColorPop(e, id); }
          else if (act === "pin") { if (!batchMode) send("pin|" + id); }
          else if (act === "expand") { toggleExpand(card, id); }
        });
      });

      card.querySelectorAll(".att").forEach(function (el) {
        var attId = el.getAttribute("data-att");
        var badge = el.querySelector(".x");
        if (badge) {
          ["mousedown", "pointerdown", "touchstart"].forEach(function (evName) {
            badge.addEventListener(evName, function (e) { e.stopPropagation(); }, true);
          });
        }
        ["mousedown", "pointerdown"].forEach(function (evName) {
          el.addEventListener(evName, function (e) { e.stopPropagation(); }, true);
        });
        el.addEventListener("mouseenter", function () { reportOneAtt(el); });
        el.addEventListener("click", function (e) {
          e.stopPropagation();
          if (e.target.getAttribute("data-act") === "delatt") { send("delatt|" + attId); showHint("已移除附件"); return; }
          send("open|" + attId);
        });
        el.addEventListener("contextmenu", function (e) {
          e.preventDefault(); e.stopPropagation();
          if (e.ctrlKey) { send("reveal|" + attId); showHint("已在资源管理器中定位"); }
          else { send("copyfile|" + attId); }
        });
      });
    });
  }

  function initSortable() {
    if (sortable) { try { sortable.destroy(); } catch (e) {} sortable = null; }
    sortables.forEach(function (x) { try { x.destroy(); } catch (e) {} });
    sortables = [];

    var zones = listEl.querySelectorAll(".zone");
    Array.prototype.forEach.call(zones, function (zone) {
      var inst = new Sortable(zone, {
        // 让位动画：普通卡片干脆利落（560ms / easeOutQuad）。
        // 超高卡片（>320px）每次让位要挪很远，相同时间里视觉上就是「飞」，
        // 所以在 onStart 里把这次拖动的 animation 调到 760。
        animation: 560,
        easing: "cubic-bezier(0.25, 0.46, 0.45, 0.94)",
        axis: "y",
        ghostClass: "ghost", chosenClass: "chosen", dragClass: "dragging",
        // 幽灵必须挂到 body：挂在滚动容器里时它是 absolute，top 是「内容坐标」，
        // 和列表的可视上下沿（视口坐标）不是一套数；列表一滚，卡片还会被内容带跑。
        // 挂 body 后 Sortable 用 position:fixed，top/transform 都是视口坐标，
        // 钳制、边缘滚动、松手滑回才能算得准。
        forceFallback: true, fallbackOnBody: true, fallbackTolerance: 3,
        filter: function (evt, target) {
          if (!target) return false;
          if (target.closest && target.closest(".ring")) return true;
          if (target.closest && target.closest(".chk")) return true;
          if (target.closest && target.closest(".pin")) return true;   // 置顶角标是按钮，不是拖拽把手
          // 附件区块（含右上角删除角标）有自己的拖动/点击语义，绝不能顺带把整条笔记拖走
          if (target.closest && target.closest(".att")) return true;
          if (target.closest && target.closest("[contenteditable='true']")) return true;
          return false;
        },
        preventOnFilter: false,
        disabled: batchMode,
        scroll: false,
        group: "zone-" + zone.getAttribute("data-zone"),   // 互不相通：拖不过另一个区
        onStart: function (evt) {
          try {
            var tg = evt && evt.originalEvent && evt.originalEvent.target;
            if (tg && tg.closest && tg.closest(".att")) {
              _sortDragging = false; document.body.classList.remove("sorting");
              try { inst.cancel(); } catch (e0) { }
              return;
            }
            if (tg && tg.closest && tg.closest(".pin")) {
              _sortDragging = false; document.body.classList.remove("sorting");
              try { inst.cancel(); } catch (e0) { }
              return;
            }
            if (tg && tg.closest && tg.closest(".ring")) {
              _sortDragging = false; document.body.classList.remove("sorting");
              try { inst.cancel(); } catch (e0) {}
              return;
            }
          } catch (e1) {}

          try {
            var oe0 = evt && evt.originalEvent;
            var item0 = evt && evt.item;
            if (oe0 && item0 && typeof oe0.clientY === "number") {
              var r0 = item0.getBoundingClientRect();
              _grabY = Math.max(0, Math.min(r0.height, oe0.clientY - r0.top));
              _cardH = r0.height;
              // 超高卡片这次拖动用更长的让位动画；普通卡片保持 560 的干脆感
              try { inst.options.animation = (r0.height > 320) ? 760 : 560; } catch (eA) { }
            }
          } catch (e2) {}

          _sortDragging = true; document.body.classList.add("sorting");
          clearAllHover();                 // 拖起来立刻清掉残留 hover，拖动过程中也不再点亮别的卡片
          _limitDbgDone = false;
          _boundsDumped = false;
          _dragStartedAt = Date.now();
          _dragMvN = 0; _dragMvLast = 0; _dragMvMaxGap = 0;
          updateTopButton();
          _virtualY = null;
          _lastClientY = null;
          _lastClientX = null;
          _dragInst = inst;
          _dragZoneEl = zone;
          _dragItem = evt && evt.item ? evt.item : null;
          _dragHomeParent = evt && evt.item ? evt.item.parentNode : null;
          _dragHomeNext = evt && evt.item ? evt.item.nextSibling : null;
          _dragHomeOrder = (_dragHomeParent && _dragHomeParent.querySelectorAll)
            ? Array.prototype.map.call(_dragHomeParent.querySelectorAll(".note"), function (c) {
                return c.getAttribute("data-id");
              })
            : null;
          _lastFbTop = null;
          _dragStartFbTop = null;
          _pressY = null;
          // 附件热区是 Topmost 独立窗口，会盖在页面上面。拖卡片时若从缩略图上滑过，
          // 指针会被热区吃掉 → 页面收不到 mousemove → 拖动卡住/抽搐。
          // 拖动期间先让 C# 把所有热区收起来，onEnd 的 reportAllAtts() 再叫回来。
          send("attrects|");
          try {
            var it0 = evt && evt.item;
            _dragDoneState = !!(it0 && it0.classList && it0.classList.contains("done"));
            if (it0) {
              var ir0 = it0.getBoundingClientRect();
              _dragStartFbTop = ir0.top;
              // 基准也必须是「手」：卡片中心会被边缘钳制，手不会
              _dragStartCenterY = (_grabY > 0 && _grabY < ir0.height)
                ? (ir0.top + _grabY) : (ir0.top + ir0.height / 2);
            } else _dragStartCenterY = null;
          } catch (e5) { _dragDoneState = false; _dragStartCenterY = null; }

          var hh = _cardH || 80, lt = 0, lb = window.innerHeight;
          var bd0 = dragBounds();
          // 直接用列表可视区域做光标活动范围，不再用 grabY/卡片高去收缩 ——
          // 收缩后按下点可能落在范围外，系统每 8ms 把光标拽回边界，卡片就抽搐/拖不动。
          lt = bd0.top;
          lb = bd0.bottom;
          if (lb - lt < 80) lb = lt + 80;
          _dragListTop = bd0.top;
          _dragListBottom = bd0.bottom;
          _dragListH = Math.max(1, bd0.bottom - bd0.top);
          send("draggrip|" + Math.round(_grabY) + "|" + Math.round(hh) + "|" + lt + "|" + lb);

          // 诊断：活动区/窗口/滚动容器尺寸（一次拖动只记一次）。
          // 「拖出显示区」如果伴随 wrapBot > innerH，就是容器底边超出窗口，钳制线失效。
          try {
            var wr0 = wrapEl.getBoundingClientRect();
            send("dbg|bounds top=" + Math.round(bd0.top) + " bot=" + Math.round(bd0.bottom) +
              " innerH=" + window.innerHeight + " wrapTop=" + Math.round(wr0.top) +
              " wrapBot=" + Math.round(wr0.bottom) + " wrapH=" + Math.round(wr0.height) +
              " cardH=" + Math.round(hh) + " scrollTop=" + Math.round(wrapEl.scrollTop));
            _boundsDumped = true;
          } catch (e9) { }

          send("attrects|");
          send("dragbegin");
          startEdgeScroll();
          startClampLoop();
        },
        onDrag: function () {
          try {
            var fb1 = document.querySelector(".sortable-fallback");
            if (fb1) {
              var rbd = forceFallbackPos(fb1);
              _lastFbTop = (rbd ? rbd.top : fb1.getBoundingClientRect().top);
            }
          } catch (e) {}
          updateOutZoneHint();
          updateDropLine();
        },
        onEnd: function (evt) {
          // 整个收尾都必须容错：这里一旦抛异常，Sortable 的 _nulling() 就执行不到，
          // 它内部的 _dragEl 永远非空 —— 下一次 mousedown 会被直接忽略，
          // 用户看到的就是「一个都拖不动了」。
          try {
          var item = (evt && evt.item) || _dragItem || null;
          // 指针松在另一个区里 = 这次拖放不成立，卡片要回到原来的位置。
          // 必须在清 _dragZoneEl 之前判断。
          var outZone = pointerOutOfZone(_dragZoneEl);
          // 跨区落点方向：目标区在本区上方 → 落到本区开头；在下方 → 落到本区末尾。
          // 拖动过程中占位预览就停在「本区里贴着目标区的那一端」，落点必须跟它一致。
          var outToTop = false;
          if (outZone && _dragZoneEl) {
            try { outToTop = pointerY() < _dragZoneEl.getBoundingClientRect().top; } catch (e0) { }
          }
          _sortDragging = false; document.body.classList.remove("sorting");
          _dragInst = null;
          updateTopButton();
          _virtualY = null;
          _dragZoneEl = null;
          _dragStartCenterY = null;
          _dragStartFbTop = null;
          try {
            var fbx = document.querySelector(".sortable-fallback");
            if (fbx) { fbx.classList.remove("out-zone"); fbx.style.clipPath = ""; }
          } catch (e6) {}
          stopEdgeScroll();
          clearLimitHint();
          hideDropLine();
          send("dragend");
          reportAllAtts(true);

          // 按 DOM 里两个区的顺序重建 notes —— DOM 本身就是分组后的结果
          var ids = [];
          listEl.querySelectorAll(".zone").forEach(function (z) {
            z.querySelectorAll(".note").forEach(function (c) {
              ids.push(c.getAttribute("data-id"));
            });
          });
          var map = {};
          notes.forEach(function (n) { map[n.id] = n; });

          var fromTop = _lastFbTop;
          _lastFbTop = null;
          // 诊断用：一次拖动里页面收到多少帧移动、最大断档多久。
          // 「拖不动」如果伴随 maxgap 很大，就是指针被别的原生窗吃掉了；
          // 如果帧数正常但卡片不动，就是位置被钳住了。只有一行成本。
          send("dbg|dragmv ms=" + (Date.now() - _dragStartedAt) +
            " n=" + _dragMvN + " maxgap=" + _dragMvMaxGap +
            " grab=" + Math.round(_grabY) + " h=" + Math.round(_cardH));
          send("dbg|noteend out=" + (outZone ? 1 : 0) +
            " from=" + (fromTop == null ? "null" : Math.round(fromTop)) +
            " item=" + (item && item.isConnected ? "in" : "out"));

          // 跨区松手：把 DOM 顺序还原成按下时的样子，再慢慢滑回原位。
          // 以前这里是交给自己那个 Sortable 实例算落点（会先跑到本区最后一个），
          // 看起来就是「一闪就回去了 / 根本没动画」。
          if (outZone) {
            // 跨区拖不动时**不要回原位**，而是落到「本区里与目标区相邻的那一端」——
            // 也就是拖动过程中占位预览 / 指示线所在的位置（用户 2026-11-10 定案）。
            //   目标区在本区下方（置顶→未完成、未完成→已完成）→ 本区末尾
            //   目标区在本区上方（已完成→未完成、未完成→置顶）→ 本区开头
            try {
              if (_dragHomeParent && item) {
                if (outToTop && _dragHomeParent.firstChild) {
                  _dragHomeParent.insertBefore(item, _dragHomeParent.firstChild);
                } else {
                  _dragHomeParent.appendChild(item);
                }
              }
            } catch (e7) { }
            _dragHomeParent = null;
            _dragHomeNext = null;
            _dragHomeOrder = null;
            // DOM 顺序变了，数据必须跟着重建，否则下次渲染又跳回原位置
            try {
              var idsX = [];
              listEl.querySelectorAll(".zone").forEach(function (z) {
                z.querySelectorAll(".note").forEach(function (c) {
                  idsX.push(c.getAttribute("data-id"));
                });
              });
              var rebuiltX = [];
              idsX.forEach(function (id) { if (map[id]) rebuiltX.push(map[id]); });
              if (rebuiltX.length === notes.length) {
                var changedX = rebuiltX.map(function (n) { return n.id; }).join(",") !==
                               notes.map(function (n) { return n.id; }).join(",");
                if (changedX) {
                  lastOrderSnap = notes.map(function (n) { return n.id; });
                  notes = rebuiltX;
                  send("reorder|" + notes.map(function (n) { return n.id; }).join(","));
                }
              }
            } catch (e8) { send("dbg|outzone-reorder-err " + (e8 && e8.message ? e8.message : e8)); }
            slideToSlot(item, fromTop, true);
            return;
          }
          _dragHomeParent = null;
          _dragHomeNext = null;
          _dragHomeOrder = null;

          // 筛选态（顶部输入框有字 = 搜索）：DOM 里只有匹配项，
          // 不能整表重建，否则没匹配到的笔记会丢。
          if (query) {
            var merged2 = mergeVisibleOrder(ids, map);
            if (!merged2) { slideToSlot(item, fromTop); return; }   // 数据对不上就放弃
            var changed2 = merged2.map(function (n) { return n.id; }).join(",") !==
                           notes.map(function (n) { return n.id; }).join(",");
            if (!changed2) { slideToSlot(item, fromTop); return; }
            lastOrderSnap = notes.map(function (n) { return n.id; });
            notes = merged2;
            send("reorder|" + notes.map(function (n) { return n.id; }).join(","));
            slideToSlot(item, fromTop);
            return;
          }

          var rebuilt = [];
          ids.forEach(function (id) { if (map[id]) rebuilt.push(map[id]); });
          if (rebuilt.length !== notes.length) return;   // 数据不一致就放弃，别把笔记弄丢

          var changed = rebuilt.map(function (n) { return n.id; }).join(",") !==
                        notes.map(function (n) { return n.id; }).join(",");

          if (!changed) {
            // 跨区拖出去了但放不下（group 隔离），DOM 没动 → 滑回原位
            slideToSlot(item, fromTop);
            return;
          }

          lastOrderSnap = notes.map(function (n) { return n.id; });
          notes = rebuilt;
          send("reorder|" + notes.map(function (n) { return n.id; }).join(","));
          slideToSlot(item, fromTop);   // 从松手位置平滑滑到落点
          } catch (err) {
            _sortDragging = false; document.body.classList.remove("sorting");
            _dragInst = null;
            try { stopEdgeScroll(); } catch (e1) { }
            try { clearLimitHint(); } catch (e2) { }
            try { send("dragend"); } catch (e3) { }
            send("dbg|onend-err " + (err && err.message ? err.message : err));
          }
        }
      });
      sortables.push(inst);
    });
  }



  /* ---------- 预览 / 收起：正文超过 15 行时给一个折叠开关 ----------
     展开状态存在 _expanded 里，卡片重渲染（改颜色、切完成、拖拽排序）后不会自己收回去。 */
  var _expanded = {};

  function textOverflows(el) {
    if (!el) return false;
    return el.scrollHeight > el.clientHeight + 1;
  }

  function syncExpand(card) {
    if (!card) return;
    var id = card.getAttribute("data-id");
    var textEl = card.querySelector("[data-act='text']");
    var btn = card.querySelector("[data-act='expand']");
    if (!textEl || !btn) return;
    var open = !!_expanded[id];
    textEl.classList.toggle("open", open);
    var over = open ? true : textOverflows(textEl);
    btn.style.display = over ? "" : "none";
    btn.textContent = open ? "收起" : "预览";
  }

  function toggleExpand(card, id) {
    if (!card) return;
    if (_expanded[id]) delete _expanded[id]; else _expanded[id] = 1;
    syncExpand(card);
  }

  function syncAllExpand() {
    if (!listEl) return;
    listEl.querySelectorAll(".note").forEach(function (c) { syncExpand(c); });
  }

  /* ---------- 编辑 ---------- */
  function startEdit(id) {
    var card = listEl.querySelector('.note[data-id="' + id + '"]');
    if (!card) return;
    var textEl = card.querySelector("[data-act='text']");
    if (!textEl) return;

    editingId = id;
    // 编辑态显示 **原始 Markdown 源**（不是渲染后的 HTML）：
    // 直接把 contenteditable 挂在渲染结果上，保存时 innerText 会丢掉 # * ` 这些标记。
    textEl.classList.remove("md");
    textEl.textContent = (find(id) || {}).text || "";
    textEl.setAttribute("contenteditable", "true");
    textEl.focus();
    var r = document.createRange();
    r.selectNodeContents(textEl); r.collapse(false);
    var sel = window.getSelection(); sel.removeAllRanges(); sel.addRange(r);

    var finish = function (commit) {
      if (editingId !== id) return;
      editingId = null;
      textEl.removeAttribute("contenteditable");
      var nt = textEl.innerText.replace(/\u00a0/g, " ").replace(/\r\n/g, "\n").trim();
      var n = find(id);
      if (commit && n && nt !== n.text) {
        n.text = nt;
        // encodeURIComponent 而不是自己转义 \n：文本里可能有 |（MD 表格）、反斜杠、
        // 引号，Split('|') 会把它撕开，转义序列 C# 侧也不会还原。
        send("update|" + id + "|" + encodeURIComponent(nt));
      }
      // 退出编辑都要回到渲染态（提交后 C# 会推一次全量，这一句保证「不提交」也复原）
      textEl.classList.add("md");
      if (n) textEl.innerHTML = mdHtml(n.text);
    };
    textEl.addEventListener("blur", function () { finish(true); }, { once: true });
    textEl.addEventListener("keydown", function (e) {
      if (e.key === "Escape") { e.preventDefault(); finish(false); textEl.blur(); }
      if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); finish(true); textEl.blur(); }
      e.stopPropagation();
    });
  }

  /* ---------- 颜色 ---------- */
  var colorTargetId = null;
  function openColorPop(e, id) {
    colorTargetId = id;
    var r = e.currentTarget.getBoundingClientRect();
    colorPop.innerHTML = '<i class="none" data-c="">×</i>' +
      NOTE_COLORS.map(function (c) { return '<i data-c="' + c + '" style="background:' + c + '"></i>'; }).join("");
    colorPop.style.left = Math.min(window.innerWidth - 226, r.left + 8) + "px";
    colorPop.style.top = Math.min(window.innerHeight - 96, r.top) + "px";
    colorPop.classList.add("on");
    // 色板是 HTML 弹层，压不住原生置顶的附件热区 —— 不撤掉的话点色块会点到后面的缩略图/卡片
    syncHotzones();
    colorPop.querySelectorAll("i").forEach(function (el) {
      el.onclick = function () {
        if (colorTargetId) send("color|" + colorTargetId + "|" + (el.getAttribute("data-c") || ""));
        colorPop.classList.remove("on");
        colorTargetId = null;
        syncHotzones();
      };
    });
  }
  document.addEventListener("mousedown", function (e) {
    if (!colorPop.classList.contains("on")) return;
    if (e.target.closest(".colorpop")) return;
    colorPop.classList.remove("on");
    syncHotzones();
  });

  /* ---------- 批量 ---------- */
  function toggleBatchSel(id) {
    if (batchSel[id]) delete batchSel[id]; else batchSel[id] = 1;
    var chk = listEl.querySelector('.note[data-id="' + id + '"] .chk');
    if (chk) { chk.classList.toggle("on", !!batchSel[id]); chk.textContent = batchSel[id] ? "✓" : ""; }
    updateBatchCnt();
  }
  function updateBatchCnt() {
    // 批量条不再显示文字提示（勾选框本身就是状态），保留函数给旧调用点用
  }
  function enterBatch(flag) {
    batchMode = flag;
    document.body.classList.toggle("batch", flag);
    batchBar.classList.toggle("on", flag);
    if (!flag) batchSel = {};
    // 批量模式下把附件热区整体撤掉：热区是原生窗，会把点击吃掉（变成打开文件），
    // 撤掉以后整卡任意位置（缩略图、删除角标、正文）都只做「选中」。
    send("batchmode|" + (flag ? "1" : "0"));
    syncHotzones();
    updateTopButton();
    updateBatchCnt();
    sortables.forEach(function (x) { try { x.option("disabled", flag); } catch (e) {} });
  }
  function doBatch(op) {
    var ids = Object.keys(batchSel);
    if (!ids.length) { showHint("先选几条"); return; }
    send("batch|" + op + "|" + ids.join(","));
    showHint(op === "delete" ? "已移入回收站" : (op === "done" ? "已设为已办" : "已设为待办"));
    // 批量操作点一次就结束，不必再手动退出
    enterBatch(false);
    batchSel = {}; render();
  }

  /* ---------- 面板 ---------- */
  /* 任一 HTML 弹窗（设置/回收站）打开时，必须把附件热区整批撤掉：
     热区是原生 Topmost 小窗，会浮在弹窗上面 —— 点面板就会点中看不见的缩略图，
     轻则没反应，重则把 .bat/.exe 附件当“打开文件”执行掉（实测踩过）。 */
  function syncHotzones() {
    var off = !!batchMode ||
      (settingsPanel && settingsPanel.classList.contains("on")) ||
      (trashPanel && trashPanel.classList.contains("on")) ||
      (colorPop && colorPop.classList.contains("on"));
    try { send("hotzones|" + (off ? 1 : 0)); } catch (e) { }
  }

  function openSettings(flag) {
    settingsPanel.classList.toggle("on", flag);
    if (flag) { renderPalettes(); renderEdgeColors(); renderEdgePulses(); try { send("state"); } catch (e) { } }
    syncHotzones();
  }
  var _trashFromSettings = false;   // 是不是从「设置 → 回收站」进来的
  function openTrash(flag) {
    trashPanel.classList.toggle("on", flag);
    if (flag) send("trash");
    syncHotzones();
  }

  /* ---------- 输入框 ---------- */
  if (inputEl) {
    var syncComposerClear = function () {
      var hasText = inputEl.value.length > 0;
      if (composerWrap) composerWrap.classList.toggle("hastext", hasText);
      if (composerHint) composerHint.classList.toggle("on", inputEl.value.trim().length > 0);
    };

    var clearComposer = function () {
      if (_qTimer) { clearTimeout(_qTimer); _qTimer = null; }
      inputEl.value = "";
      query = "";
      inputEl.style.height = "auto";
      syncComposerClear();
      render();
      inputEl.focus();
    };

    inputEl.addEventListener("input", function () {
      inputEl.style.height = "auto";
      inputEl.style.height = Math.min(220, Math.max(46, inputEl.scrollHeight)) + "px";
      syncComposerClear();
      var v = inputEl.value.trim();
      if (v === query) return;
      // 防抖：连续输入时只渲染最后一次，避免每个字符都重建整表（慢且闪）
      if (_qTimer) clearTimeout(_qTimer);
      _qTimer = setTimeout(function () {
        _qTimer = null;
        query = v;
        render();
      }, v ? 110 : 0);
    });
    inputEl.addEventListener("blur", function () {
      if (composerHint) composerHint.classList.remove("on");
    });
    inputEl.addEventListener("keydown", function (e) {
      if (e.key === "Escape") {
        if (inputEl.value || query) clearComposer();
        else if (batchMode) enterBatch(false);
        return;
      }
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        var v = inputEl.value.trim();
        if (!v) return;
        var _cbox = document.querySelector(".composer-box") || inputEl;
        var _cr = _cbox ? _cbox.getBoundingClientRect() : null;
        _addPending = _cr ? { top: _cr.top, left: _cr.left, w: _cr.width } : true;
        send("add|" + encodeURIComponent(v));
        inputEl.value = ""; query = "";
        inputEl.style.height = "auto";
        syncComposerClear();
        render();
      }
    });

    if (composerClear) {
      composerClear.addEventListener("mousedown", function (e) {
        e.preventDefault();
      });
      composerClear.addEventListener("click", function (e) {
        e.preventDefault();
        e.stopPropagation();
        clearComposer();
      });
    }
    syncComposerClear();
  }

  /* ---------- 按钮 ---------- */
  on("batchTodo", function () { doBatch("todo"); });
  on("batchDone", function () { doBatch("done"); });
  on("batchDel", function () { doBatch("delete"); });
  on("batchAll", function () {
    var all = visibleNotes();
    var allOn = Object.keys(batchSel).length < all.length;
    batchSel = {};
    if (allOn) all.forEach(function (n) { batchSel[n.id] = 1; });
    render(); updateBatchCnt();
  });
  on("batchExit", function () { enterBatch(false); });
  /* ---------- 卡片 hover 状态（带宽限）----------
     附件上面盖的是原生置顶小窗：指针从正文移到缩略图时，页面的 mouseleave 先到、
     C# 的 attHover(true) 后到，中间那一两帧就是「底部信息闪一下」。
     所以离开不立刻摘类，留 140ms 宽限；hover / attHover 再进来就取消。 */
  var _hoverTimers = new WeakMap();
  var _lastScrollAt = 0;          // 最近一次列表滚动时刻
  var _hoverPt = { x: -1, y: -1 };  // 指针位置（滚动停下后补 hover 用）
  function cardHoverOn(card) {
    if (!card) return;
    // 拖动期间一律不进入 hover 态：幽灵跟手滑过别的卡片时，那些卡片会收到
    // mouseenter，导致底部的「建 … / 编辑 / 删除」在拖动中乱闪（用户明确否掉的行为）。
    if (_sortDragging || _attDrag) return;
    // 滚动期间也不点亮：快速滚动时卡片一张张从指针下掠过，会连续触发 mouseenter，
    // 加上 140ms 宽限就变成「好几张卡的创建时间同时亮着」。滚动停下 240ms 后再补一次。
    if (Date.now() - _lastScrollAt < 160) return;
    var t = _hoverTimers.get(card);
    if (t) { clearTimeout(t); _hoverTimers["delete"](card); }
    card.classList.add("hovering");
  }
  function cardHoverOff(card) {
    if (!card) return;
    var t = _hoverTimers.get(card);
    if (t) clearTimeout(t);
    _hoverTimers.set(card, setTimeout(function () {
      _hoverTimers["delete"](card);
      card.classList.remove("hovering");
    }, 140));
  }
  /// 拖动一开始就把所有残留 hover 清干净（否则拖动前悬停的那张会一直亮着）
  function clearAllHover() {
    var cards = document.querySelectorAll(".note.hovering");
    for (var i = 0; i < cards.length; i++) cards[i].classList.remove("hovering");
  }

  /* 滚动 → 立刻清掉已亮的卡片；滚动停下 240ms 后，把指针下那张补回 hover 态
     （滚动过程中 mouseenter 被抑制了，不补的话停下后要动鼠标才有反应）。 */
  document.addEventListener("mousemove", function (e) {
    _hoverPt.x = e.clientX; _hoverPt.y = e.clientY;
    if (!_hoverFixPending) {
      _hoverFixPending = true;
      requestAnimationFrame(function () { _hoverFixPending = false; fixStaleHover(); });
    }
  }, true);
  var _hoverFixPending = false;
  /* 快速滚动时 mouseleave 有时不派发 → .hovering 会挂在已经滚走的卡片上，
     越滚越多（实测一次快速滚动后同时挂着 5 张）。指针一动就把非指针下的全清掉。 */
  function fixStaleHover() {
    var cards = document.querySelectorAll(".note.hovering");
    if (cards.length === 0) return;
    var el = document.elementFromPoint(_hoverPt.x, _hoverPt.y);
    var under = el && el.closest ? el.closest(".note") : null;
    for (var i = 0; i < cards.length; i++) if (cards[i] !== under) cards[i].classList.remove("hovering");
  }
  var _scrollHoverGuardAt = 0, _scrollEndTimer = 0;
  if (wrapEl) wrapEl.addEventListener("scroll", function () {
    var now = Date.now();
    _lastScrollAt = now;
    // body.scrolling 是 CSS 那一层的开关：`:hover` 由浏览器自己维护，JS 拦不住，
    // 只能让它整段失效。快速滚动时卡片连续掠过指针就不会再一张张点亮。
    document.body.classList.add("scrolling");
    clearAllHover();                                        // 每帧都清：滚动时本来就不该有 hover
    _scrollHoverGuardAt = now;
    clearTimeout(_scrollEndTimer);
    _scrollEndTimer = setTimeout(function () {
      document.body.classList.remove("scrolling");
      if (_sortDragging || _attDrag || batchMode) return;
      try {
        var el = document.elementFromPoint(_hoverPt.x, _hoverPt.y);
        var card = el && el.closest ? el.closest(".note") : null;
        if (card) cardHoverOn(card);
      } catch (e) { }
    }, 240);
  }, { passive: true });

  /* ---------- 待办条：按住那一刻的压下反馈 ---------- */
  function clearNotePress() {
    var a = document.querySelectorAll(".note.pressing");
    for (var i = 0; i < a.length; i++) a[i].classList.remove("pressing");
  }
  document.addEventListener("mousedown", function (e) {
    var t = e.target;
    if (!t || !t.closest) return;
    // 置顶角标也是按钮：按它绝不能整卡下压 —— 那看起来像「这条开始被拖了」（用户 2026-11-10 反馈）
    if (t.closest(".att") || t.closest(".ring") || t.closest(".acts") ||
        t.closest(".chk") || t.closest(".pin") || t.closest("[contenteditable='true']")) return;
    var n = t.closest(".note");
    if (n) n.classList.add("pressing");
  }, true);
  document.addEventListener("mouseup", clearNotePress, true);
  document.addEventListener("dragend", clearNotePress, true);
  window.addEventListener("blur", clearNotePress);

  on("setClose", function () { openSettings(false); });
  on("trashClose", function () {
    openTrash(false);
    // 从设置里点进来的（设置已经被收起），关掉回收站就退回设置，而不是把两个都关了
    if (_trashFromSettings) { _trashFromSettings = false; openSettings(true); }
  });
  on("trashEmpty", function () {
    if (!confirm("清空回收站里的全部内容？此操作不可恢复。")) return;
    send("empttrash");
  });
  // 窗口置顶设置已移到标题栏图钉按钮（C# 侧处理），设置面板不再有开关
  on("swAuto", function () {
    settings.autoStart = !settings.autoStart;
    document.getElementById("swAuto").classList.toggle("on", settings.autoStart);
    send("autostart|" + (settings.autoStart ? 1 : 0));
  });
  on("swClose", function () {
    settings.closeToTray = !settings.closeToTray;
    document.getElementById("swClose").classList.toggle("on", settings.closeToTray);
    send("closetotray|" + (settings.closeToTray ? 1 : 0));
  });
  on("btnDataRoot", function () { send("pickdataroot"); });
  on("rowTrash", function () {
    openSettings(false);          // 先收起设置（openSettings 会清掉这个标记，所以放在前面）
    _trashFromSettings = true;
    openTrash(true);
  });
  on("rowOrphan", function () { send("orphanscan"); });
  on("swRight", function () {
    settings.rightDone = !settings.rightDone;
    document.getElementById("swRight").classList.toggle("on", settings.rightDone);
    send("rightdone|" + (settings.rightDone ? 1 : 0));
  });

  /* ---------- C# 入口 ---------- */
  window.api = {
    setNotes: function (arr) {
      var keep = wrapEl ? wrapEl.scrollTop : 0;
      notes = arr || [];
      notes.sort(function (a, b) {
        if (a.status !== b.status) return a.status - b.status;
        var sa = (a.sort || a.created || 0), sb = (b.sort || b.created || 0);
        if (sa !== sb) return sb - sa;
        return (b.created || 0) - (a.created || 0);
      });

      var fresh = [];
      for (var i = 0; i < notes.length; i++) {
        if (!_prevNoteIds[notes[i].id]) fresh.push(notes[i]);
      }

      render(true);
      if (wrapEl) wrapEl.scrollTop = keep;

      // 按 Enter 新建：新卡片从「输入框那一格」滑下来落到自己的位置
      // （置顶区在上面时，就能看到它一路滑过置顶区，而不是凭空出现）
      if (_addPending) {
        var origin = _addPending === true ? null : _addPending;
        _addPending = false;
        try { if (wrapEl) wrapEl.scrollTop = 0; } catch (e0) { }   // 先保证落点可见
        if (fresh.length && origin) {
          try {
            var nl = listEl.querySelector('.note[data-id="' + fresh[0].id + '"]');
            if (nl) {
              var r = nl.getBoundingClientRect();
              var dy = origin.top - r.top;
              var dx = origin.left - r.left;
              var sc = Math.max(0.6, Math.min(1, origin.w / (r.width || 1)));
              try {
                nl.animate(
                  [
                    { transform: "translate(" + Math.round(dx) + "px," + Math.round(dy) + "px) scale(" + sc.toFixed(2) + ")", opacity: 0.55 },
                    { transform: "none", opacity: 1 }
                  ],
                  { duration: 620, easing: "cubic-bezier(.16,1,.3,1)", fill: "none" }
                );
              } catch (e1) { }
            }
          } catch (e2) { }
        }
      }

      // 拖文件新建：回到顶部 + 新卡片闪一下，让用户知道多了一条
      if (_dropNewPending) {
        _dropNewPending = false;
        animateScrollTo(0, null);
        if (fresh.length) flashNote(fresh[0].id);
      } else if (_dropFlashId) {
        var fid = _dropFlashId;
        _dropFlashId = null;
        flashNote(fid);
      }

      _prevNoteIds = {};
      for (var j = 0; j < notes.length; j++) _prevNoteIds[notes[j].id] = 1;
      updateDoneButton();
    },
    // 无主附件扫描结果：先让人看见数量与体积，确认后才永久删除
    orphanInfo: function (n, bytes) {
      var t = bytes >= 1048576 ? (bytes / 1048576).toFixed(1) + " MB"
            : bytes >= 1024 ? Math.round(bytes / 1024) + " KB" : bytes + " B";
      if (confirm("有 " + n + " 个无主文件（" + t + "），不属于任何待办。\n永久删除？")) send("orphanpurge");
    },
    setTrash: function (arr, meta) {
      var mt = document.getElementById("trashMeta");
      if (mt) mt.textContent = meta || "";
      trash = arr || [];
      if (!trash.length) {
        trashList.innerHTML = '<div style="color:var(--faint);font-size:12px;text-align:center;padding:26px 0">回收站是空的</div>';
        return;
      }
      trashList.innerHTML = trash.map(function (n) {
        return '<div class="trashitem" data-id="' + esc(n.id) + '">' +
          '<div class="t">' + esc(n.text) + '</div>' +
          '<div class="a"><span data-act="restore">恢复</span>' +
          '<span class="del" data-act="purge">彻底删除</span>' +
          '<span style="margin-left:auto;color:var(--faint)">' +
          (fmtDay(n.updated)?fmtDay(n.updated)+" ":"") + fmtTime(n.updated) + '</span></div></div>';
      }).join("");
      trashList.querySelectorAll("[data-act]").forEach(function (el) {
        el.onclick = function () {
          var item = el.closest(".trashitem");
          var id = item.getAttribute("data-id");
          if (el.getAttribute("data-act") === "restore") send("restore|" + id);
          else {
            if (!confirm("彻底删除这条？不可恢复。")) return;
            send("purge|" + id);
          }
        };
      });
    },
    setState: function (st) {
      settings = Object.assign(settings, st || {});
      var a = document.getElementById("swTop"); if (a) a.classList.toggle("on", !!settings.topmost);
      var b = document.getElementById("swAuto"); if (b) b.classList.toggle("on", !!settings.autoStart);
    var c2 = document.getElementById("swRight"); if (c2) c2.classList.toggle("on", !!settings.rightDone);
      var c3 = document.getElementById("swClose"); if (c3) c3.classList.toggle("on", !!settings.closeToTray);
      var dr = document.getElementById("dataRootPath"); if (dr && settings.dataRoot) dr.textContent = settings.dataRoot;
      var dz = document.getElementById("dataSize"); if (dz && settings.dataSize) dz.textContent = "已占用 " + settings.dataSize;
      applyPalette(settings.palette || 9);
    },
    setPalette: function (i) { settings.palette = i; applyPalette(i); },
    toggleBatch: function () { enterBatch(!batchMode); },
    /* 附件悬停 / 按下反馈：附件被 C# 的透明热区盖着，页面收不到鼠标事件，
       由热区转发（AttDragHost 的 MouseEnter/MouseLeave/MouseDown/Up）。 */
    attHover: function (id, on) {
      var el = document.querySelector('.att[data-att="' + id + '"]');
      if (!el) return;
      el.classList.toggle("hot", !!on);
      /* 附件上面盖的是原生置顶小窗：指针移上去后页面收不到 hover，
         .note:hover 失效 → 底部「建 xxx / 编辑 / 删除」会闪一下没了。
         这里给卡片补一个 .hovering，让这些信息在整张卡片范围内一直可见。 */
      var card = el.closest ? el.closest(".note") : null;
      if (card) { if (on) cardHoverOn(card); else cardHoverOff(card); }
    },
    attPress: function (id, on) {
      var el = document.querySelector('.att[data-att="' + id + '"]');
      if (el) el.classList.toggle("press", !!on);
    },
    /* 按在删除角标上：角标本身要有反馈（变红 + 缩一下），不然像没点到 */
    attBadge: function (id, on) {
      var el = document.querySelector('.att[data-att="' + id + '"]');
      if (el) el.classList.toggle("badgepress", !!on);
    },
    attDragBegin: function (id, x, y) { return attDragBegin(id, x, y); },
    attDragMove: function (id, x, y) { return attDragMove(id, x, y); },
    attDragEnd: function (id, x, y) { return attDragEnd(id, x, y); },
    nativeDropTarget: function (x, y) {
      try {
        var el = document.elementFromPoint(x, y);
        var card = el && el.closest ? el.closest(".note") : null;
        return card ? (card.getAttribute("data-id") || "") : "";
      } catch (e) { return ""; }
    },
    openTrash: function () { openTrash(true); },
    openSettings: function () { openSettings(true); },
    setMaximized: function () {},
    reportAttRects: function () { reportAllAtts(true); },
    /* 缩略图在后台生成完成后只替换这一个附件，不整页重渲染。 */
    setAttThumb: function (id, url) {
      try {
        var el = document.querySelector('.att[data-att="' + id + '"]');
        if (!el || !url) return false;
        var box = el.querySelector(".thumbbox");
        if (!box) return false;
        box.innerHTML = '<img class="thumb" draggable="false" src="' + url + '" alt="">';
        // 缩略图是后台异步生成的：视频的播放标要在缩略图真的出来之后补上
        if (el.getAttribute("data-kind") === "video") {
          var pb = document.createElement("span");
          pb.className = "play";
          pb.innerHTML = SVG_PLAY;
          el.appendChild(pb);
        }
        el.classList.add("has-thumb");
        reportAllAtts(true);
        return true;
      } catch (e) { return false; }
    },
    /* 分片上传的终态（C# 回调） */
    upDone: function (token) { upFinish(token, true, ""); },
    upFail: function (token, why) { upFinish(token, false, why); },
    /* 滚轮落在 C# 的透明热区上时由 C# 转发过来。
       C# 已经先收起发送滚轮的那个热区，后面的滚轮直接由页面原生处理。

       以前这里用 scrollBy({behavior:"smooth"})：连续几格滚轮会各自起一条平滑动画，
       后一条打断前一条、从当前中间位置重新起步 —— 手感上是「黏、跟不上手」。
       改成「累计一个目标值 + 每帧指数逼近」：连续滚轮的位移自然叠加，
       松手后自己收尾，和系统滚轮一样跟手。 */
    hotWheel: function (delta) {
      try {
        if (!wrapEl) return;
        var d = -(parseFloat(delta) || 0) * 0.9;
        if (!d) return;
        beginScrollBurst();
        _lastScrollAt = Date.now();
        _wheelAt = Date.now();
        var max = Math.max(0, wrapEl.scrollHeight - wrapEl.clientHeight);
        if (!_wheelAnim) { _wheelTarget = wrapEl.scrollTop; _wheelAnim = true; }
        _wheelTarget = Math.max(0, Math.min(max, _wheelTarget + d));
        if (!_wheelRaf) _wheelRaf = requestAnimationFrame(wheelStep);
        scheduleAttRestore();
      } catch (e) { }
    },
    hint: function (t) { showHint(t); }
  };

  /* ---------- 附件拖动（重排 / 跨笔记移动） ----------
     WebView2 里附件被 C# 的透明窗口盖着（为了能把文件拖到微信），鼠标事件到不了页面。
     所以拖动过程由 C# 转发坐标（CSS 像素、相对 WebView 客户区），这里只画幽灵和判落点。 */
  var _attDrag = null;

  function attBoxOf(noteEl, create) {
    if (!noteEl) return null;
    var b = noteEl.querySelector(".atts");
    if (!b && create) {
      // 目标笔记原本没有附件：临时造一个容器，位置和 render() 生成的完全一致
      // （.body 里、.text 之后、.foot 之前）。取消拖动时 render() 会重建。
      b = document.createElement("div");
      b.className = "atts";
      var body = noteEl.querySelector(".body");
      var foot = noteEl.querySelector(".foot");
      if (body) {
        if (foot && foot.parentNode === body) body.insertBefore(b, foot);
        else body.appendChild(b);
      } else noteEl.appendChild(b);
    }
    return b;
  }

  /* 元素的「布局位置」：从 rect 里扣掉正在跑的 FLIP 位移。
     直接读 rect 会读到动画中间态 —— 下一个 mousemove 拿它当基准，
     就会把旁边本来不该动的卡片也带出去（用户看到「第二个、第三个一起让位」）。 */
  function layoutRect(el) {
    var r = el.getBoundingClientRect();
    var out = { left: r.left, top: r.top, right: r.right, bottom: r.bottom,
                width: r.width, height: r.height };
    // getComputedStyle 是最贵的一步（会触发样式解析）。只有真的在跑 FLIP 的元素
    // 才需要扣掉动画位移；其它元素直接读布局矩形就够了。
    // 外部拖入时这个函数每帧要对一排附件各跑一次，省下来就是拖动跟手度。
    if (!el.__flip) return out;
    try {
      var tf = getComputedStyle(el).transform;
      if (tf && tf !== "none") {
        var m = /matrix\(([^)]+)\)/.exec(tf);
        if (m) {
          var v = m[1].split(",").map(parseFloat);
          if (v.length >= 6) {
            out.left -= v[4]; out.right -= v[4];
            out.top -= v[5]; out.bottom -= v[5];
          }
        }
      }
    } catch (e) { }
    return out;
  }

  /* ---------- 「这条待办有没有附件区」的高度动画 ----------
     0 个附件 ↔ 1 个附件时卡片会突然长高/塌下去（拖附件跨笔记、外部拖入、
     删掉最后一张都会遇到）。用 .atts 容器的高度过渡来表达：容器一帧一帧地
     长/缩，下面的卡片跟着布局平滑移动，和别的动画是同一条曲线。
     注意：这段动画期间不能再对下面的卡片做 FLIP（布局本身在动，会双倍位移）。 */
  // 附件换位/附件区高度变化：和待办条让位（560ms）同一节奏。
  // 之前 320ms，跨待办拖动时「其他文件滑开」一闪就过去了，看着很急。
  var ATT_BOX_MS = 520;
  var ATT_BOX_EASE = "cubic-bezier(.16,1,.3,1)";
  var ATT_BOX_TOP = 10;                 // 与 .atts{margin-top:10px} 一致

  function resetAttBox(box) {
    if (!box) return;
    if (box.__attAnim) { clearTimeout(box.__attAnim); box.__attAnim = 0; }
    box.style.transition = "";
    box.style.height = "";
    box.style.overflow = "";
    box.style.marginTop = "";
    box.style.display = "";
  }

  /* 空 → 有：从 0 高度涨到真实高度 */
  function attBoxGrow(box) {
    if (!box) return;
    var h = box.offsetHeight;
    if (!h) return;
    box.style.overflow = "hidden";
    box.style.height = "0px";
    box.style.marginTop = "0px";
    void box.offsetHeight;              // 让起始值先落地，否则不会有过渡
    box.style.transition = "height " + ATT_BOX_MS + "ms " + ATT_BOX_EASE +
                           ",margin-top " + ATT_BOX_MS + "ms " + ATT_BOX_EASE;
    box.style.height = h + "px";
    box.style.marginTop = ATT_BOX_TOP + "px";
    box.__attAnim = setTimeout(function () {
      box.__attAnim = 0;
      box.style.transition = "";
      box.style.height = "";
      box.style.overflow = "";
      box.style.marginTop = "";
    }, ATT_BOX_MS + 40);
  }

  /* 有 → 空：从旧高度缩到 0（内容跟着被裁掉，像手风琴收起来） */
  function attBoxShrink(box, fromH, removeAfter) {
    if (!box || !fromH) return;
    box.style.display = "flex";         // 抵消 .atts:empty{display:none}
    box.style.overflow = "hidden";
    box.style.height = fromH + "px";
    box.style.marginTop = ATT_BOX_TOP + "px";
    void box.offsetHeight;
    box.style.transition = "height " + ATT_BOX_MS + "ms " + ATT_BOX_EASE +
                           ",margin-top " + ATT_BOX_MS + "ms " + ATT_BOX_EASE;
    box.style.height = "0px";
    box.style.marginTop = "0px";
    box.__attAnim = setTimeout(function () {
      box.__attAnim = 0;
      box.style.transition = "";
      box.style.height = "";
      box.style.overflow = "";
      box.style.marginTop = "";
      box.style.display = "";
      if (removeAfter && box.parentNode) box.parentNode.removeChild(box);
    }, ATT_BOX_MS + 40);
  }

  /* 插入点：返回目标笔记 + 目标容器 + 插到哪个兄弟节点前面（null = 末尾）。
     落点判定分两步：先算指针落在「附件排的第几行」，再在这一行里按 x 定格子。 */

  /* 落点判定用的「自然布局」缓存。
     附件是 flex-wrap 的一排：把占位槽插进去会让这一行换行、其余附件整体重排。
     如果每帧都读实时 rect，就会变成：
       插进去 → 位置变了 → 判成不该插这儿 → 撤掉 → 位置又变回来 → 又判成该插这儿 …
     用户看到的就是「满行最右侧稍微一动就不断让位 / 恢复」。
     所以一律沿用「插入之前」量到的那组 rect，并且存成相对附件容器左上角的坐标：
     列表滚动、上面的卡片变高，都只需要加当前容器坐标，不用重新量
     （重新量到的是「插了占位之后」的布局，正是噪声来源）。 */
  var _hitLayout = { note: null, sig: "", rows: null };

  function hitResetLayout() { _hitLayout = { note: null, sig: "", rows: null }; }

  function hitLayout(noteEl, box, excludeEl) {
    var kids = Array.prototype.slice.call(box.querySelectorAll(".att")).filter(function (k) {
      return k !== excludeEl && !k.classList.contains("att-slot");
    });
    // 只有「这一排附件本身」变了（增删）或容器宽度变了才重算。
    // 占位槽插进来导致的高度变化不算 —— 那正是要把判定锁住的噪声。
    var sig = Math.round(box.getBoundingClientRect().width) + "|" +
      kids.map(function (k) { return k.getAttribute("data-att") || ""; }).join(",");
    if (_hitLayout.note === noteEl && _hitLayout.sig === sig && _hitLayout.rows) {
      return { kids: kids, rows: _hitLayout.rows };
    }
    var br = box.getBoundingClientRect();
    var rows = kids.map(function (k) {
      var r = layoutRect(k);
      return {
        l: Math.round(r.left - br.left), t: Math.round(r.top - br.top),
        r: Math.round(r.right - br.left), b: Math.round(r.bottom - br.top)
      };
    });
    _hitLayout = { note: noteEl, sig: sig, rows: rows };
    return { kids: kids, rows: rows };
  }

  /* 死区：想改判，指针必须从上一次真正生效的位置挪开 8px。
     满行最右边那一格的中线，正好压在「插到最后一格前」和「追加到下一行」的分界上，
     手抖 1~2px 就会来回翻；只按边界距离留余量不够（x、y 两个方向都会触发）。 */
  var PICK_DEAD = 8;
  var _pick = { key: "", x: 0, y: 0 };
  function hitResetPick() { _pick = { key: "", x: 0, y: 0 }; }
  function hitGate(key, cur, prop, x, y) {
    if (prop === cur) return cur;
    if (_pick.key === key &&
        Math.abs(x - _pick.x) < PICK_DEAD && Math.abs(y - _pick.y) < PICK_DEAD) return cur;
    _pick.key = key; _pick.x = x; _pick.y = y;
    return prop;
  }

  /* 把一排附件按视觉行分组（DOM 顺序即阅读顺序，相邻且 top 相近的算一行） */
  function hitRowsByLine(rows) {
    var out = [];
    for (var i = 0; i < rows.length; i++) {
      var g = out[out.length - 1];
      if (g && Math.abs(rows[g[0]].t - rows[i].t) <= 4) g.push(i);
      else out.push([i]);
    }
    return out;
  }

  /* 指针所在的那一行（不在任何一行里 → null） */
  function hitLineAt(rows, box, y) {
    var lines = hitRowsByLine(rows), br = box.getBoundingClientRect();
    for (var i = 0; i < lines.length; i++) {
      var g = lines[i];
      var top = rows[g[0]].t + br.top;
      var bot = rows[g[g.length - 1]].b + br.top;
      if (y >= top - 4 && y <= bot + 4) return g;
    }
    return null;
  }

  /* 该插到 kids 的第几个：落在附件那一排里 → 占位放在指针所在的那一格；否则追加到末尾。
     分界放在两格之间的空隙中间，不是格子的中线 ——
     指针还压在这一格上（哪怕已经过了中线）时，占位就留在这一格，
     不会「一过中线，下一格就开始让位」。
     sameNote（同一张卡内重排）时 kids 里没有自己，要把「自己原来那一格」补回格子序列，
     否则一抓起来就会被算成下一格。 */
  function hitPickIndex(box, kids, rows, x, y, sameNote, cur, key, homeRect, homeIdx) {
    var br = box.getBoundingClientRect();
    var line = hitLineAt(rows, box, y);
    if (!line) {
      if (sameNote) return cur < 0 ? kids.length : cur;
      return hitGate(key, cur, kids.length, x, y);
    }
    var gap = line.length > 1 ? (rows[line[1]].l - rows[line[0]].r) : 11;
    var firstRowT = rows[line[0]].t;

    // 自己原来那一格：同一张卡里、且就在这一行 → 补回格子序列
    var merged = !!(sameNote && homeRect && homeIdx >= 0 &&
      Math.abs(homeRect.t - firstRowT) <= 4 && homeIdx <= line[line.length - 1] + 1);
    // 自己原来那一格在更上面一行 → 这一行的格子整体往后挪一位
    var base = line[0] + (sameNote && homeRect && !merged && homeRect.t < firstRowT - 4 ? 1 : 0);

    var before = 0, placed = false;
    for (var i = 0; i < line.length; i++) {
      if (merged && !placed && homeIdx === line[i]) {
        if (x < homeRect.r + br.left + gap / 2) break;
        before++;
        placed = true;
      }
      if (x < rows[line[i]].r + br.left + gap / 2) break;
      before++;
    }
    if (merged && !placed && homeIdx === line[line.length - 1] + 1) {
      if (x >= homeRect.r + br.left + gap / 2) before++;
    }

    var prop = base + before;
    // 这一行已经满了：再插一格就会换行。指针还在这一行上（格子后面那点空隙）时，
    // 不要把占位甩到下一行第一个去。
    if (!sameNote && prop === line[line.length - 1] + 1 &&
        x <= br.left + (box.clientWidth || 0) && hitLineFull(box, rows, line)) {
      prop = line[line.length - 1];
    }
    var out = hitGate(key, cur, prop, x, y);
    if (out < 0) out = kids.length;
    if (out > kids.length) out = kids.length;
    return out;
  }

  /* 这一行再塞一格会不会换行（按容器宽度算，和 flex-wrap 一致） */
  function hitLineFull(box, rows, line) {
    var z = rows[line[line.length - 1]];
    var w = z.r - z.l;
    var gap = line.length > 1 ? (rows[line[1]].l - rows[line[0]].r) : 11;
    var cw = box.clientWidth;
    if (!cw) return false;
    return z.r + gap + w > cw + 1;
  }

  /* 元素当前插在 kids 里的第几个（不在这个容器里 → -1） */
  function hitIndexIn(box, el, kids) {
    if (!el || el.parentNode !== box) return -1;
    var n = 0;
    for (var c = box.firstElementChild; c && c !== el; c = c.nextElementSibling) {
      if (kids.indexOf(c) >= 0) n++;
    }
    return n;
  }

  function attInsertPointAt(x, y) {
    var el = document.elementFromPoint(x, y);
    var noteEl = el && el.closest ? el.closest(".note") : null;

    // 指针落在卡片之间的空隙时优先留在当前这条笔记，避免被 ±26px 兜底判到邻卡
    if (!noteEl && _attDrag && _attDrag.el) {
      var curNote = _attDrag.el.closest(".note");
      if (curNote) {
        var cr = curNote.getBoundingClientRect();
        if (y >= cr.top - 40 && y <= cr.bottom + 40) noteEl = curNote;
      }
    }
    if (!noteEl) {
      var best = null, bd = 1e9;
      document.querySelectorAll(".note").forEach(function (n) {
        var r = n.getBoundingClientRect();
        if (y < r.top - 26 || y > r.bottom + 26) return;
        var d = Math.abs(y - (r.top + r.height / 2));
        if (d < bd) { bd = d; best = n; }
      });
      noteEl = best;
    }
    if (!noteEl) return null;
    var box = attBoxOf(noteEl, true);
    if (!box) return null;
    var dragEl = _attDrag ? _attDrag.el : null;
    var lay = hitLayout(noteEl, box, dragEl);
    var kids = lay.kids;
    // 「同一张卡」按拖动开始时它在哪个容器算，不能按当前 parentNode 算：
    // 跨待办拖进来后元素立刻被搬进目标容器，按 parentNode 会误判成重排。
    var sameNote = !!(_attDrag && _attDrag.homeBox === box);
    var idx = hitPickIndex(box, kids, lay.rows, x, y, sameNote,
      hitIndexIn(box, dragEl, kids),
      (noteEl.getAttribute("data-id") || "") + "|a",
      _attDrag ? _attDrag.homeRect : null,
      _attDrag ? _attDrag.origIndex : -1);
    return { noteEl: noteEl, box: box, ref: kids[idx] || null, idx: idx };
  }

  /* 搬 DOM + FLIP：先记「看得见的位置」，取消旧动画，搬完再用布局位置补差值。
     看起来是「其他文件往旁边滑开」，且每次只会动真正换位的那几个。 */
  function attMoveWithFlip(box, el, ref) {
    if (!box) return false;
    if (ref === el) return false;
    var oldParent = el.parentNode;
    if (!ref && oldParent === box && !el.nextElementSibling) return false;
    if (ref && ref.previousElementSibling === el) return false;

    // 两个容器「有没有附件」的状态：0 ↔ N 时卡片高度会突变，搬完要补一段高度动画。
    // 先把可能还在跑的高度动画清掉，否则量到的是中间值。
    var boxStates = [];
    [oldParent, box].forEach(function (p) {
      if (!p) return;
      for (var q = 0; q < boxStates.length; q++) if (boxStates[q].el === p) return;
      resetAttBox(p);
      boxStates.push({
        el: p,
        had: p.querySelectorAll(".att").length,
        h: p.getBoundingClientRect().height
      });
    });

    // FLIP 要连「旧容器」一起量：跨笔记移动时，来源那边也要合拢
    var recs = [];
    var seen = [];
    [oldParent, box].forEach(function (p) {
      if (!p || seen.indexOf(p) >= 0) return;
      seen.push(p);
      Array.prototype.forEach.call(p.children, function (k) {
        if (k === el) return;
        var r = k.getBoundingClientRect();     // 视觉位置（含正在进行的动画）
        recs.push([k, r.left, r.top]);
      });
    });

    // 取消旧动画：否则新旧两层 transform 叠加，或者从动画中值起算，会抖/飞错位置
    recs.forEach(function (rec) {
      var k = rec[0];
      if (k.__flip) { try { k.__flip.cancel(); } catch (e) { } }
      k.__flip = null;
    });

    if (ref) box.insertBefore(el, ref); else box.appendChild(el);

    // 0 ↔ N 的高度变化补动画（拖附件跨笔记时卡片才不会瞬间长高/塌下去）
    boxStates.forEach(function (s) {
      var now = s.el.querySelectorAll(".att").length;
      if (s.had === 0 && now > 0) attBoxGrow(s.el);
      else if (s.had > 0 && now === 0) attBoxShrink(s.el, s.h, false);
    });

    recs.forEach(function (rec) {
      var k = rec[0];
      if (!k.parentNode) return;
      var r = layoutRect(k);                   // 新槽位的布局位置（已无动画）
      var dx = Math.round(rec[1] - r.left), dy = Math.round(rec[2] - r.top);
      if (!dx && !dy) return;
      try {
        var a = k.animate(
          [{ transform: "translate(" + dx + "px," + dy + "px)" }, { transform: "none" }],
          { duration: 620, easing: "cubic-bezier(.16,1,.3,1)" });
        k.__flip = a;
        a.onfinish = function () { if (k.__flip === a) k.__flip = null; };
        a.oncancel = function () { if (k.__flip === a) k.__flip = null; };
      } catch (e) { }
    });
    return true;
  }

  function clearAttOver() {
    document.querySelectorAll(".note.att-over").forEach(function (n) { n.classList.remove("att-over"); });
    document.querySelectorAll(".att.drop-before,.att.drop-after").forEach(function (a) {
      a.classList.remove("drop-before", "drop-after");
    });
  }

  function attDragBegin(id, x, y) {
    if (_attDrag) attDragEnd(null, null, null);
    hitResetLayout();
    hitResetPick();
    var el = document.querySelector('.att[data-att="' + id + '"]');
    if (!el) return false;
    var r = el.getBoundingClientRect();
    var g = el.cloneNode(true);
    g.removeAttribute("data-att");
    g.className = "att att-ghost";
    var gx = Math.round(x - r.width / 2), gy = Math.round(y - r.height / 2);
    // 起始位置直接写进 transform：以前是先 translate(0,0) 再过渡到光标，
    // 旧 .att 的 transition:transform 会让它「从程序最上面慢慢跑下来」。
    g.style.cssText = "position:fixed;left:0;top:0;margin:0;z-index:9000;" +
      "width:" + r.width + "px;height:" + r.height + "px;opacity:.92;pointer-events:none;" +
      "transition:none;will-change:transform;transform:translate3d(" + gx + "px," + gy + "px,0) scale(1.06)";
    document.body.appendChild(g);
    el.classList.add("dragging-att");
    el.classList.remove("hot", "press", "badgepress");
    var box = attBoxOf(el.closest(".note"));
    // 自己原来那一格的坐标（相对附件容器）：判定落点时要把这一格补回格子序列，
    // 否则刚抓起来那一刻指针就在「下一格」的判定里，一动手就整排让位。
    var homeRect = null;
    if (box) {
      var bbr = box.getBoundingClientRect();
      homeRect = {
        l: Math.round(r.left - bbr.left), t: Math.round(r.top - bbr.top),
        r: Math.round(r.right - bbr.left), b: Math.round(r.bottom - bbr.top)
      };
    }
    _attDrag = {
      id: id, el: el, ghost: g, w: r.width, h: r.height, homeBox: box, homeRect: homeRect,
      noteId: el.closest(".note").getAttribute("data-id"),
      origParent: el.parentNode, origNext: el.nextSibling,
      origIndex: box ? Array.prototype.indexOf.call(box.querySelectorAll(".att"), el) : -1
    };
    _attDrag.lx = x; _attDrag.ly = y;
    clearAllHover();                     // 拖附件时也别把沿途卡片点亮
    // 起步位置先记进死区：手抖 8px 内不换位
    _pick = { key: _attDrag.noteId + "|a", x: x, y: y };
    // 拖到列表上下沿要能自己滚：想放的目标常常不在当前这一屏
    edgeDragStart(attDragRefresh, x, y);
    attDragMove(id, x, y);
    return true;
  }

  function attDragRefresh() {
    if (!_attDrag) return;
    attDragMove(_attDrag.id, _attDrag.lx, _attDrag.ly);
  }

  function attDragMove(id, x, y) {
    if (!_attDrag || _attDrag.id !== id) return;
    _attDrag.lx = x; _attDrag.ly = y;
    edgeDragStart(attDragRefresh, x, y);
    // 幽灵同样不许离开列表可视区：指针顶到输入框/列表底时，它就停在边界上
    var bd = dragBounds();
    var gx = Math.round(x - _attDrag.w / 2);
    var gy = Math.round(y - _attDrag.h / 2);
    gx = Math.max(0, Math.min(window.innerWidth - _attDrag.w, gx));
    gy = Math.max(bd.top, Math.min(bd.bottom - _attDrag.h, gy));
    _attDrag.ghost.style.transform = "translate3d(" + gx + "px," + gy + "px,0) scale(1.06)";

    // 落点判定用「指针」而不是被钳制后的幽灵中心：
    // 拖到列表边缘时幽灵会停在边界，但手还在继续走，用幽灵中心会把落点判回上一行。
    clearAttOver();
    var hx = Math.max(6, Math.min(window.innerWidth - 6, x));
    var hy = Math.max(bd.top + 6, Math.min(bd.bottom - 6, y));
    var hit = attInsertPointAt(hx, hy);
    if (hit && hit.noteEl) {
      hit.noteEl.classList.add("att-over");
      attMoveWithFlip(hit.box, _attDrag.el, hit.ref);
    }
  }

  function attDragEnd(id, x, y) {
    // 哪怕这次不是同一个附件（事件乱序），也必须把边缘滚动停掉，
    // 否则它会拿着旧指针位置一直写 scrollTop。
    edgeDragStop();
    hitResetLayout();
    hitResetPick();
    if (!_attDrag) return;
    if (id != null && id !== _attDrag.id) return;
    var d = _attDrag;
    _attDrag = null;
    clearAttOver();

    if (x == null || y == null) {                 // 取消：也走同一段回槽动画，不要瞬间跳回
      try { d.origParent.insertBefore(d.el, d.origNext); } catch (e3) { }
      try {
        var gc = d.ghost;
        if (gc && gc.parentNode) {
          var to2 = d.el.getBoundingClientRect();
          gc.style.setProperty("transition",
            "transform .56s cubic-bezier(.22,1,.36,1), opacity .56s ease", "important");
          gc.getBoundingClientRect();
          gc.style.transform = "translate3d(" + Math.round(to2.left) + "px," +
                               Math.round(to2.top) + "px,0) scale(1)";
          setTimeout(function () {
            try { if (gc.parentNode) gc.parentNode.removeChild(gc); } catch (e4) { }
            try { d.el.classList.remove("dragging-att", "hot", "press", "badgepress"); } catch (e5) { }
          }, 600);
        } else {
          d.el.classList.remove("dragging-att", "hot", "press", "badgepress");
        }
      } catch (e2) {
        try { if (d.ghost && d.ghost.parentNode) d.ghost.parentNode.removeChild(d.ghost); } catch (e6) { }
        d.el.classList.remove("dragging-att", "hot", "press", "badgepress");
      }
      return;
    }

    // 落点命令先算好，等回槽动画结束再发送。
    // 以前一松手就 send(attmove)，C# 收到立刻 PushAll() 重渲染，
    // 元素当场出现在槽位 —— 幽灵的 560ms 动画被这次重渲染完全抢掉，
    // 用户看到的就是「一松手瞬间归位」。
    var box = d.el.parentNode;
    var noteEl = box && box.closest ? box.closest(".note") : null;
    var moveCmd = "";
    if (noteEl) {
      var noteId = noteEl.getAttribute("data-id");
      var kids = Array.prototype.slice.call(box.querySelectorAll(".att"));
      var k = kids.indexOf(d.el);
      // MoveAttachment 是「先 RemoveAt(old) 再 Insert(newIndex)」，
      // newIndex = 当前 DOM 里的下标即可（旧代码往后挪 +1，会插过头一位）。
      if (k >= 0) moveCmd = "attmove|" + d.id + "|" + noteId + "|" + k;
    }

    function finishAttDrop() {
      try { d.el.classList.remove("dragging-att", "hot", "press", "badgepress"); } catch (e) { }
      if (moveCmd) send(moveCmd);
    }

    // 松手：幽灵从光标位置滑到它真正的槽位，落地后再撤掉、再提交状态
    try {
      var to = d.el.getBoundingClientRect();
      var g = d.ghost;
      if (g && g.parentNode && to.width > 0) {
        g.style.width = to.width + "px";
        g.style.height = to.height + "px";
        // 松手后滑到槽位：560ms，慢到看得清走位，又不会拖沓。
        // 用 important：.att-ghost 上曾经有一条带 !important 的 transition:none，
        // 会把这条 inline 过渡一起压掉，表现就是「一松手瞬间归位」。
        g.style.setProperty("transition",
          "transform .56s cubic-bezier(.22,1,.36,1), opacity .56s ease", "important");
        g.getBoundingClientRect();          // 先把 transition 落下去，再改 transform，确保真的走动画
        g.style.transform = "translate3d(" + Math.round(to.left) + "px," +
                            Math.round(to.top) + "px,0) scale(1)";
        setTimeout(function () {
          try { if (g.parentNode) g.parentNode.removeChild(g); } catch (e) { }
          finishAttDrop();
        }, 600);
        return;
      }
    } catch (e) { }

    if (d.ghost && d.ghost.parentNode) { try { d.ghost.parentNode.removeChild(d.ghost); } catch (e7) { } }
    finishAttDrop();
  }

  /* 幽灵只该活在「C# 正在转发拖动」的那段时间里。
     鼠标在窗口外松开、热区被收起、页面重渲染……这些情况下 C# 收不到 MouseUp，
     页面就永远等不到 attDragEnd，幽灵会一直浮在列表上（列表能滚它却不动）。
     兜底：窗口失焦、或者页面上出现新的一次按下，说明任何幽灵都已经是死的。 */
  function killOrphanAttGhost() {
    if (!_attDrag) return;
    var d = _attDrag;
    _attDrag = null;
    try { clearAttOver(); } catch (e0) { }
    try { if (d.ghost && d.ghost.parentNode) d.ghost.parentNode.removeChild(d.ghost); } catch (e1) { }
    try {
      if (d.origParent && d.el && d.el.parentNode !== d.origParent)
        d.origParent.insertBefore(d.el, d.origNext);
    } catch (e2) { }
    try { if (d.el) d.el.classList.remove("dragging-att", "hot", "press", "badgepress"); } catch (e3) { }
  }
  window.addEventListener("blur", killOrphanAttGhost);
  document.addEventListener("pointerdown", function (e) {
    // 真拖动时鼠标被热区 HWND 捕获，页面收不到 pointerdown，所以这里命中就一定是残留
    if (_attDrag) killOrphanAttGhost();
  }, true);

  /* 按在待办条正文上（可能是要拖这条）→ 立刻把原生附件热区收起来。
     热区是盖在缩略图上的 Topmost 独立窗，收起要走消息通道，有十几毫秒延迟。
     等 Sortable 的 onStart 再收就晚了：指针已经从缩略图上滑过去，
     那几帧 mousemove 被热区吃掉，表现就是「按住下半部分往下拖，拖不动/一卡一卡」。 */
  document.addEventListener("pointerdown", function (e) {
    if (e.button !== 0 || _sortDragging || batchMode) return;
    var t = e.target;
    if (!t || !t.closest) return;
    if (!t.closest(".note")) return;
    if (t.closest(".att") || t.closest(".ring") || t.closest(".chk") ||
        t.closest("[contenteditable='true']")) return;
    beginScrollBurst();
    scheduleAttRestore();
  }, true);

  /* ---------- 启动 ---------- */
  window.addEventListener("DOMContentLoaded", function () {
    applyPalette(9);
    render();
    sendRetry("dbg|appjs " + APPJS_VER);
    sendRetry("ready");
    sendRetry("getstate");
  });

  // 如果脚本加载时 DOM 已就绪（DOMContentLoaded 已过），直接启动
  if (document.readyState === "complete" || document.readyState === "interactive") {
    setTimeout(function () {
      applyPalette(9);
      render();
      sendRetry("dbg|appjs " + APPJS_VER);
      sendRetry("ready");
      sendRetry("getstate");
    }, 0);
  }

  /* 滚动回调：每帧最多跑一次。
     以前每个 scroll 事件都跑「按钮状态 + 收起热区 + 重置计时」三遍，
     而按钮状态翻转还会触发全量 querySelectorAll('.att') 扫描 + 重报热区。 */
  if (wrapEl) wrapEl.addEventListener("scroll", function () {
    _lastScrollAt = Date.now();
    beginScrollBurst();
    scheduleAttRestore();
    if (_scrollRaf) return;
    _scrollRaf = requestAnimationFrame(function () {
      _scrollRaf = 0;
      updateTopButton();
      updateDoneButton();
    });
  }, { passive: true });
  if (topBtn) topBtn.addEventListener("click", scrollToTop);
  if (doneBtn) doneBtn.addEventListener("click", scrollToDone);
  if (wrapEl) {
    wrapEl.addEventListener("wheel", function () {
      // 滚轮已经落到页面上了，说明用户在用原生滚动 —— 立刻放弃我们的补间，
      // 否则两条 scrollTop 写入会互相打架（表现就是滚一小段卡住）。
      if (_wheelRaf) { cancelAnimationFrame(_wheelRaf); _wheelRaf = 0; }
      _wheelAnim = false;
      cancelTopScroll();
    }, { passive: true });
    wrapEl.addEventListener("touchstart", cancelTopScroll, { passive: true });
    wrapEl.addEventListener("mousedown", cancelTopScroll, { passive: true });
  }
  /* 拖动期间的边缘滚动：不依赖 SortableJS 的 onMove（forceFallback 下不可靠），
     用捕获阶段的全局 mousemove 直接驱动。 */
  document.addEventListener("mousemove", function (e) {
    _lastClientY = e.clientY;
    _lastClientX = e.clientX;
    if (_sortDragging) {
      _dragMvN++;
      var tn = Date.now();
      if (_dragMvLast && tn - _dragMvLast > _dragMvMaxGap) _dragMvMaxGap = tn - _dragMvLast;
      _dragMvLast = tn;
    }
    // 拖着卡片时收到「没有按键」的 mousemove = 那一下松手被原生层吞了
    // （热区抢焦点、窗口失焦、光标被 ClipCursor 顶在边界）。补一个 pointerup
    // 让 Sortable 正常收尾；否则列表会一直滚、底边红框一直闪。
    if (_sortDragging && e.buttons === 0 && Date.now() - _dragStartedAt > 200) {
      forceFinishSortDrag();
    }
    // 滚停后不再靠「鼠标一动就提前恢复热区」：热区本来就没在滚动时被收起来，
    // hover 一直是即时的；提前恢复只会多打一次全量 SetWindowPos，纯浪费。
    // 外部拖入时如果松手在窗口外，浏览器不会再派发 drop/dragend，
    // 蓝色「松开·加到这条」和落点预览会永久卡住。鼠标回到无按键状态就清掉。
    // 拖拽期间 dragover 是连续的；超过 200ms 没有 dragover 才认为是「已经松手了」，
    // 否则某些环境下拖拽中的 mousemove(buttons=0) 会把预览一闪一闪地清掉。
    if (e.buttons === 0 && (_dropTargetEl || _dropPreviewEl ||
        (composerWrap && composerWrap.classList.contains("dropping"))) &&
        (Date.now() - _lastDragOverAt > 200)) cleanupDropUi();
  }, true);

  document.addEventListener("dragover", function (e) { e.preventDefault(); });

  /* ---------- 外部文件拖入：占位预览 ----------
     落到某条笔记上 → 在那条的附件行里插入一个虚线空槽（和站内拖附件同一个位置算法、
     同一套 FLIP / 0↔N 高度动画）；落到空白或输入框 → 列表顶部一张虚线占位卡。
     全程不放文字提示。 */
  function dragHasFiles(dt) {
    try { return !!(dt && dt.types && Array.prototype.indexOf.call(dt.types, "Files") >= 0); } catch (e) { return false; }
  }
  /* 外部拖进来的文字 / 链接（微信链接卡片、浏览器地址栏、选中的一段话……）
     这类拖拽没有 Files，只有 text/plain、text/html、text/uri-list 里的一个或几个。 */
  var DROP_TEXT_TYPES = ["text/plain", "text/html", "text/uri-list"];
  function dragHasText(dt) {
    try {
      if (!dt || !dt.types) return false;
      for (var i = 0; i < DROP_TEXT_TYPES.length; i++) {
        if (Array.prototype.indexOf.call(dt.types, DROP_TEXT_TYPES[i]) >= 0) return true;
      }
      return false;
    } catch (e) { return false; }
  }
  function dragIsSupported(dt) { return dragHasFiles(dt) || dragHasText(dt); }
  /* 从本页自己选中的文字又拖回来：别再插一遍 */
  function dragIsTextInternal() {
    try {
      if (window.__ourDragActive && (Date.now() - (window.__ourDragAt || 0)) < 8000) return true;
      return !!(window.__internalTextDragAt && (Date.now() - window.__internalTextDragAt) < 2500);
    } catch (e) { return false; }
  }

  /* 把拖进来的内容还原成一段可读文本：
     - 有 URL（uri-list，或纯文本里带的链接）→ Markdown 链接 [标题](url)，
       标题优先取 html 里 <a> 的文字（微信卡片就是这个形状）
     - 只有 html → 转纯文本（网页富文本、卡片正文）
     - 最后退回 text/plain */
  function firstUrlIn(s) {
    if (!s) return "";
    var lines = String(s).split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      var l = lines[i].trim();
      if (!l || l.indexOf("#") === 0) continue;   // uri-list 的注释行
      if (/^https?:\/\//i.test(l)) return l;
    }
    return "";
  }
  function htmlLinkOf(html) {
    try {
      var d = document.createElement("div");
      d.innerHTML = html || "";
      var a = d.querySelector("a[href]");
      if (!a) return null;
      var href = a.getAttribute("href") || "";
      if (!/^https?:\/\//i.test(href)) return null;
      var title = (a.textContent || "").replace(/\s+/g, " ").trim();
      return { url: href, title: (title && title !== href) ? title : "" };
    } catch (e) { return null; }
  }
  function htmlToPlain(html) {
    try {
      var d = document.createElement("div");
      d.innerHTML = html || "";
      return (d.innerText || d.textContent || "").replace(/\u00a0/g, " ").replace(/[ \t]+\n/g, "\n").trim();
    } catch (e) { return ""; }
  }
  function extractDropText(dt) {
    var html = "", plain = "", uri = "";
    try { html = dt.getData("text/html") || ""; } catch (e1) { }
    try { plain = dt.getData("text/plain") || dt.getData("Text") || ""; } catch (e2) { }
    try { uri = dt.getData("text/uri-list") || ""; } catch (e3) { }
    // 少数来源（微信/浏览器）只挂老式格式名，逐个再试
    if (!uri) { try { uri = dt.getData("URL") || dt.getData("text/x-moz-url") || dt.getData("DownloadURL") || ""; } catch (e4) { } }
    var link = htmlLinkOf(html);
    var url = firstUrlIn(uri) || firstUrlIn(plain) || (link ? link.url : "");
    if (url) {
      var title = (link && link.title) || "";
      if (!title) {
        // 纯文本里「标题 + 链接」两行是常见形状，取第一个不是 URL 的行当标题
        var ls = plain.split(/\r?\n/);
        for (var i = 0; i < ls.length; i++) {
          var t = ls[i].trim();
          if (t && !/^https?:\/\//i.test(t)) { title = t.length > 120 ? t.slice(0, 120) + "…" : t; break; }
        }
      }
      return title ? "[" + title + "](" + url + ")" : url;
    }
    var txt = html ? htmlToPlain(html) : "";
    if (!txt) txt = plain.replace(/\u00a0/g, " ").trim();
    if (txt.length > 8000) txt = txt.slice(0, 8000) + "…";
    return txt;
  }
  function dragIsInternal() {
    try {
      if (window.__ourDragActive && (Date.now() - (window.__ourDragAt || 0)) < 8000) return true;
      return !!(window.__internalDragAt && (Date.now() - window.__internalDragAt) < 2500);
    } catch (e) { return false; }
  }
  // 有些来源（微信链接卡片）压根不给拖拽数据，退一步支持「复制 → 粘贴」：
  // 整段粘贴就是一条链接时，自动落成 Markdown 链接，而不是一串裸文本。
  function pasteLinkAsMarkdown(e) {
    var t = e.target;
    if (!t || t.tagName !== "TEXTAREA") return;
    var dt = e.clipboardData;
    if (!dt) return;
    var html = "", plain = "";
    try { html = dt.getData("text/html") || ""; } catch (e1) { }
    try { plain = dt.getData("text/plain") || ""; } catch (e2) { }
    var link = htmlLinkOf(html);
    var url = (link && link.url) || firstUrlIn(plain) || "";
    if (!url) return;
    if (plain.length > 600) return;
    var rest = plain.split(url).join(" ").replace(/\s+/g, " ").trim();
    if (rest.length > 120) return;
    var title = (link && link.title) || (rest && rest !== url ? rest : "");
    if (!title) return;
    var md = "[" + title + "](" + url + ")";
    e.preventDefault();
    var s = t.selectionStart, en = t.selectionEnd, v = t.value || "";
    t.value = v.slice(0, s) + md + v.slice(en);
    t.selectionStart = t.selectionEnd = s + md.length;
    try { t.dispatchEvent(new Event("input", { bubbles: true })); } catch (e3) { }
  }
  document.addEventListener("paste", pasteLinkAsMarkdown, true);
  function clearDropTargetCard() {
    if (_dropTargetEl) { try { _dropTargetEl.classList.remove("drop-target"); } catch (e) { } _dropTargetEl = null; }
  }
  function hideDropPreview() {
    if (_dropPreviewEl && _dropPreviewEl.parentNode) _dropPreviewEl.parentNode.removeChild(_dropPreviewEl);
    _dropPreviewEl = null;
  }

  /* ---------- 拖到输入框：框内长出虚线占位 ---------- */
  var composerDropEl = document.getElementById("composerDrop");
  var composerAreaEl = document.querySelector(".composer-area");
  var _cdropCount = 0;
  var _cdropText = false;

  function hideComposerDrop(instant) {
    if (!composerWrap || !composerWrap.classList.contains("dropping")) return;
    if (instant) {
      composerWrap.classList.add("cdrop-instant");
      composerWrap.classList.remove("dropping");
      void composerWrap.offsetHeight;         // 先把无过渡状态落下去
      setTimeout(function () { composerWrap.classList.remove("cdrop-instant"); }, 60);
    } else {
      composerWrap.classList.remove("dropping");
    }
    if (composerDropEl) composerDropEl.innerHTML = "";
    _cdropCount = 0;
    _cdropText = false;
  }

  function showComposerDrop(n, mode) {
    if (!composerWrap || !composerDropEl) return;
    var isText = mode === "text";
    var cnt = Math.max(1, Math.min(4, n || 1));
    if (!composerWrap.classList.contains("dropping") || cnt !== _cdropCount || isText !== _cdropText) {
      var html = "";
      if (isText) {
        // 文字/链接：不摆附件格子（那是"要传文件"的意思），用一块虚线提示
        html = '<span class="chint">松开 · 添加到新待办</span>';
      } else {
        for (var i = 0; i < cnt; i++) html += '<i class="slot"></i>';
        if (n > cnt) html += '<span class="cdrop-more">+' + (n - cnt) + '</span>';
      }
      composerDropEl.innerHTML = html;
      _cdropCount = cnt;
      _cdropText = isText;
    }
    composerWrap.classList.add("dropping");
  }

  var _extOn = false;
  var _extSlot = null;      // 目标笔记里的虚线空槽
  var _extNote = null;      // 空槽所在的卡片（落点就是它）
  var _extNoteId = "";      // 记 id：拖动过程中卡片万一被重渲染替换，落点也不会丢

  /* 第一次进来自动让 C# 把附件热区全部收起来：
     热区是 Topmost 的独立窗，会把 OLE 的命中抢走 —— 页面收不到连续的 dragover，
     预览就卡着不动，拖动本身也发涩。 */
  function extBegin() {
    if (_extOn) return;
    _extOn = true;
    hitResetLayout();
    hitResetPick();
    try { send("extdrag|1"); } catch (e) { }
  }
  function extEnd(instant) {
    edgeDragStop();
    var cdropOn = !!(composerWrap && composerWrap.classList.contains("dropping"));
    if (!_extOn && !_extSlot && !_dropPreviewEl && !_dropTargetEl && !cdropOn) return;
    _extOn = false;
    hitResetPick();
    removeExtSlot(!instant);
    hideDropPreview();
    clearDropTargetCard();
    hideComposerDrop(true);
    _extNote = null;
    _extNoteId = "";
    _dropApplied = false; _dropAX = -9999; _dropAY = -9999;
    try { send("extdrag|0"); } catch (e) { }
  }

  function ensureExtSlot() {
    if (_extSlot) return _extSlot;
    var s = document.createElement("div");
    s.className = "att att-slot";
    // 文件名那行留个空位（高度与真实附件一致），预览不会比落地后矮一截
    s.innerHTML = '<div class="thumbbox"></div><div class="nm"></div>';
    _extSlot = s;
    return s;
  }

  function removeExtSlot(animate) {
    if (!_extSlot) return;
    var s = _extSlot;
    _extSlot = null;
    var box = s.parentNode;
    try { if (box) box.removeChild(s); } catch (e) { }
    if (animate && box && box.parentNode && !box.querySelector(".att")) {
      attBoxShrink(box, box.getBoundingClientRect().height, true);
    }
  }

  /* 和 attInsertPointAt 同一套「阅读顺序」判定，只是没有正在拖的元素 */
  function extInsertPointAt(x, y) {
    var el = null;
    try { el = document.elementFromPoint(x, y); } catch (e) { }
    var noteEl = el && el.closest ? el.closest(".note") : null;
    if (!noteEl) return null;
    var box = attBoxOf(noteEl, true);
    if (!box) return null;
    var lay = hitLayout(noteEl, box, null);
    var kids = lay.kids;
    // 只有指针真的压在附件那一排上，才按格子插；落在标题/正文/卡片空白处一律追加到末尾。
    var idx = hitPickIndex(box, kids, lay.rows, x, y, false,
      hitIndexIn(box, _extSlot, kids),
      (noteEl.getAttribute("data-id") || "") + "|e");
    return { noteEl: noteEl, box: box, ref: kids[idx] || null, idx: idx };
  }

  function showDropPreview() {
    if (_dropPreviewEl || !wrapEl) return;
    var d = document.createElement("div");
    d.id = "droppreview";
    d.innerHTML = '<div class="npthumb"></div><div class="nplines"><i></i><i></i></div>';
    document.body.appendChild(d);
    var wr = wrapEl.getBoundingClientRect();
    d.style.left = (wr.left + 31) + "px";
    d.style.top = (wr.top + 6) + "px";
    d.style.width = Math.max(140, wr.width - 31 - 16) + "px";
    _dropPreviewEl = d;
  }

  function updateDropUi(x, y) {
    // 指针在输入框上：框里长出占位（松手 = 新建一条待办）
    if (composerAreaEl) {
      var cr = composerAreaEl.getBoundingClientRect();
      if (x >= cr.left && x <= cr.right && y >= cr.top && y <= cr.bottom) {
        removeExtSlot(true);
        hideDropPreview();
        clearDropTargetCard();
        _extNote = null;
        _extNoteId = "";
        showComposerDrop(_dragFileCount, _dragKind);
        return;
      }
    }
    hideComposerDrop(false);

    /* 文字/链接：只在卡片上打高亮，不往附件行里插虚线格
       （虚线格是"传文件"的语义，插了反而误导，还会在卡片里留下空附件区） */
    if (_dragKind === "text") {
      var tel = null;
      try { tel = document.elementFromPoint(x, y); } catch (e5) { }
      var tcard = tel && tel.closest ? tel.closest(".note") : null;
      removeExtSlot(true);
      _extNote = null;
      _extNoteId = "";
      if (tcard) {
        hideDropPreview();
        _extNote = tcard;
        _extNoteId = tcard.getAttribute("data-id") || "";
        if (_dropTargetEl !== tcard) {
          clearDropTargetCard();
          _dropTargetEl = tcard;
          tcard.classList.add("drop-target");
        }
        return;
      }
      clearDropTargetCard();
      if (!wrapEl) return;
      var twr = wrapEl.getBoundingClientRect();
      if (x >= twr.left && x <= twr.right && y >= twr.top - 46 && y <= twr.bottom) showDropPreview();
      else hideDropPreview();
      return;
    }

    var hit = extInsertPointAt(x, y);
    if (hit) {
      hideDropPreview();
      _extNote = hit.noteEl;
      _extNoteId = hit.noteEl.getAttribute("data-id") || "";
      if (_dropTargetEl !== hit.noteEl) {
        clearDropTargetCard();
        _dropTargetEl = hit.noteEl;
        hit.noteEl.classList.add("drop-target");
      }
      attMoveWithFlip(hit.box, ensureExtSlot(), hit.ref);
      return;
    }
    // 空白 / 输入框：这次松手会新建一条
    removeExtSlot(true);
    clearDropTargetCard();
    _extNote = null;
    _extNoteId = "";
    if (!wrapEl) return;
    var wr = wrapEl.getBoundingClientRect();
    // 列表区 + 输入框（列表上方 46px）都算「新建」
    if (x >= wr.left && x <= wr.right && y >= wr.top - 46 && y <= wr.bottom) showDropPreview();
    else hideDropPreview();
  }

  var _dropWatchTimer = 0;
  function clearDropWatch() {
    if (_dropWatchTimer) { clearTimeout(_dropWatchTimer); _dropWatchTimer = 0; }
  }
  function armDropWatch() {
    clearDropWatch();
    _dropWatchTimer = setTimeout(function () {
      _dropWatchTimer = 0;
      cleanupDropUi();
    }, 3500);
  }
  function cleanupDropUi() { clearDropWatch(); extEnd(true); }

  // 拖拽时 dragover 是 60~120Hz，重活（elementFromPoint + FLIP）必须每帧最多一次
  var _dragPt = null, _dragRaf = 0;
  var _dropApplied = false, _dropAX = -9999, _dropAY = -9999;
  var _dropDoneAt = 0, _lastTypesLogged = "";
  /* 边缘自动滚了一帧之后：内容动了、指针没动，落点必须重算 */
  function dropRefresh() {
    if (!_dragPt) return;
    updateDropUi(_dragPt.x, _dragPt.y);
    _dropAX = _dragPt.x; _dropAY = _dragPt.y; _dropApplied = true;
  }
  document.addEventListener("dragover", function (e) {
    // 刚松手后的几百毫秒里原生层还会补发 dragover，别让预览又长回来
    if (_dropDoneAt && Date.now() - _dropDoneAt < 1500) return;
    if (dragIsInternal() || dragIsTextInternal()) return;
    if (!dragIsSupported(e.dataTransfer)) {
      // 诊断：把陌生拖拽的 types 记进 bridge.log，用来查微信卡片这类拖不进来的来源
      try {
        var ty = (e.dataTransfer && e.dataTransfer.types)
          ? Array.prototype.slice.call(e.dataTransfer.types).join(",") : "none";
        if (ty !== _lastTypesLogged) { _lastTypesLogged = ty; send("droptypes|" + ty); }
      } catch (eT) { }
      return;
    }
    e.preventDefault();
    // 明确要副本：不写这一句，某些来源（Word/微信文字）会按 move 处理，原文就被拿走了
    try { e.dataTransfer.dropEffect = "copy"; } catch (eD) { }
    _lastDragOverAt = Date.now();
    _dragKind = dragHasFiles(e.dataTransfer) ? "file" : "text";
    try {
      var its = e.dataTransfer.items;
      if (_dragKind === "file" && its && its.length) _dragFileCount = its.length;
      else if (_dragKind === "file" && e.dataTransfer.files && e.dataTransfer.files.length) _dragFileCount = e.dataTransfer.files.length;
      else _dragFileCount = 1;
    } catch (e12) { }
    extBegin();
    _dragPt = { x: e.clientX, y: e.clientY };
    // 拖到列表上下沿就自己滚（不然这一屏以外的待办根本放不进去）
    edgeDragStart(dropRefresh, e.clientX, e.clientY);
    if (_dragRaf) return;
    _dragRaf = requestAnimationFrame(function () {
      _dragRaf = 0;
      if (!_dragPt) return;
      // 已经摆好预览、指针又几乎没动：没必要每帧再跑一遍 elementFromPoint + 一排 rect。
      var mdx = _dragPt.x - _dropAX, mdy = _dragPt.y - _dropAY;
      if (_dropApplied && mdx * mdx + mdy * mdy < 6) { armDropWatch(); return; }
      updateDropUi(_dragPt.x, _dragPt.y);
      _dropAX = _dragPt.x; _dropAY = _dragPt.y; _dropApplied = true;
      armDropWatch();
    });
  }, true);

  document.addEventListener("dragleave", function (e) {
    if (!dragIsSupported(e.dataTransfer)) return;
    if (!e.relatedTarget) cleanupDropUi();
  }, true);

  document.addEventListener("dragend", function () { window.__ourDragActive = false; cleanupDropUi(); }, true);
  window.addEventListener("blur", cleanupDropUi);

  // 双保险：即使 C# 热区因为系统层级问题没盖住，也不允许缩略图变成浏览器原生图片拖拽。
  // 否则 Chrome 会把 img 的缓存文件当成“新拖入文件”，名字会变成 ico_xxx.png。
  document.addEventListener("dragstart", function (e) {
    window.__ourDragActive = true;
    window.__ourDragAt = Date.now();
    var t = e.target;
    if (t && t.closest && (t.closest(".att") || t.closest(".atts"))) {
      e.preventDefault();
      e.stopPropagation();
      return;
    }
    // 本页里选中的文字被拖出去：打上时间戳，2.5 秒内拖回本页不当作"外部拖入"
    try {
      var sel = document.getSelection();
      if (sel && String(sel).trim().length) window.__internalTextDragAt = Date.now();
    } catch (e13) { }
  }, true);

  /* ---------- 拖入的文件：分片上传（没有单文件上限） ----------
     浏览器不给本地路径，大文件也不能整块 base64。这里按 2MB 切片顺序送到 C#，
     C# 收到哪片写哪片，收完再搬进 blobs 建附件。
     上传卡就长在刚才那个虚线占位槽的位置：进度环 → 完成 → 失败，全在原地表达。 */
  var UP_CHUNK = 2 * 1024 * 1024;
  var RING_R = 15;
  var RING_C = 2 * Math.PI * RING_R;
  var _uploads = {};          // token → { tile, name, size, done }
  var _shelf = null;          // 新建笔记时用的悬浮架（还没有卡片可挂）

  function upTile(name) {
    var t = document.createElement("div");
    t.className = "att att-up";
    t.innerHTML =
      '<div class="thumbbox">' +
        '<svg class="upring" viewBox="0 0 36 36">' +
          '<circle class="bg" cx="18" cy="18" r="' + RING_R + '"></circle>' +
          '<circle class="fg" cx="18" cy="18" r="' + RING_R + '" ' +
            'stroke-dasharray="' + RING_C.toFixed(2) + '" stroke-dashoffset="' + RING_C.toFixed(2) + '"></circle>' +
        '</svg>' +
        '<span class="pc">0%</span>' +
      '</div>' +
      '<div class="nm"><span class="bn">' + esc(name) + '</span></div>';
    return t;
  }

  function upProgress(token, p) {
    var st = _uploads[token];
    if (!st || st.done) return;
    var fg = st.tile.querySelector(".fg");
    var pc = st.tile.querySelector(".pc");
    if (fg) fg.setAttribute("stroke-dashoffset", (RING_C * (1 - p)).toFixed(2));
    if (pc) pc.textContent = Math.min(99, Math.round(p * 100)) + "%";
  }

  function shelfAdd(tiles) {
    if (!_shelf) {
      _shelf = document.createElement("div");
      _shelf.id = "upshelf";
      document.body.appendChild(_shelf);
      var wr = wrapEl ? wrapEl.getBoundingClientRect() : null;
      if (wr) {
        _shelf.style.left = (wr.left + 31) + "px";
        _shelf.style.top = (wr.top + 6) + "px";
        _shelf.style.width = Math.max(140, wr.width - 31 - 16) + "px";
      }
    }
    tiles.forEach(function (t) { _shelf.appendChild(t); });
  }

  function shelfIdle() {
    if (!_shelf) return;
    for (var k in _uploads) { if (!_uploads[k].done) return; }
    try { if (_shelf.parentNode) _shelf.parentNode.removeChild(_shelf); } catch (e) { }
    _shelf = null;
  }

  function upFinish(token, ok, why) {
    var st = _uploads[token];
    if (!st || st.done) return;
    st.done = true;
    st.tile.classList.add(ok ? "ok" : "bad");
    var pc = st.tile.querySelector(".pc");
    if (pc) pc.textContent = ok ? "✓" : "!";
    if (!ok) {
      st.tile.title = why || "复制失败";
      showHint("复制失败：" + (why || st.name), 3600);
      // 失败的块点一下就撤掉，不留在列表里碍眼
      st.tile.addEventListener("click", function () {
        try { if (st.tile.parentNode) st.tile.parentNode.removeChild(st.tile); } catch (e) { }
        delete _uploads[token];
        shelfIdle();
      });
    }
    shelfIdle();
  }

  function startUpload(file, targetId, tile) {
    var token = "u" + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
    _uploads[token] = { tile: tile, name: file.name, size: file.size, done: false };
    var name = String(file.name || "file").replace(/[|\r\n]/g, "_");
    send("upbegin|" + token + "|" + targetId + "|" + name + "|" + file.size);

    var off = 0;
    function step() {
      var st = _uploads[token];
      if (!st || st.done) return;
      if (off >= file.size) {                 // 空文件也要走一趟，否则永远不落盘
        send("upend|" + token);
        upProgress(token, 1);
        var pc = st.tile.querySelector(".pc");
        if (pc) pc.textContent = "…";
        return;
      }
      var end = Math.min(off + UP_CHUNK, file.size);
      var fr = new FileReader();
      fr.onload = function () {
        var s = String(fr.result || "");
        var k = s.indexOf(",");
        send("upchunk|" + token + "|" + (k >= 0 ? s.slice(k + 1) : ""));
        off = end;
        upProgress(token, off / Math.max(1, file.size));
        setTimeout(step, 0);
      };
      fr.onerror = function () {
        try { send("upcancel|" + token); } catch (e2) { }
        upFinish(token, false, "文件读取失败");
      };
      try { fr.readAsDataURL(file.slice(off, end)); }
      catch (err) {
        try { send("upcancel|" + token); } catch (e3) { }
        upFinish(token, false, "文件读取失败");
      }
    }
    step();
  }

  document.addEventListener("drop", function (e) {
    e.preventDefault();
    // 告诉来源这是「复制」，源端就不会把原文删掉
    try { e.dataTransfer.dropEffect = "copy"; } catch (eC) { }
    window.__ourDragActive = false;
    _dropDoneAt = Date.now();
    var dt = e.dataTransfer;
    if (!dt) return;
    // 自己拖出去的附件又被拖回来：忽略，否则会复制出一份新笔记
    try {
      if (dt.types && Array.prototype.indexOf.call(dt.types, "TempNotesInternal") >= 0) return;
    } catch (e9) { }
    // Chromium 经常不透出自定义格式，用发起拖拽前打的时间戳兜底
    try {
      if (window.__internalDragAt && (Date.now() - window.__internalDragAt) < 2500) return;
    } catch (e10) { }
    if (dragIsTextInternal()) return;

    // 落点决定去哪：卡片上 → 加到那条笔记；输入框/空白 → 新建一条。
    // 以预览空槽所在的卡片为准（松手瞬间指针可能正好压在占位块上）。
    var target = "";
    try {
      if (_extNoteId) target = _extNoteId;
      else {
        var el = document.elementFromPoint(e.clientX, e.clientY);
        var card = el && el.closest ? el.closest(".note") : null;
        if (card) target = card.getAttribute("data-id") || "";
      }
    } catch (err) { }

    var files = [];
    try { files = Array.prototype.slice.call(dt.files || []); } catch (e14) { }

    /* ---------- 拖进来的是文字 / 链接（没有文件） ---------- */
    if (!files.length) {
      var text = extractDropText(dt);
      cleanupDropUi();
      if (!text) {
        try { send("dropempty|" + (dt.types ? Array.prototype.slice.call(dt.types).join(",") : "none")); } catch (e15) { }
        return;
      }
      if (target) {
        // 落在某条待办上：追加到那条的正文后面（换行分隔）
        var tn = find(target);
        if (tn) {
          var nt = (tn.text ? tn.text.replace(/\s+$/, "") + "\n" : "") + text;
          tn.text = nt;
          _dropFlashId = target;
          send("update|" + target + "|" + encodeURIComponent(nt));
          render();                       // 先本地落一遍，C# 回推再校正
        }
      } else {
        // 落在输入框 / 空白：新建一条待办，走和按 Enter 同一套动画
        var cbox = document.querySelector(".composer-box");
        var cr = cbox ? cbox.getBoundingClientRect() : null;
        _addPending = cr ? { top: cr.top, left: cr.left, w: cr.width } : true;
        _dropNewPending = true;           // 新卡回来时滚到顶部 + 闪一下
        send("add|" + encodeURIComponent(text));
      }
      return;
    }
    var box = _extSlot && _extSlot.parentNode ? _extSlot.parentNode : null;
    if (!box && _extNote) box = attBoxOf(_extNote, true);
    var wasEmpty = box ? !box.querySelector(".att") : false;

    // 输入框里松手：先把框内占位收掉（瞬时），悬浮进度卡的位置才不会偏 66px
    hideComposerDrop(true);

    var tiles = files.map(function (f) { return upTile(f.name); });
    if (box) {
      var ref = _extSlot;
      for (var i = 0; i < tiles.length; i++) box.insertBefore(tiles[i], ref);
      if (wasEmpty) attBoxGrow(box);          // 空附件区从 0 高度长出来
    } else {
      shelfAdd(tiles);                        // 新建：先挂在列表顶部的悬浮架上
    }

    // 虚线占位撤掉（上传卡已经占住了这个位置，容器不会塌）
    cleanupDropUi();

    if (!target) _dropNewPending = true;      // 新卡片 → 回到顶部 + 高亮
    else _dropFlashId = target;

    // 先说这一批有几个：C# 收齐之前不重渲染，否则同批没传完的卡片会被冲掉
    try { send("upbatch|" + files.length); } catch (e11) { }
    for (var j = 0; j < files.length; j++) startUpload(files[j], target, tiles[j]);
  });
})();




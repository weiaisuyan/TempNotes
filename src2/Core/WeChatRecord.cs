using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TempNotes
{
    /// <summary>
    /// 微信「多选消息 → 拖出」生成的聊天记录包解析器。
    ///
    /// 微信导出的文件形如 聊天记录_20261118_092742.zip，里面只有一个 聊天记录.txt，
    /// 正文按空行分块，每块三行起步：
    ///     ·发送人
    ///     2026年11月7日 14:36
    ///     [链接] 标题 https://...
    /// 或普通文字 / 裸链接。一个块对应一条待办。
    /// </summary>
    internal static class WeChatRecord
    {
        public sealed class Message
        {
            public string Sender;
            public string Date;
            public string RawText;
            public string DisplayText;
            /// <summary>消息里带的链接（没有就是空）。后台线程拿它做标题解析与短链。</summary>
            public string Url;
            /// <summary>true = 只有标题没有链接，需要按标题去搜。</summary>
            public bool TitleOnly;
            /// <summary>从原文里拆出来的标题（可能为空）。</summary>
            public string TitleSeed;

            /// <summary>这条消息引用的媒体文件名（[图片]/[视频]/[文件] 后面那串）。</summary>
            public string MediaName;
            /// <summary>image | video | file</summary>
            public string MediaKind;
            /// <summary>从 zip 里解出来的临时路径；zip 里没有对应实体时为空。</summary>
            public string MediaPath;
        }

        // 只认微信自动生成的名字，避免把用户自己拖进来的普通 zip 误当聊天记录。
        private static readonly Regex NameRx = new Regex(
            @"^聊天记录[_-]?\d{8}[_-]\d{6}\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DateRx = new Regex(
            @"^\d{4}年\d{1,2}月\d{1,2}日\s+\d{1,2}:\d{2}(?::\d{2})?$",
            RegexOptions.Compiled);

        // (?m) ^ 按行匹配；(?s) . 跨行；块结束 = 空行后接下一个 · 发送人，或文末。
        private static readonly Regex BlockRx = new Regex(
            @"(?ms)^[·•]?(?<sender>[^\n]+)\n(?<date>\d{4}年\d{1,2}月\d{1,2}日\s+\d{1,2}:\d{2}(?::\d{2})?)\n(?<body>.*?)(?=\n\n[·•]|\z)",
            RegexOptions.Compiled);

        private static readonly Regex UrlRx = new Regex(
            @"https?://[^\s<>""']+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const int MaxTextBytes = 20 * 1024 * 1024;
        private const int MaxMessages = 500;

        public static bool IsChatRecordName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            return NameRx.IsMatch(fileName);
        }

        /// <summary>
        /// 尝试解析一个微信聊天记录 zip。解析失败返回 false，调用方按普通附件兜底。
        ///
        /// mediaDir 非空时，把 zip 里「聊天记录内的图片、视频和文件/」下的实体解到这里，
        /// 并把路径挂到对应的 [图片]/[视频]/[文件] 消息上 —— 否则用户只看到文件名，
        /// 图片内容、视频、文件本体全丢了（这是 2026-11-18 用户报的问题）。
        /// </summary>
        public static bool TryParse(string zipPath, string mediaDir, out List<Message> messages)
        {
            messages = null;
            try
            {
                if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return false;

                using (var fs = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry txt = null;
                    foreach (var e in zip.Entries)
                    {
                        if (string.Equals(e.Name, "聊天记录.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            txt = e;
                            break;
                        }
                    }
                    if (txt == null)
                    {
                        foreach (var e in zip.Entries)
                        {
                            if (e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                            {
                                txt = e;
                                break;
                            }
                        }
                    }
                    if (txt == null || txt.Length > MaxTextBytes) return false;

                    string text;
                    using (var s = txt.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        text = Decode(ms.ToArray());
                    }

                    messages = ParseText(text);
                    if (messages == null || messages.Count == 0) return false;

                    if (!string.IsNullOrEmpty(mediaDir))
                    {
                        try { AttachMedia(zip, mediaDir, messages); }
                        catch { }
                    }
                    return true;
                }
            }
            catch
            {
                messages = null;
                return false;
            }
        }

        /// <summary>
        /// 把 zip 里的媒体实体解到 mediaDir，按文件名对到消息上。
        /// 微信的目录名是「聊天记录内的图片、视频和文件」，但不同版本略有差别，
        /// 所以这里不按目录名匹配，直接按「文件名」对 —— 消息里写的就是文件名。
        /// </summary>
        private static void AttachMedia(ZipArchive zip, string mediaDir, List<Message> messages)
        {
            // 只对真正带 [图片]/[视频]/[文件] 的消息建索引
            var byName = new Dictionary<string, Message>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in messages)
            {
                if (string.IsNullOrEmpty(m.MediaName)) continue;
                if (!byName.ContainsKey(m.MediaName)) byName[m.MediaName] = m;
            }
            if (byName.Count == 0) return;

            Directory.CreateDirectory(mediaDir);

            foreach (var e in zip.Entries)
            {
                if (e.Length <= 0) continue;
                string name = e.Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;

                Message target;
                if (!byName.TryGetValue(name, out target)) continue;
                if (!string.IsNullOrEmpty(target.MediaPath)) continue;   // 已经解过（重名只取第一个）

                try
                {
                    string safe = Path.GetFileName(name);
                    string dest = Path.Combine(mediaDir, safe);
                    int n = 1;
                    while (File.Exists(dest))
                    {
                        dest = Path.Combine(mediaDir,
                            Path.GetFileNameWithoutExtension(safe) + " (" + (++n) + ")" + Path.GetExtension(safe));
                    }
                    using (var src = e.Open())
                    using (var dst = File.Create(dest))
                        src.CopyTo(dst);
                    target.MediaPath = dest;
                }
                catch { }
            }
        }

        private static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            try
            {
                // 微信实测是 UTF-8 无 BOM。strict 解码，碰到非法字节再退 GBK。
                var utf8 = new UTF8Encoding(false, true);
                string s = utf8.GetString(bytes);
                return s.TrimStart('\uFEFF');
            }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(bytes); }
                catch { return Encoding.UTF8.GetString(bytes); }
            }
        }

        private static List<Message> ParseText(string text)
        {
            var list = new List<Message>();
            if (string.IsNullOrEmpty(text)) return list;

            text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');

            foreach (Match m in BlockRx.Matches(text))
            {
                AddMessage(list, m.Groups["sender"].Value, m.Groups["date"].Value, m.Groups["body"].Value);
                if (list.Count >= MaxMessages) break;
            }

            // 兜底：某些版本可能不是严格的「空行 + ·」结构，按空行切开逐块看。
            if (list.Count == 0)
            {
                string[] parts = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    string[] lines = part.Split('\n');
                    if (lines.Length < 2) continue;
                    string date = (lines[1] ?? "").Trim();
                    if (!DateRx.IsMatch(date)) continue;

                    var body = new StringBuilder();
                    for (int i = 2; i < lines.Length; i++)
                    {
                        if (body.Length > 0) body.Append('\n');
                        body.Append(lines[i]);
                    }
                    AddMessage(list, lines[0], date, body.ToString());
                    if (list.Count >= MaxMessages) break;
                }
            }

            return list;
        }

        private static void AddMessage(List<Message> list, string sender, string date, string body)
        {
            body = (body ?? "").Trim();
            if (body.Length == 0) return;
            if (body.Length > 20000) body = body.Substring(0, 20000) + "…";

            var msg = new Message
            {
                Sender = (sender ?? "").Trim(),
                Date = (date ?? "").Trim(),
                RawText = body
            };
            Classify(msg);

            // 先用「纯本地」信息排好版：URL 只做参数清理（不联网、毫秒级），
            // 卡片一落地就已经是「标题 + 短链接」的紧凑样子，
            // 后台解析完再替换，高度不会再大幅跳动。
            msg.DisplayText = LocalDisplay(msg);
            list.Add(msg);
        }

        /// <summary>不联网的展示文本：能排多少排多少，让首屏就是紧凑卡片。</summary>
        private static string LocalDisplay(Message msg)
        {
            // [图片]/[视频]/[文件]：实体已经挂成附件，卡片上有缩略图，
            // 正文只留文件名就够，别再显示「[图片]」这种占位标记。
            if (!string.IsNullOrEmpty(msg.MediaName))
                return msg.MediaName;

            string url = msg.Url;
            if (!string.IsNullOrEmpty(url))
            {
                string clean = LinkResolver.CleanUrl(url);
                return BuildCardDisplay(msg.TitleSeed, clean);
            }
            // 只有标题（B站/小程序卡片）：首屏只能先显示标题，链接等后台解析。
            if (msg.TitleOnly && !string.IsNullOrEmpty(msg.TitleSeed))
                return "**" + LinkResolver.CleanTitle(msg.TitleSeed).Replace("**", "＊") + "**";
            return msg.RawText;
        }

        /// <summary>
        /// 判断这条消息是「带链接」「只有标题」还是「纯文字」，并拆出标题种子。
        /// 真正的标题解析/短链在后台线程做（见 Bridge.EnrichWeChatMessages），
        /// 拖入时不联网，界面不会因为网络卡住。
        /// </summary>
        private static void Classify(Message msg)
        {
            string body = msg.RawText ?? "";
            string url = FirstUrl(body);

            // [图片] xxx.jpg / [视频] xxx.mp4 / [文件] xxx.zip —— 这三类要从 zip 里把实体解出来。
            Match media = Regex.Match(body, @"^\[(图片|视频|文件)\]\s*(.+?)\s*$", RegexOptions.Singleline);
            if (media.Success)
            {
                msg.MediaKind = media.Groups[1].Value == "图片" ? "image"
                              : media.Groups[1].Value == "视频" ? "video" : "file";
                msg.MediaName = Path.GetFileName(media.Groups[2].Value.Replace('\\', '/'));
                return;
            }

            if (body.StartsWith("[链接]", StringComparison.Ordinal))
            {
                string rest = body.Substring(4).Trim();
                if (!string.IsNullOrEmpty(url))
                {
                    int at = rest.IndexOf(url, StringComparison.Ordinal);
                    msg.Url = url;
                    msg.TitleSeed = at > 0 ? OneLine(rest.Substring(0, at)) : "";
                }
                else
                {
                    msg.TitleOnly = true;
                    msg.TitleSeed = OneLine(rest);
                }
                return;
            }

            if (!string.IsNullOrEmpty(url))
            {
                int at = body.IndexOf(url, StringComparison.Ordinal);
                string before = at > 0 ? body.Substring(0, at).Trim() : "";
                msg.Url = url;
                msg.TitleSeed = CleanShareTitle(before, url);
                return;
            }

            // 纯文字消息：没有链接，展示就用原文。
        }

        /// <summary>
        /// 卡片正文：标题加粗一行，第二行直接放可点击的链接（用户要的就是这个，
        /// 不要“打开公众号文章/打开微博”这种没信息量的文案）。
        /// 由后台线程在解析完标题后调用。
        /// </summary>
        public static string BuildCardDisplay(string title, string url)
        {
            title = LinkResolver.CleanTitle(title);
            if (title.Length > 140) title = title.Substring(0, 140) + "…";

            if (string.IsNullOrEmpty(url)) return title;

            // 链接本身就当锚文本：显示的是真实地址，点一下直接跳转。
            // 用 Markdown 链接而不是 <url> 自动链接，因为 URL 里的 & 会打断后者的正则。
            string link = "[" + url + "](" + url + ")";
            if (title.Length == 0) return link;
            return "**" + title.Replace("**", "＊") + "**\n" + link;
        }

        private static string CleanShareTitle(string before, string url)
        {
            before = (before ?? "").Trim();
            if (before.Length == 0) return "";

            // 抖音/快手/小红书分享：3.30 复制打开抖音，看看【作者的作品】标题……
            Match m = Regex.Match(before,
                @"(?:复制打开|打开)(?:抖音|快手|小红书|B站|哔哩哔哩)[，,]\s*看看(.+)$",
                RegexOptions.Singleline);
            if (m.Success) return OneLine(m.Groups[1].Value);

            // 有些分享文本前面还有“3.30”之类的时长/序号，去掉。
            return OneLine(Regex.Replace(before, @"^\d+(?:\.\d+)?\s*", ""));
        }

        private static string FirstUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            Match m = UrlRx.Match(s);
            if (!m.Success) return "";
            // 微信里链接后面常跟中文标点；这些不属于 URL。
            return m.Value.TrimEnd('.', ',', ';', '!', '?', '，', '。', '；', '！', '、', '）', ')', ']', '}');
        }

        private static string OneLine(string s)
        {
            return Regex.Replace(s ?? "", @"\s+", " ").Trim();
        }
    }
}

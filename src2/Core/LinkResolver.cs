using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TempNotes
{
    /// <summary>
    /// 链接卡片解析器。
    ///
    /// 目标是「标题尽量真、链接尽量短、点击能跳」：
    ///  - URL 里的跟踪参数（微信 mpshare/sharer_shareinfo、X 的 s/t 等）先清掉；
    ///  - 清完还长就试一次 tinyurl，失败就用清理后的原链；
    ///  - 标题：X 用 oEmbed 拿正文，微博用 m.weibo.cn 接口，B站/YouTube 用公开接口，
    ///    其它站点抓 og:title / title；都不行再退回按标题搜索。
    ///
    /// 所有网络调用都可能失败，失败一律返回空，由调用方回退，绝不抛异常。
    /// </summary>
    internal static class LinkResolver
    {
        public sealed class Enriched
        {
            public string Title;      // 真实标题/正文摘要（失败时为空）
            public string Url;        // 可点击的链接（可能被换成短链）
            public string SearchUrl;  // 只有标题没有 URL 时的兜底搜索链接
        }

        private const string UA_DESKTOP =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        private const string UA_MOBILE =
            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 " +
            "(KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, Enriched> Cache =
            new Dictionary<string, Enriched>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex HtmlTagRx = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex SpaceRx = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex HeadTitleRx = new Regex(
            @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex OgTitleRx = new Regex(
            @"<meta[^>]+(?:property|name)\s*=\s*[""']og:title[""'][^>]+content\s*=\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OgTitleRx2 = new Regex(
            @"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]+(?:property|name)\s*=\s*[""']og:title[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 各平台跟踪参数：清掉它们能显著缩短链接，且不影响打开。
        private static readonly string[] TrackingKeys =
        {
            "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
            "mpshare", "scene", "srcid", "sharer_shareinfo", "sharer_shareinfo_first",
            "sharer_sharetime", "sharer_shareid", "share_source", "share_medium", "share_plat",
            "share_session_id", "share_tag", "timestamp", "unique_k", "spm_id_from",
            "vd_source", "from_spmid", "share_from", "is_story_h5", "s", "t"
        };

        static LinkResolver()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /* ---------------- 链接清理 / 缩短 ---------------- */

        /// <summary>
        /// 去掉跟踪参数。微信公众号只留 __biz/mid/idx/sn（这四个就够定位文章）。
        /// </summary>
        public static string CleanUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            try
            {
                var u = new Uri(url, UriKind.Absolute);
                string host = (u.Host ?? "").ToLowerInvariant();
                var q = ParseQuery(u.Query);

                if (host.EndsWith("mp.weixin.qq.com"))
                {
                    string biz, mid, idx, sn;
                    if (q.TryGetValue("__biz", out biz) && q.TryGetValue("mid", out mid))
                    {
                        q.TryGetValue("idx", out idx);
                        q.TryGetValue("sn", out sn);
                        var sb = new StringBuilder("https://mp.weixin.qq.com/s?__biz=" + biz);
                        sb.Append("&mid=").Append(mid);
                        if (!string.IsNullOrEmpty(idx)) sb.Append("&idx=").Append(idx);
                        if (!string.IsNullOrEmpty(sn)) sb.Append("&sn=").Append(sn);
                        return sb.ToString();
                    }
                    return "https://mp.weixin.qq.com" + u.AbsolutePath;
                }

                foreach (string k in TrackingKeys)
                {
                    // 只删“跟踪类”参数；v / id / mid 这些是内容标识，删了就打不开。
                    if (k == "s" || k == "t")
                    {
                        if (!(host == "x.com" || host.EndsWith("twitter.com"))) continue;
                    }
                    q.Remove(k);
                }

                string path = u.AbsolutePath;
                var rebuilt = new StringBuilder();
                rebuilt.Append(u.Scheme).Append("://").Append(u.Host);
                if (!u.IsDefaultPort) rebuilt.Append(':').Append(u.Port);
                rebuilt.Append(path);
                if (q.Count > 0)
                {
                    bool first = true;
                    foreach (var kv in q)
                    {
                        rebuilt.Append(first ? '?' : '&').Append(kv.Key).Append('=').Append(kv.Value);
                        first = false;
                    }
                }
                return rebuilt.ToString();
            }
            catch { return url; }
        }

        /// <summary>
        /// 太长就试一次 tinyurl（失败返回清理后的原链）。
        /// 只在后台线程调用，避免卡住 UI。
        /// </summary>
        public static string ShortenUrl(string url)
        {
            string clean = CleanUrl(url);
            if (string.IsNullOrEmpty(clean) || clean.Length <= 90) return clean;
            try
            {
                string api = "https://tinyurl.com/api-create.php?url=" + Uri.EscapeDataString(clean);
                string s = HttpGet(api, UA_DESKTOP, 4000).Trim();
                if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) && s.Length < clean.Length)
                    return s;
            }
            catch { }
            return clean;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;
            string q = query.TrimStart('?');
            foreach (string part in q.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq);
                string v = part.Substring(eq + 1);
                if (!d.ContainsKey(k)) d[k] = v;
            }
            return d;
        }

        /* ---------------- 标题解析 ---------------- */

        /// <summary>有 URL：尽量拿到真实标题，并把长链换成短链。</summary>
        public static Enriched EnrichUrl(string url, string fallbackTitle)
        {
            if (string.IsNullOrEmpty(url)) return null;

            lock (CacheLock)
            {
                Enriched hit;
                if (Cache.TryGetValue(url, out hit)) return hit;
            }

            var r = new Enriched { Url = url };
            string host = "";
            try { host = new Uri(url, UriKind.Absolute).Host.ToLowerInvariant(); } catch { }

            try
            {
                if (host == "x.com" || host.EndsWith("twitter.com") || host == "t.co")
                    r.Title = FetchXTweet(url);
                else if (host.EndsWith("weibo.com") || host.EndsWith("weibo.cn"))
                    r.Title = FetchWeibo(url);
                else if (host.EndsWith("youtube.com") || host == "youtu.be")
                    r.Title = FetchYouTubeOembed(url);
                else if (host.EndsWith("bilibili.com") || host == "b23.tv")
                    r.Title = FetchBilibili(url);
                else
                    r.Title = FetchGenericTitle(url);
            }
            catch { }

            if (string.IsNullOrEmpty(r.Title)) r.Title = fallbackTitle ?? "";

            // 短链放最后做：即使它失败，标题也已经拿到了。
            r.Url = ShortenUrl(url);

            // 只缓存成功的结果。网络抖动导致 Title 为空时如果也写进缓存，
            // 这条链接就永远显示不出标题了（实测踩过：首次超时 → 之后一直空白）。
            if (!string.IsNullOrEmpty(r.Title))
            {
                lock (CacheLock) { Cache[url] = r; }
            }
            return r;
        }

        /// <summary>只有标题：先按平台搜索精确匹配，再退 Bing 精确搜索。</summary>
        public static Enriched EnrichTitle(string title)
        {
            string t = CleanTitle(title);
            var r = new Enriched { Title = t };
            if (t.Length == 0) return r;

            string key = "title:" + t;
            lock (CacheLock)
            {
                Enriched hit;
                if (Cache.TryGetValue(key, out hit)) return hit;
            }

            string bili = SearchBilibili(t);
            if (!string.IsNullOrEmpty(bili))
            {
                r.Url = bili;
                lock (CacheLock) { Cache[key] = r; }
                return r;
            }

            string yt = SearchYouTube(t);
            if (!string.IsNullOrEmpty(yt))
            {
                r.Url = yt;
                lock (CacheLock) { Cache[key] = r; }
                return r;
            }

            r.SearchUrl = "https://www.bing.com/search?q=" + Uri.EscapeDataString("\"" + t + "\"");
            lock (CacheLock) { Cache[key] = r; }
            return r;
        }

        private static string FetchXTweet(string url)
        {
            string api = "https://publish.twitter.com/oembed?url=" + Uri.EscapeDataString(url);
            // 实测这条接口经系统代理要 1.1~1.8 秒，2.5 秒超时偶尔会踩线失败，
            // 失败后卡片就只剩一个裸链接。给到 5 秒，并且失败重试一次。
            string json = HttpGet(api, UA_DESKTOP, 5000);
            if (string.IsNullOrEmpty(json)) json = HttpGet(api, UA_DESKTOP, 5000);
            if (string.IsNullOrEmpty(json)) return "";

            string html = JsonField(json, "html");
            string author = JsonField(json, "author_name");
            if (string.IsNullOrEmpty(html)) return "";

            html = Regex.Replace(html, "<br\\s*/?>", " ", RegexOptions.IgnoreCase);
            string text = HtmlToText(html);
            int cut = text.IndexOf('—');
            if (cut > 0) text = text.Substring(0, cut);
            text = OneLine(text);
            if (text.Length > 110) text = text.Substring(0, 110) + "…";
            if (string.IsNullOrEmpty(author)) return text;
            return "@" + author + "：" + text;
        }

        private static string FetchWeibo(string url)
        {
            string id = "";
            try
            {
                var u = new Uri(url, UriKind.Absolute);
                foreach (string seg in u.AbsolutePath.Split('/'))
                {
                    if (seg.Length >= 10 && Regex.IsMatch(seg, @"^\d+$")) id = seg;
                }
            }
            catch { }
            if (string.IsNullOrEmpty(id)) return "";

            string api = "https://m.weibo.cn/statuses/show?id=" + id;
            var req = (HttpWebRequest)WebRequest.Create(api);
            req.UserAgent = UA_MOBILE;
            req.Referer = "https://m.weibo.cn/";
            req.Headers["X-Requested-With"] = "XMLHttpRequest";
            req.Accept = "application/json, text/plain, */*";
            req.Timeout = 5000;
            req.ReadWriteTimeout = 5000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            string json;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var rd = new StreamReader(s, Encoding.UTF8))
                json = rd.ReadToEnd();

            if (string.IsNullOrEmpty(json) || json.IndexOf("\"ok\":1", StringComparison.Ordinal) < 0) return "";

            string text = JsonField(json, "text");
            string user = JsonField(json, "screen_name");
            text = OneLine(HtmlToText(text));
            if (text.Length > 110) text = text.Substring(0, 110) + "…";
            if (string.IsNullOrEmpty(text)) return "";
            return string.IsNullOrEmpty(user) ? text : "@" + user + "：" + text;
        }

        private static string FetchYouTubeOembed(string url)
        {
            string api = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(url);
            string json = HttpGet(api, UA_DESKTOP, 4000);
            if (string.IsNullOrEmpty(json)) return "";
            return CleanTitle(JsonField(json, "title"));
        }

        private static string FetchBilibili(string url)
        {
            string bvid = "";
            Match m = Regex.Match(url, @"(BV[0-9A-Za-z]{10})");
            if (m.Success) bvid = m.Groups[1].Value;
            if (string.IsNullOrEmpty(bvid)) return FetchGenericTitle(url);

            string api = "https://api.bilibili.com/x/web-interface/view?bvid=" + bvid;
            string json = HttpGet(api, UA_DESKTOP, 4000);
            if (string.IsNullOrEmpty(json)) return "";
            return CleanTitle(JsonField(json, "title"));
        }

        private static string FetchGenericTitle(string url)
        {
            string html = HttpGet(url, UA_DESKTOP, 4000);
            if (string.IsNullOrEmpty(html)) return "";

            Match m = OgTitleRx.Match(html);
            if (!m.Success) m = OgTitleRx2.Match(html);
            string t = m.Success ? m.Groups[1].Value : "";
            if (string.IsNullOrEmpty(t))
            {
                m = HeadTitleRx.Match(html);
                if (m.Success) t = m.Groups[1].Value;
            }
            t = CleanTitle(t);
            if (t.Length > 140) t = t.Substring(0, 140) + "…";
            return t;
        }

        private static string SearchBilibili(string title)
        {
            try
            {
                string url = "https://api.bilibili.com/x/web-interface/search/all/v2?keyword=" +
                             Uri.EscapeDataString(title);
                string json = HttpGet(url, "Mozilla/5.0", 4000);
                if (string.IsNullOrEmpty(json)) return "";

                string want = CleanTitle(title);
                MatchCollection items = Regex.Matches(
                    json,
                    @"""bvid"":""(?<bvid>[^""]+)""[\s\S]{0,3000}?""title"":""(?<title>(?:\\.|[^""])*)""",
                    RegexOptions.Singleline);
                foreach (Match m in items)
                {
                    if (!m.Success) continue;
                    string got = CleanTitle(JsonUnescape(m.Groups["title"].Value));
                    if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) continue;
                    string bvid = m.Groups["bvid"].Value;
                    if (!string.IsNullOrEmpty(bvid))
                        return "https://www.bilibili.com/video/" + bvid + "/";
                }
            }
            catch { }
            return "";
        }

        private static string SearchYouTube(string title)
        {
            try
            {
                string url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(title);
                string html = HttpGet(url, "Mozilla/5.0", 5000);
                if (string.IsNullOrEmpty(html)) return "";

                MatchCollection ms = Regex.Matches(
                    html,
                    @"""videoId"":""([^""]+)""[\s\S]{0,1500}?""title"":\{""runs"":\[\{""text"":""([^""]+)""",
                    RegexOptions.Singleline);
                string want = CleanTitle(title);
                foreach (Match m in ms)
                {
                    if (!m.Success) continue;
                    string got = CleanTitle(JsonUnescape(m.Groups[2].Value));
                    if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) continue;
                    return "https://www.youtube.com/watch?v=" + m.Groups[1].Value;
                }
            }
            catch { }
            return "";
        }

        /* ---------------- 小工具 ---------------- */

        public static string CleanTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = HtmlTagRx.Replace(s, "");
            try { s = WebUtility.HtmlDecode(s); } catch { }
            s = SpaceRx.Replace(s, " ").Trim();
            return s;
        }

        private static string HtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = Regex.Replace(html, "<br\\s*/?>", " ", RegexOptions.IgnoreCase);
            s = HtmlTagRx.Replace(s, "");
            try { s = WebUtility.HtmlDecode(s); } catch { }
            return s;
        }

        private static string OneLine(string s)
        {
            return SpaceRx.Replace(s ?? "", " ").Trim();
        }

        /// <summary>从 JSON 文本里取一个字符串字段（不依赖序列化器，对多变的接口更宽容）。</summary>
        private static string JsonField(string json, string name)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(name)) return "";
            Match m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return "";
            return JsonUnescape(m.Groups[1].Value);
        }

        private static string JsonUnescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                var sb = new StringBuilder(s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                    char n = s[++i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber, null, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                    break;
                                }
                            }
                            sb.Append('u');
                            break;
                        default: sb.Append(n); break;
                    }
                }
                return sb.ToString();
            }
            catch { return s; }
        }

        private static string HttpGet(string url, string ua, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = string.IsNullOrEmpty(ua) ? UA_DESKTOP : ua;
            req.Accept = "text/html,application/json;q=0.9,*/*;q=0.8";
            req.Headers["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}

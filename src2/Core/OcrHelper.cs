using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TempNotes
{
    /// <summary>
    /// 截图识字。
    ///
    /// 主引擎：百度智能云「通用文字识别」在线接口（免费额度 1000 次/月，够用）。
    ///   优点：不用装任何依赖、中文识别质量明显高于系统自带、对压缩模糊的小字也稳。
    ///   代价：必须联网，且要用户自己去百度智能云建个应用拿 API Key / Secret Key。
    ///
    /// 兜底：Windows.Media.Ocr（系统自带，离线）。没填 key、断网、或接口报错时自动退回，
    ///   保证功能永远可用，不会点一下就「什么也没发生」。
    /// </summary>
    internal static class OcrHelper
    {
        public sealed class Line
        {
            public int X, Y, W, H;
            public string Text;
            public double Score = 1.0;
        }

        /// <summary>可选的日志回调（Bridge 挂上去写 bridge.log），为空就静默。</summary>
        public static Action<string> Log;
        private static void LogLine(string s) { try { if (Log != null) Log(s); } catch { } }

        // ================= 百度 OCR =================

        private const string BaiduTokenUrl = "https://aip.baidubce.com/oauth/2.0/token";
        private const string BaiduOcrAccurate = "https://aip.baidubce.com/rest/2.0/ocr/v1/accurate_basic";
        private const string BaiduOcrBasic = "https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic";

        private static string _token;
        private static long _tokenExpireAt;      // 毫秒时间戳
        private static readonly object TokenLock = new object();

        // 免费版百度 OCR 的 QPS 很低（标准版 2 QPS、高精度版 1 QPS），
        // 连发两个请求（比如先试高精度、失败立刻重试标准版）就会撞
        // "Open api qps request limit reached"（error_code = 18）。
        // 这里做两件事：① 全局串行 + 最小间隔；② 撞到 18 自动退避重试。
        private static readonly object CallLock = new object();
        private static long _lastCallAt;

        /// <summary>最近一次失败原因，给页面提示用。</summary>
        public static string LastError;

        private static void ThrottleBaidu()
        {
            lock (CallLock)
            {
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                long wait = 1200 - (now - _lastCallAt);
                if (wait > 0) System.Threading.Thread.Sleep((int)wait);
                _lastCallAt = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            }
        }

        /// <summary>调百度 OCR 接口：节流 + 撞到 QPS 限流时退避重试。</summary>
        private static string CallBaiduOcr(string url, string body, string token)
        {
            string full = url + "?access_token=" + Uri.EscapeDataString(token);
            string resp = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ThrottleBaidu();
                resp = HttpPostForm(full, body, 25000);
                if (string.IsNullOrEmpty(resp)) return resp;
                if (!Regex.IsMatch(resp, "\"error_code\"\\s*:\\s*18\\b")) return resp;
                // 18 = QPS 超限：等一会儿再来
                int wait = 1600 + attempt * 1500;
                LogLine("百度 QPS 限流，等 " + wait + "ms 重试（第 " + (attempt + 1) + " 次）");
                System.Threading.Thread.Sleep(wait);
            }
            return resp;
        }

        private static string GetBaiduToken(string ak, string sk)
        {
            lock (TokenLock)
            {
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                if (!string.IsNullOrEmpty(_token) && now < _tokenExpireAt) return _token;

                string url = BaiduTokenUrl + "?grant_type=client_credentials" +
                             "&client_id=" + Uri.EscapeDataString(ak) +
                             "&client_secret=" + Uri.EscapeDataString(sk);
                string json = HttpGet(url, 15000);
                if (string.IsNullOrEmpty(json)) { LastError = "连不上百度服务器"; return null; }

                string tok = JsonField(json, "access_token");
                string err = JsonField(json, "error_description");
                if (string.IsNullOrEmpty(tok))
                {
                    LastError = string.IsNullOrEmpty(err) ? ("拿不到 access_token：" + Trim(json, 160)) : err;
                    return null;
                }
                _token = tok;
                int exp = 2592000;
                string expStr = JsonField(json, "expires_in");
                if (!string.IsNullOrEmpty(expStr)) int.TryParse(expStr, out exp);
                // 提前 5 分钟过期，避免临界点失败
                _tokenExpireAt = now + (long)Math.Max(60, exp - 300) * 1000L;
                return _token;
            }
        }

        /// <summary>把图缩到合适尺寸再转 JPEG，既压体积也提高百度那边的识别率。</summary>
        private static string PrepareImageForBaidu(string srcPath)
        {
            string tmp = Path.Combine(Path.GetTempPath(),
                "TempNotes-ocr-" + Guid.NewGuid().ToString("N") + ".jpg");
            try
            {
                using (var src = Image.FromFile(srcPath))
                {
                    int maxSide = 1600;
                    int w = src.Width, h = src.Height;
                    if (w > maxSide || h > maxSide)
                    {
                        double k = Math.Min((double)maxSide / w, (double)maxSide / h);
                        w = Math.Max(1, (int)Math.Round(w * k));
                        h = Math.Max(1, (int)Math.Round(h * k));
                    }
                    using (var dst = new Bitmap(w, h))
                    {
                        using (var g = Graphics.FromImage(dst))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        var enc = GetJpegEncoder();
                        var ps = new EncoderParameters(1);
                        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                        dst.Save(tmp, enc, ps);
                    }
                }
                return tmp;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        private static List<Line> RecognizeBaidu(string imagePath, string ak, string sk)
        {
            var list = new List<Line>();
            string token = GetBaiduToken(ak, sk);
            if (string.IsNullOrEmpty(token)) return list;

            string prepared = PrepareImageForBaidu(imagePath);
            string usePath = prepared ?? imagePath;
            try
            {
                byte[] bytes = File.ReadAllBytes(usePath);
                if (bytes.Length > 3 * 1024 * 1024)
                {
                    // 还是太大：Baidu 要求 base64 后 < 4MB
                    LastError = "图片太大";
                    return list;
                }
                string b64 = Convert.ToBase64String(bytes);

                string body = "image=" + FormEncode(b64) +
                              "&detect_direction=true&paragraph=false&probability=false";

                // 默认用「标准版」：新建的百度应用一般只默认开通这个，1000 次/月免费。
                // 高精度版很多账号要单独申请，先撞它只会白耗一次 QPS。
                string url = _baiduUseAccurate ? BaiduOcrAccurate : BaiduOcrBasic;
                string resp = CallBaiduOcr(url, body, token);

                if (!_baiduUseAccurate && IsBaiduNoPerm(resp))
                {
                    // 标准版都没权限，试试高精度版（有些账号反过来）
                    resp = CallBaiduOcr(BaiduOcrAccurate, body, token);
                    if (!IsBaiduNoPerm(resp)) _baiduUseAccurate = true;
                }

                if (string.IsNullOrEmpty(resp)) { LastError = "百度接口无响应"; return list; }

                string errMsg = JsonField(resp, "error_msg");
                if (!string.IsNullOrEmpty(errMsg))
                {
                    LastError = DescribeBaiduError(resp, errMsg);
                    LogLine("Baidu OCR error: " + Trim(resp, 300));
                    return list;
                }

                // words_result: [{"words":"..."}]
                foreach (Match m in Regex.Matches(resp, "\"words\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\""))
                {
                    string t = JsonUnescape(m.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(t)) list.Add(new Line { Text = t });
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogLine("Baidu OCR 失败: " + ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(prepared))
                {
                    try { if (File.Exists(prepared)) File.Delete(prepared); } catch { }
                }
            }
            return list;
        }

        private static bool _baiduUseAccurate;

        /// <summary>把百度返回的错误码翻成人话。</summary>
        private static string DescribeBaiduError(string resp, string errMsg)
        {
            int code = 0;
            Match m = Regex.Match(resp ?? "", "\"error_code\"\\s*:\\s*(\\d+)");
            if (m.Success) int.TryParse(m.Groups[1].Value, out code);

            switch (code)
            {
                case 18:
                    return "百度接口限流（免费版每秒只能 1 次），已自动重试仍失败，请等 2 秒再试";
                case 17:
                    return "百度这个月免费额度用完了（1000 次/月）";
                case 19:
                    return "百度总配额用完了";
                case 6:
                    return "这个百度应用没开通「通用文字识别」权限，请到控制台开通";
                case 110:
                    return "百度 access_token 失效，请重试";
                case 111:
                    return "百度 access_token 过期，请重试";
                case 100:
                    return "百度参数错误（图片可能损坏）";
                default:
                    return "百度：" + errMsg;
            }
        }

        private static bool IsBaiduNoPerm(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return false;
            // 6 = 无权限（这个应用没开通该接口）
            return Regex.IsMatch(resp, "\"error_code\"\\s*:\\s*6\\b");
        }

        // ================= OCR.space（免注册，默认 helloworld 演示 key） =================
        // 优点：不用注册就能用，识别质量对中文截图很好（引擎 2）。
        // 缺点：demo key 有频率限制；自己去 https://ocr.space/ocrapi/freekey 领一个免费 key
        //       可以到 25000 次/月，填进设置里即可。

        private const string OcrSpaceUrl = "https://api.ocr.space/parse/image";

        private static List<Line> RecognizeOcrSpace(string imagePath, string apiKey)
        {
            var list = new List<Line>();
            if (string.IsNullOrEmpty(apiKey)) apiKey = "helloworld";

            string prepared = PrepareImageForBaidu(imagePath);   // 同一套缩图/压 JPEG 逻辑
            string usePath = prepared ?? imagePath;
            try
            {
                byte[] bytes = File.ReadAllBytes(usePath);
                string dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
                string body = "base64Image=" + FormEncode(dataUri) +
                              "&language=chs&isOverlayRequired=false&OCREngine=2&scale=true";
                string resp = HttpPostFormWithHeader(OcrSpaceUrl, body, "apikey", apiKey, 30000);
                if (string.IsNullOrEmpty(resp)) { LastError = "OCR.space 无响应"; return list; }

                string err = JsonField(resp, "ErrorMessage");
                if (!string.IsNullOrEmpty(err))
                {
                    LastError = "OCR.space：" + err;
                    LogLine("OCR.space error: " + Trim(resp, 300));
                    return list;
                }

                // ParsedText 是整段文本，按行拆
                string txt = JsonField(resp, "ParsedText");
                if (string.IsNullOrEmpty(txt))
                {
                    LastError = "OCR.space 没返回文字";
                    return list;
                }
                foreach (string raw in txt.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string t = raw.Trim();
                    if (t.Length == 0) continue;
                    // OCR.space 的 helloworld 演示 key 会往正文里塞一行广告提示，别让它变成待办
                    if (t.StartsWith("Note:", StringComparison.OrdinalIgnoreCase) &&
                        t.IndexOf("ocr.space", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (t.IndexOf("ocr.space/ocrapi/freekey", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    list.Add(new Line { Text = t });
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogLine("OCR.space 失败: " + ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(prepared))
                {
                    try { if (File.Exists(prepared)) File.Delete(prepared); } catch { }
                }
            }
            return list;
        }

        private static string HttpPostFormWithHeader(string url, string form, string headerName,
                                                     string headerValue, int timeoutMs)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(form);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.ContentLength = data.Length;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "Mozilla/5.0";
                req.Headers[headerName] = headerValue;
                using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var rd = new StreamReader(s, Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch (WebException wex)
            {
                string body = ReadErrorBody(wex);
                if (!string.IsNullOrEmpty(body)) return body;
                LastError = wex.Message;
                return null;
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        /// <summary>设置页「测试」按钮：只验 key 能不能拿到 token，不消耗 OCR 次数。</summary>
        public static string TestBaidu(string ak, string sk)
        {
            if (string.IsNullOrEmpty(ak) || string.IsNullOrEmpty(sk)) return "请先填 API Key 和 Secret Key";
            LastError = null;
            string tok = GetBaiduToken(ak, sk);
            if (string.IsNullOrEmpty(tok)) return "失败：" + (LastError ?? "未知原因");
            return "连接成功，可以用了";
        }

        /// <summary>设置页「测试」按钮（OCR.space）：拿一张 8x8 的图跑一次，验证 key 能不能用。</summary>
        public static string TestOcrSpace(string spaceKey)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                    "TempNotes-ocrtest-" + Guid.NewGuid().ToString("N") + ".png");
                using (var bmp = new Bitmap(60, 24))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    using (var f = new Font("Arial", 10))
                        g.DrawString("TEST", f, Brushes.Black, 2, 4);
                    bmp.Save(tmp, ImageFormat.Png);
                }
                try
                {
                    var lines = RecognizeOcrSpace(tmp, spaceKey);
                    if (lines.Count > 0) return "连接成功，可以用了";
                    return "失败：" + (LastError ?? "没返回结果");
                }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }
            catch (Exception ex) { return "失败：" + ex.Message; }
        }

        // ================= Windows OCR（离线兜底） =================

        private static readonly object ScriptLock = new object();
        private static string _psScriptPath;

        private static readonly string[] PsLines = new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "Add-Type -AssemblyName System.Runtime.WindowsRuntime",
            "$asTaskOp = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' }) | Select-Object -First 1",
            "function AwaitOp($op, $t) {",
            "  $task = $asTaskOp.MakeGenericMethod($t).Invoke($null, @($op))",
            "  $task.Wait(-1) | Out-Null",
            "  $task.Result",
            "}",
            "[Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime] | Out-Null",
            "[Windows.Storage.Streams.DataWriter,Windows.Storage.Streams,ContentType=WindowsRuntime] | Out-Null",
            "[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime] | Out-Null",
            "[Windows.Media.Ocr.OcrEngine,Windows.Media.Ocr,ContentType=WindowsRuntime] | Out-Null",
            "[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime] | Out-Null",
            "$bytes = [System.IO.File]::ReadAllBytes($args[0])",
            "$ras = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream",
            "$dw = New-Object Windows.Storage.Streams.DataWriter($ras)",
            "$dw.WriteBytes($bytes)",
            "[void](AwaitOp ($dw.StoreAsync()) ([uint32]))",
            "[void](AwaitOp ($dw.FlushAsync()) ([bool]))",
            "$dw.DetachStream() | Out-Null",
            "$ras.Seek(0)",
            "$decoder = AwaitOp ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($ras)) ([Windows.Graphics.Imaging.BitmapDecoder])",
            "$bitmap = AwaitOp ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])",
            "$engine = $null",
            "try { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language 'zh-Hans-CN')) } catch { }",
            "if (-not $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }",
            "if (-not $engine) { [System.IO.File]::WriteAllText($args[1], '', (New-Object System.Text.UTF8Encoding $false)); exit 2 }",
            "$result = AwaitOp ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])",
            "$sb = New-Object System.Text.StringBuilder",
            "foreach ($line in $result.Lines) {",
            "  if ($line.Words.Count -eq 0) { continue }",
            "  [void]$sb.AppendLine('0' + \"`t\" + '0' + \"`t\" + '0' + \"`t\" + '0' + \"`t\" + $line.Text)",
            "}",
            "[System.IO.File]::WriteAllText($args[1], $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))",
            "Write-Output ('OK ' + $result.Lines.Count)"
        };

        private static string ScriptDir(string sub)
        {
            var dirs = new List<string>();
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(exeDir)) dirs.Add(Path.Combine(exeDir, sub));
            }
            catch { }
            dirs.Add(Path.Combine(Path.GetTempPath(), "TempNotes-" + sub));

            Exception last = null;
            foreach (var dir in dirs)
            {
                try { Directory.CreateDirectory(dir); return dir; }
                catch (Exception ex) { last = ex; }
            }
            throw new IOException("OCR 目录无法创建", last);
        }

        private static string EnsurePsScript()
        {
            lock (ScriptLock)
            {
                if (!string.IsNullOrEmpty(_psScriptPath) && File.Exists(_psScriptPath)) return _psScriptPath;
                string dir = ScriptDir("ocr");
                string p = Path.Combine(dir, "win_ocr.ps1");
                File.WriteAllText(p, string.Join("\r\n", PsLines) + "\r\n", new UTF8Encoding(false));
                _psScriptPath = p;
                return p;
            }
        }

        private static string PsExe
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                        @"WindowsPowerShell\v1.0\powershell.exe");
                return File.Exists(p) ? p : "powershell.exe";
            }
        }

        private static List<Line> RecognizeWindows(string imagePath)
        {
            var list = new List<Line>();
            string script = EnsurePsScript();
            string outFile = Path.Combine(Path.GetDirectoryName(script),
                "out-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = PsExe,
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script +
                                "\" \"" + imagePath + "\" \"" + outFile + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); } catch { }
                        return list;
                    }
                    if (!string.IsNullOrEmpty(se)) LogLine("Windows OCR stderr: " + se.Trim());
                    else if (!string.IsNullOrEmpty(so)) LogLine("Windows OCR " + so.Trim());
                }

                if (!File.Exists(outFile)) return list;
                foreach (string raw in File.ReadAllLines(outFile, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string[] parts = raw.Split(new[] { '\t' }, 5);
                    if (parts.Length < 5) continue;
                    list.Add(new Line { Text = parts[4] });
                }
            }
            catch (Exception ex) { LogLine("Windows OCR 失败: " + ex.Message); }
            finally { try { if (File.Exists(outFile)) File.Delete(outFile); } catch { } }
            return list;
        }

        // ================= 公共入口 =================

        /// <summary>识别一张图，返回按阅读顺序排好的文本行（失败返回空表，绝不抛）。</summary>
        public static List<Line> Recognize(string imagePath, string engine, string ak, string sk, string spaceKey)
        {
            var list = new List<Line>();
            LastError = null;
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return list;

            if (string.IsNullOrEmpty(engine)) engine = "space";

            if (engine == "baidu")
            {
                if (string.IsNullOrEmpty(ak) || string.IsNullOrEmpty(sk))
                {
                    LastError = "还没填百度 API Key / Secret Key";
                }
                else
                {
                    try
                    {
                        list = RecognizeBaidu(imagePath, ak, sk);
                        if (list.Count > 0) return list;
                        LogLine("百度 OCR 没结果（" + (LastError ?? "空") + "）");
                    }
                    catch (Exception ex) { LogLine("百度 OCR 异常: " + ex.Message); }
                    return list;      // 百度是用户明确选的，失败了不该偷偷换别的引擎出乱码
                }
            }
            else if (engine == "space")
            {
                try
                {
                    list = RecognizeOcrSpace(imagePath, spaceKey);
                    if (list.Count > 0) return list;
                    LogLine("OCR.space 没结果（" + (LastError ?? "空") + "），退回 Windows OCR");
                }
                catch (Exception ex) { LogLine("OCR.space 异常: " + ex.Message); }
            }

            return RecognizeWindows(imagePath);
        }

        // ================= 文本 → 待办 =================

        private static readonly Regex NoiseRx = new Regex(
            @"^[\s\p{P}\p{S}]*$|^[Oo0○◯〇□☐☑✓√·•\-–—*]+$", RegexOptions.Compiled);

        private static readonly Regex LeadingRx = new Regex(
            @"^[\s○◯〇Oo□☐☑✓√·•\-–—*]+|^\d{1,2}\s*[.、)）]\s*|^[①-⑳]\s*", RegexOptions.Compiled);

        private static readonly HashSet<string> UiLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "待办", "全部待办", "待办清单", "我的待办", "清单", "已完成", "未完成",
            "今天", "昨天", "明天", "本周", "全部", "笔记", "便签",
            "新建", "编辑", "完成", "更多", "设置", "搜索",
            "小米笔记", "小米便签", "云服务", "同步"
        };

        private static readonly Regex StatusRx = new Regex(
            @"^\d{1,2}:\d{2}$|^\d{1,3}\s*%$|^[+\-]?\d{1,3}$", RegexOptions.Compiled);

        /// <summary>
        /// 把 OCR 结果切成一条条待办。
        /// Windows 的简中 OCR 会在每个汉字之间插空格（"买 牛 奶"），这里先还原。
        /// </summary>
        public static List<string> SplitTodos(List<Line> lines)
        {
            var todos = new List<string>();
            if (lines == null) return todos;

            foreach (var ln in lines)
            {
                if (ln.Score > 0 && ln.Score < 0.45) continue;
                string t = Normalize(ln.Text);
                if (string.IsNullOrEmpty(t)) continue;

                t = LeadingRx.Replace(t, "").Trim();
                t = t.Trim(' ', '·', '•', '-', '—', '*', '|');
                if (t.Length == 0) continue;
                if (NoiseRx.IsMatch(t)) continue;
                if (UiLabels.Contains(t)) continue;
                if (StatusRx.IsMatch(t)) continue;
                if (t.Length > 500) t = t.Substring(0, 500) + "…";

                if (todos.Count > 0 && todos[todos.Count - 1] == t) continue;
                todos.Add(t);
            }
            return todos;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\u3000', ' ');
            s = Regex.Replace(s, @"(?<=[\u4e00-\u9fff\u3400-\u4dbf])\s+(?=[\u4e00-\u9fff\u3400-\u4dbf])", "");
            s = Regex.Replace(s, @"(?<=[\u4e00-\u9fff])\s+(?=[0-9A-Za-z（）()、，。：:；;！!？?])", "");
            s = Regex.Replace(s, @"(?<=[0-9A-Za-z）)、，。：:；;！!？?])\s+(?=[\u4e00-\u9fff])", "");
            return Regex.Replace(s, @"[ \t]{2,}", " ").Trim();
        }

        // ================= 小工具 =================

        private static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        private static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "Mozilla/5.0";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var rd = new StreamReader(s, Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch (WebException wex)
            {
                // 401/400 这类错误，百度会在响应体里给 error / error_description，
                // 必须读出来，否则用户只能看到「连不上服务器」这种误导性提示。
                string body = ReadErrorBody(wex);
                if (!string.IsNullOrEmpty(body)) return body;
                LastError = wex.Message;
                return null;
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        private static string HttpPostForm(string url, string form, int timeoutMs)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(form);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.ContentLength = data.Length;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "Mozilla/5.0";
                using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var rd = new StreamReader(s, Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch (WebException wex)
            {
                string body = ReadErrorBody(wex);
                if (!string.IsNullOrEmpty(body)) return body;
                LastError = wex.Message;
                return null;
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        /// <summary>
        /// 自己做 x-www-form-urlencoded 编码。
        /// 不用 Uri.EscapeDataString：.NET Framework 版对超长字符串（截图 base64 动辄几 MB）
        /// 会抛「无效的 URI: URI 字符串太长」，实测踩过。
        /// </summary>
        private static string FormEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 32);
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            foreach (byte b in bytes)
            {
                if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                    (b >= '0' && b <= '9') || b == '-' || b == '_' || b == '.' || b == '~')
                    sb.Append((char)b);
                else if (b == ' ') sb.Append('+');
                else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        private static string ReadErrorBody(WebException wex)        {
            try
            {
                if (wex.Response == null) return null;
                using (var s = wex.Response.GetResponseStream())
                using (var rd = new StreamReader(s, Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch { return null; }
        }

        private static string JsonField(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return "";
            return JsonUnescape(m.Groups[1].Value);
        }

        private static string JsonUnescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
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
                            if (int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }
    }
}

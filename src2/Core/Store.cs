using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TempNotes
{
    /// <summary>
    /// 数据层：notes.json 单文件存全部笔记；附件实体在 blobs\&lt;noteId&gt;\&lt;原始文件名&gt;。
    /// 附件保持原始文件名是硬要求——拖到微信时对方看到的文件名就是磁盘名。
    /// </summary>
    public class Store
    {
        public string Root { get; private set; }
        public string BlobDir { get { return Path.Combine(Root, "blobs"); } }
        public string ThumbDir { get { return Path.Combine(Root, "thumbs"); } }
        public string BackupDir { get { return Path.Combine(Root, "backup"); } }
        public string DbPath { get { return Path.Combine(Root, "notes.json"); } }

        public Database Db { get; private set; }
        public Settings Settings { get; private set; }

        // —— v2813：优先级名称 —— 一个颜色一个名字，索引与前端 NOTE_COLORS 一一对应 ——
        public const int LabelCount = 10;
        public List<string> Labels { get; private set; }
        public List<int> LabelOrder { get; private set; }     // chip 顺序：0..9 的一个排列（v2816）
        public string LabelsPath { get { return Path.Combine(Root, "labels.json"); } }
        private DateTime _labelsMtime = DateTime.MinValue;   // 已读盘的那份 labels.json 的 mtime（跨端热同步用）

        // —— v2810：数据改成「一条笔记一个文件」——
        // 单文件 notes.json 在两台机器同时写时是整个文件级的竞态：谁后写谁把对方的改动整份吃掉，
        // 而且删除无法表达（合并只会「加回来」）。拆分后每个笔记各占一个文件，OneDrive 逐文件同步，
        // 冲突范围从「整库」缩小到「同一条笔记」，删除用 deleted\<id>.json 墓碑表达。
        public string NotesDir { get { return Path.Combine(Root, "notes"); } }
        public string DeletedDir { get { return Path.Combine(Root, "deleted"); } }

        private readonly Dictionary<string, string> _written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // id → 上次写入时的内容指纹
        private readonly Dictionary<string, DateTime> _seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase); // 文件全路径 → 已读 mtime

        private long _loadedRev;                // 兼容旧字段（已不再用于判断）
        private DateTime _loadedWriteUtc;       // 兼容旧字段（已不再用于判断）
        private bool _saving;                   // 防止 Save ↔ Merge 互相递归

        private static readonly DataContractJsonSerializer DbSer =
            new DataContractJsonSerializer(typeof(Database));
        private static readonly DataContractJsonSerializer SetSer =
            new DataContractJsonSerializer(typeof(Settings));
        private static readonly DataContractJsonSerializer NoteSer =
            new DataContractJsonSerializer(typeof(Note));
        private static readonly DataContractJsonSerializer TombSer =
            new DataContractJsonSerializer(typeof(Tombstone));
        private static readonly DataContractJsonSerializer LabSer =
            new DataContractJsonSerializer(typeof(LabelFile));

        /// <summary>日志回调（由 MainWindow 注入，写进 bridge.log）。</summary>
        public static Action<string> LogSink;
        private static void Log(string msg)
        {
            try { var s = LogSink; if (s != null) s("Store " + msg); } catch { }
        }

        /// <summary>数据目录的引导文件，放在 exe 同目录。不能写进 settings.json ——
        /// settings.json 本身就在数据目录里，改目录时等于自己搬自己。</summary>
        private static string RootFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "root.txt"); } }
        public static string DefaultRoot { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"); } }

        private static string ReadRoot()
        {
            try
            {
                if (File.Exists(RootFile))
                {
                    string s = File.ReadAllText(RootFile).Trim().Trim('"');
                    if (s.Length > 0) return s;
                }
            }
            catch { }
            return DefaultRoot;
        }

        public Store()
        {
            Root = ReadRoot();
            EnsureDirs();
            Load();
            // 启动时修一次失效路径（数据目录搬过家、或从另一台机器同步过来的 notes.json），
            // 否则附件一律显示「文件已被移动或删除」，清空回收站也删不掉实体。
            try { RepairAttachmentPaths(); } catch { }
        }

        private void EnsureDirs()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(NotesDir);
            Directory.CreateDirectory(DeletedDir);
            Directory.CreateDirectory(BlobDir);
            Directory.CreateDirectory(ThumbDir);
            Directory.CreateDirectory(BackupDir);
        }

        /// <summary>
        /// 切换数据目录（移动 + 合并 + 去重）：
        ///   1) 读目标目录里已有的 notes.json（比如 OneDrive 上别的机器同步过去的）；
        ///   2) 和本机现有数据合并：按 Note.Id 去重，同一条取 updated 更新的那份，附件取并集；
        ///   3) 复制 blobs / thumbs 里目标没有的文件（同名跳过）；
        ///   4) 原子写入目标 notes.json 并校验条数；
        ///   5) 切到新目录、写 root.txt、重新载入；
        ///   6) 移动语义：确认合并落盘后，删掉源目录里的数据文件（root.txt 在 exe 目录，不参与）。
        /// </summary>
        /// <summary>读某个数据目录里的全部笔记（兼容旧版单文件 notes.json 与新版 notes\ 目录）。</summary>
        private List<Note> ReadNotesFromRoot(string root)
        {
            var list = new List<Note>();
            try
            {
                string nd = Path.Combine(root, "notes");
                var files = SafeFiles(nd, "*.json");
                if (files.Count > 0)
                {
                    foreach (var f in files) { var n = TryReadNote(f); if (n != null) list.Add(n); }
                    return list;
                }
                string legacy = Path.Combine(root, "notes.json");
                if (File.Exists(legacy))
                {
                    var db = LoadJson<Database>(legacy, DbSer);
                    if (db != null && db.Notes != null)
                        foreach (var n in db.Notes) { if (n == null) continue; Normalize(n); list.Add(n); }
                }
            }
            catch { }
            return list;
        }

        public bool SwitchRoot(string newRoot, out string msg)
        {
            msg = "";
            try
            {
                if (string.IsNullOrWhiteSpace(newRoot)) { msg = "目录为空"; return false; }
                newRoot = Path.GetFullPath(newRoot.Trim().Trim('"')).TrimEnd('\\');
                string old = Root.TrimEnd('\\');
                if (string.Equals(newRoot, old, StringComparison.OrdinalIgnoreCase)) { msg = "和当前目录相同"; return false; }
                if (newRoot.StartsWith(old + "\\", StringComparison.OrdinalIgnoreCase))
                { msg = "新目录不能在当前数据目录里面"; return false; }
                if (old.StartsWith(newRoot + "\\", StringComparison.OrdinalIgnoreCase))
                { msg = "当前目录在新目录里面，不能这样合并"; return false; }

                Directory.CreateDirectory(newRoot);
                Directory.CreateDirectory(Path.Combine(newRoot, "notes"));
                Directory.CreateDirectory(Path.Combine(newRoot, "deleted"));

                // ---- 1) 目标目录已有数据（旧版单文件 / 新版按笔记分文件都认）----
                var targetNotes = ReadNotesFromRoot(newRoot);
                var tomb = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in ReadTombstoneFiles(Path.Combine(newRoot, "deleted")))
                    if (t != null && !string.IsNullOrEmpty(t.Id)) tomb[t.Id] = t.At;
                if (Db.Deleted != null)
                    foreach (var t in Db.Deleted)
                        if (t != null && !string.IsNullOrEmpty(t.Id))
                        { long o; if (!tomb.TryGetValue(t.Id, out o) || t.At > o) tomb[t.Id] = t.At; }

                // ---- 2) 合并去重 ----
                var byId = new Dictionary<string, Note>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in targetNotes) if (n != null && !string.IsNullOrEmpty(n.Id)) byId[n.Id] = n;

                int added = 0, dup = 0, mergedAtt = 0;
                foreach (var src in Db.Notes)
                {
                    if (src == null || string.IsNullOrEmpty(src.Id)) continue;
                    if (src.Attachments == null) src.Attachments = new List<Attachment>();

                    Note exist;
                    if (!byId.TryGetValue(src.Id, out exist))
                    {
                        byId[src.Id] = src; added++;
                        continue;
                    }
                    dup++;
                    Note newer = (src.UpdatedAt > exist.UpdatedAt) ? src : exist;
                    Note older = ReferenceEquals(newer, src) ? exist : src;
                    if (newer.Attachments == null) newer.Attachments = new List<Attachment>();
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var a in newer.Attachments) if (a != null && !string.IsNullOrEmpty(a.Id)) ids.Add(a.Id);
                    foreach (var a in (older.Attachments ?? new List<Attachment>()))
                    {
                        if (a == null || string.IsNullOrEmpty(a.Id) || ids.Contains(a.Id)) continue;
                        newer.Attachments.Add(a); ids.Add(a.Id); mergedAtt++;
                    }
                    byId[src.Id] = newer;
                }

                // 墓碑说话：删过的条目不再写回目标目录
                foreach (var t in tomb)
                {
                    Note n;
                    if (byId.TryGetValue(t.Key, out n) && t.Value >= n.UpdatedAt) byId.Remove(t.Key);
                }

                // ---- 3) 附件实体 / 缩略图 ----
                int copied = 0, skipped = 0;
                CopyTreeInto(BlobDir, Path.Combine(newRoot, "blobs"), ref copied, ref skipped);
                CopyTreeInto(ThumbDir, Path.Combine(newRoot, "thumbs"), ref copied, ref skipped);

                string targetSet = Path.Combine(newRoot, "settings.json");
                try
                {
                    if (!File.Exists(targetSet) && File.Exists(Path.Combine(old, "settings.json")))
                        File.Copy(Path.Combine(old, "settings.json"), targetSet, false);
                    // 优先级名称跟着数据目录一起搬（它是数据，不是本机偏好）
                    string targetLbl = Path.Combine(newRoot, "labels.json");
                    if (!File.Exists(targetLbl) && File.Exists(LabelsPath))
                        File.Copy(LabelsPath, targetLbl, false);
                }
                catch { }

                // ---- 4) 落盘并校验 ----
                var merged = new Database { Notes = new List<Note>(byId.Values), Deleted = new List<Tombstone>() };
                foreach (var t in tomb) merged.Deleted.Add(new Tombstone { Id = t.Key, At = t.Value });
                RepathAttachments(merged, newRoot);

                foreach (var n in merged.Notes) WriteAtomic(Path.Combine(newRoot, "notes", n.Id + ".json"), NoteSer, n);
                foreach (var t in merged.Deleted) WriteAtomic(Path.Combine(newRoot, "deleted", t.Id + ".json"), TombSer, t);
                // 旧版单文件留着会和 notes\ 打架，迁移完就改名留档
                string legacyTarget = Path.Combine(newRoot, "notes.json");
                try { if (File.Exists(legacyTarget)) File.Move(legacyTarget, legacyTarget + ".migrated-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }

                var check = ReadNotesFromRoot(newRoot);
                int want = merged.Notes.Count, got = check.Count;
                if (got != want) { msg = "校验失败：应写 " + want + " 条，实际读回 " + got + " 条"; return false; }

                // ---- 5) 切换 ----
                string oldRoot = Root;
                Root = newRoot;
                EnsureDirs();
                File.WriteAllText(RootFile, Root);
                Load();

                // ---- 6) 移动语义：源目录清空（只删数据，不删目录本身）----
                try { DeleteDataFiles(oldRoot); } catch { }

                msg = "合并完成：新增 " + added + " 条，重复合并 " + dup + " 条（补进附件 " + mergedAtt +
                      " 个），复制文件 " + copied + " 个" + (skipped > 0 ? "，同名跳过 " + skipped + " 个" : "") +
                      "；源目录数据已清空";
                return true;
            }
            catch (Exception ex) { msg = ex.Message; return false; }
        }

        /// <summary>复制目录树，只补目标缺失的文件（同名不覆盖）。</summary>
        private static void CopyTreeInto(string from, string to, ref int copied, ref int skipped)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
            {
                string dst = Path.Combine(to, Path.GetFileName(f));
                if (File.Exists(dst)) { skipped++; continue; }
                try { File.Copy(f, dst, false); copied++; } catch { }
            }
            foreach (var d in Directory.GetDirectories(from))
                CopyTreeInto(d, Path.Combine(to, Path.GetFileName(d)), ref copied, ref skipped);
        }

        /// <summary>删掉某个数据目录里的数据文件（移动成功后清理源目录）。</summary>
        private static void DeleteDataFiles(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            foreach (var dir in new[] { "notes", "deleted", "blobs", "thumbs", "backup" })
            {
                try { var d = Path.Combine(root, dir); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            }
            foreach (var f in new[] { "notes.json", "settings.json" })
            {
                try { var x = Path.Combine(root, f); if (File.Exists(x)) File.Delete(x); } catch { }
            }
        }

        /// <summary>当前数据目录占用的空间（给设置面板显示）。</summary>
        public string DataSizeText
        {
            get
            {
                try
                {
                    long total = 0;
                    foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                    { try { total += new FileInfo(f).Length; } catch { } }
                    if (total >= 1024L * 1024 * 1024) return (total / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
                    if (total >= 1024 * 1024) return (total / 1024.0 / 1024).ToString("0.0") + " MB";
                    if (total >= 1024) return (total / 1024.0).ToString("0") + " KB";
                    return total + " B";
                }
                catch { return "未知"; }
            }
        }

        // ---------------- 读写 ----------------

        public void Load()
        {
            EnsureDirs();
            MigrateLegacyDb();                       // 老的单文件 notes.json → notes\<id>.json（只做一次，原文件改名留档）

            Db = new Database();
            Settings = LoadJson<Settings>(Path.Combine(Root, "settings.json"), SetSer) ?? new Settings();
            Labels = LoadLabels();

            _written.Clear();
            _seen.Clear();

            var byId = new Dictionary<string, Note>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in SafeFiles(NotesDir, "*.json"))
            {
                var n = TryReadNote(f);
                if (n == null) continue;
                _seen[f] = LastWriteUtc(f);
                Note exist;
                if (!byId.TryGetValue(n.Id, out exist)) byId[n.Id] = n;
                else if (n.UpdatedAt > exist.UpdatedAt) byId[n.Id] = n;   // 同 id 多份 = 同步冲突副本，取新的
            }
            Db.Notes = byId.Values.ToList();
            Db.Deleted = ReadTombstoneFiles();

            ApplyTombstones();                        // 别的机器删掉的，这里不要复活
            PruneTombstones();
            foreach (var n in Db.Notes) _written[n.Id] = Signature(n);

            int prunedOnLoad = PruneEmptyNotes();     // 启动即清掉历史遗留的空卡片
            _loadedRev = Db.Rev;
            _loadedWriteUtc = DateTime.UtcNow;
            if (prunedOnLoad > 0) WriteChanged();
            TryBackupOnStart();
        }

        /// <summary>老版本把全部笔记放在 notes.json 里。首次升级到 v2810 时拆成一注一文件。</summary>
        private void MigrateLegacyDb()
        {
            try
            {
                if (SafeFiles(NotesDir, "*.json").Count > 0) return;      // 已经拆过了
                if (!File.Exists(DbPath)) return;
                var db = LoadJson<Database>(DbPath, DbSer);
                if (db == null || db.Notes == null) return;

                int wrote = 0;
                foreach (var n in db.Notes)
                {
                    if (n == null || string.IsNullOrEmpty(n.Id)) continue;
                    Normalize(n);
                    WriteAtomic(NotePath(n.Id), NoteSer, n);
                    wrote++;
                }
                if (db.Deleted != null)
                    foreach (var t in db.Deleted) if (t != null && !string.IsNullOrEmpty(t.Id)) WriteTombstoneFile(t.Id, t.At);

                // 原文件改名留档，不删 —— 出问题时还能人工翻回旧档
                string keep = DbPath + ".migrated-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(DbPath, keep); } catch { }
                Log("数据已升级为按笔记分文件：" + wrote + " 条（旧档留档 " + Path.GetFileName(keep) + "）");
            }
            catch (Exception ex) { Log("升级数据结构失败：" + ex.Message); }
        }

        private static void Normalize(Note n)
        {
            if (n.Attachments == null) n.Attachments = new List<Attachment>();
            if (n.Text == null) n.Text = "";
            if (n.Sort == 0) n.Sort = n.CreatedAt;
        }

        private Note TryReadNote(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                Note n;
                using (var fs = File.OpenRead(path)) n = (Note)NoteSer.ReadObject(fs);
                if (n == null || string.IsNullOrEmpty(n.Id)) return null;
                Normalize(n);
                return n;
            }
            catch { return null; }   // 正在被同步/半写状态：下次扫描再读
        }

        private string NotePath(string id) { return Path.Combine(NotesDir, id + ".json"); }
        private string TombstonePath(string id) { return Path.Combine(DeletedDir, id + ".json"); }

        private void WriteNoteFile(Note n)
        {
            WriteAtomic(NotePath(n.Id), NoteSer, n);
            _seen[NotePath(n.Id)] = LastWriteUtc(NotePath(n.Id));
            _written[n.Id] = Signature(n);
        }

        private void DeleteNoteFile(string id)
        {
            try { var p = NotePath(id); if (File.Exists(p)) File.Delete(p); _seen.Remove(p); } catch { }
            _written.Remove(id);
        }

        private void WriteTombstoneFile(string id, long at)
        {
            var t = new Tombstone { Id = id, At = at };
            WriteAtomic(TombstonePath(id), TombSer, t);
        }

        private static List<Tombstone> ReadTombstoneFiles(string dir)
        {
            var list = new List<Tombstone>();
            try
            {
                if (!Directory.Exists(dir)) return list;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        using (var fs = File.OpenRead(f)) { var t = (Tombstone)TombSer.ReadObject(fs); if (t != null && !string.IsNullOrEmpty(t.Id)) list.Add(t); }
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private List<Tombstone> ReadTombstoneFiles() { return ReadTombstoneFiles(DeletedDir); }

        /// <summary>内存里的墓碑落到笔记列表上：更新时间早于删除时间的条目直接消失。</summary>
        private void ApplyTombstones()
        {
            if (Db.Deleted == null || Db.Deleted.Count == 0) return;
            var tomb = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Db.Deleted)
            {
                if (t == null || string.IsNullOrEmpty(t.Id)) continue;
                long old;
                if (!tomb.TryGetValue(t.Id, out old) || t.At > old) tomb[t.Id] = t.At;
            }
            for (int i = Db.Notes.Count - 1; i >= 0; i--)
            {
                var n = Db.Notes[i];
                long at;
                if (n != null && tomb.TryGetValue(n.Id, out at) && at >= n.UpdatedAt)
                {
                    Db.Notes.RemoveAt(i);
                    DeleteNoteFile(n.Id);
                }
            }
        }

        private bool IsDeleted(string id, long updatedAt)
        {
            if (Db.Deleted == null) return false;
            foreach (var t in Db.Deleted)
                if (t != null && string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) && t.At >= updatedAt) return true;
            return false;
        }

        private void MarkDeleted(string id, long at)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (Db.Deleted == null) Db.Deleted = new List<Tombstone>();
            bool found = false;
            foreach (var t in Db.Deleted)
                if (t != null && string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) { t.At = Math.Max(t.At, at); found = true; break; }
            if (!found) Db.Deleted.Add(new Tombstone { Id = id, At = at });
            try { WriteTombstoneFile(id, at); } catch { }
        }

        /// <summary>墓碑留 30 天足够所有机器同步到；过期就删文件，免得目录越堆越多。</summary>
        private void PruneTombstones()
        {
            try
            {
                if (Db.Deleted == null) { Db.Deleted = new List<Tombstone>(); return; }
                long cutoff = Note.NowMs() - 30L * 24 * 3600 * 1000;
                Db.Deleted.RemoveAll(t => t == null || t.At < cutoff);
                if (!Directory.Exists(DeletedDir)) return;
                foreach (var f in Directory.GetFiles(DeletedDir, "*.json"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        private static string Signature(Note n)
        {
            if (n == null) return "";
            var sb = new StringBuilder(64);
            sb.Append(n.UpdatedAt).Append('|').Append(n.Status).Append('|').Append(n.Sort).Append('|')
              .Append(n.Pinned ? 1 : 0).Append('|').Append(n.Color ?? "").Append('|')
              .Append(n.Attachments == null ? 0 : n.Attachments.Count).Append('|')
              .Append(n.Text == null ? "" : n.Text);
            return sb.ToString();
        }

        private static List<string> SafeFiles(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? new List<string>(Directory.GetFiles(dir, pattern)) : new List<string>(); }
            catch { return new List<string>(); }
        }

        private static DateTime LastWriteUtc(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>启动时留一份快照（单文件、旧格式，方便人工回滚），只留最近 7 份。</summary>
        private void TryBackupOnStart()
        {
            try
            {
                if (Db == null || Db.Notes == null || Db.Notes.Count == 0) return;
                var snap = new Database { Rev = Db.Rev, Notes = Db.Notes, Deleted = Db.Deleted };
                string name = "notes-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
                WriteAtomic(Path.Combine(BackupDir, name), DbSer, snap);
                var old = new DirectoryInfo(BackupDir).GetFiles("notes-*.json")
                    .OrderByDescending(f => f.CreationTimeUtc).Skip(7).ToList();
                foreach (var f in old) { try { f.Delete(); } catch { } }
            }
            catch { }
        }

        private static T LoadJson<T>(string path, DataContractJsonSerializer ser) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var fs = File.OpenRead(path)) return (T)ser.ReadObject(fs);
            }
            catch
            {
                // 坏档：改名留证，不静默丢数据
                try { File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
                return null;
            }
        }

        public void Save()
        {
            Save(false);
        }

        /// <summary>
        /// skipMerge=true 时不先做磁盘合并 —— 只有「删除类」操作才这么用：
        /// 合并会把「内存里已经删掉、但磁盘上还在」的条目按 id 加回来，删除等于白删。
        /// </summary>
        public void Save(bool skipMerge)
        {
            // v2810 起 Save 只写「内容变了的那几条」，不再整份重写 notes.json。
            // 写前仍先收一眼磁盘（另一台刚改过的条目），避免把对方的新内容覆盖掉。
            if (!skipMerge && !_saving)
            {
                _saving = true;
                try { PullFromDiskCore(false); } catch { } finally { _saving = false; }
            }
            WriteChanged();
        }

        /// <summary>只为内容有变化的笔记写文件；没变的文件一个都不碰（减少与 OneDrive 同步撞车）。</summary>
        private void WriteChanged()
        {
            foreach (var n in Db.Notes.ToList())
            {
                if (n == null || string.IsNullOrEmpty(n.Id)) continue;
                string sig = Signature(n);
                string old;
                if (_written.TryGetValue(n.Id, out old) && old == sig && File.Exists(NotePath(n.Id))) continue;
                try { WriteNoteFile(n); } catch { }
            }
            _loadedWriteUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// 删掉「既没有文字、也没有附件」的笔记。回收站（status=2）里的不动。
        /// 只改内存，返回删了几条；要不要落盘由调用方决定。
        /// 这个清理以前只在「编辑清空 / 删掉最后一个附件」时跑，所以 add、切完成、置顶
        /// 这几条路径能留下永久空卡片（用户 2026-11-09 发现置顶区有一条空的）。
        /// </summary>
        public int PruneEmptyNotes()
        {
            int removed = 0;
            try
            {
                for (int i = Db.Notes.Count - 1; i >= 0; i--)
                {
                    var n = Db.Notes[i];
                    if (n == null) { Db.Notes.RemoveAt(i); removed++; continue; }
                    if (n.Status == 2) continue;                                  // 回收站保留
                    if (!string.IsNullOrWhiteSpace(n.Text)) continue;              // 有文字
                    if (n.Attachments != null && n.Attachments.Count > 0) continue; // 有附件
                    MarkDeleted(n.Id, Math.Max(Note.NowMs(), n.UpdatedAt));   // 空卡片也别让它从别的机器"复活"
                    DeleteNoteFile(n.Id);
                    Db.Notes.RemoveAt(i);
                    removed++;
                }
            }
            catch { }
            return removed;
        }

        /// <summary>先清空笔记再落盘：删掉了就跳过合并（否则会被磁盘份合并回来）。</summary>
        public void SaveAfterPrune()
        {
            int n = PruneEmptyNotes();
            Save(n > 0);
        }

        /// <summary>
        /// 把磁盘上「新出现 / 被改过」的笔记文件并进内存（同 id 取 UpdatedAt 新的），
        /// 并按 deleted\ 里的墓碑删掉本机还留着的条目。返回是否有变化。
        /// v2810 起逐文件比对 mtime，只有变过的那几条才读盘。
        /// </summary>
        public bool MergeFromDisk(bool force = false)
        {
            return PullFromDiskCore(false, force);
        }

        private bool PullFromDiskCore(bool writeBack, bool force = false)
        {
            bool changed = false;
            try
            {
                // 1) 墓碑：另一台永久删除的，本机也要消失（这是旧版做不到的）
                var diskTomb = ReadTombstoneFiles();
                if (diskTomb.Count > 0)
                {
                    if (Db.Deleted == null) Db.Deleted = new List<Tombstone>();
                    foreach (var t in diskTomb)
                    {
                        bool known = false;
                        foreach (var m in Db.Deleted)
                            if (m != null && string.Equals(m.Id, t.Id, StringComparison.OrdinalIgnoreCase))
                            { m.At = Math.Max(m.At, t.At); known = true; break; }
                        if (!known) Db.Deleted.Add(new Tombstone { Id = t.Id, At = t.At });

                        for (int i = Db.Notes.Count - 1; i >= 0; i--)
                        {
                            var n = Db.Notes[i];
                            if (n != null && string.Equals(n.Id, t.Id, StringComparison.OrdinalIgnoreCase) && t.At >= n.UpdatedAt)
                            {
                                Db.Notes.RemoveAt(i);
                                _written.Remove(n.Id);
                                try { DeleteNoteFile(n.Id); } catch { }   // 墓碑到了，本机的笔记文件也一起删掉，别留孤儿
                                changed = true;
                            }
                        }
                    }
                }

                // 2) 笔记文件：只读 mtime 变过的
                foreach (var f in SafeFiles(NotesDir, "*.json"))
                {
                    DateTime mtime = LastWriteUtc(f);
                    DateTime prev;
                    if (_seen.TryGetValue(f, out prev) && mtime <= prev && !force) continue;
                    var dn = TryReadNote(f);
                    if (dn == null) continue;
                    _seen[f] = mtime;
                    if (IsDeleted(dn.Id, dn.UpdatedAt)) continue;      // 已删的不复活

                    Note mine = null;
                    for (int i = 0; i < Db.Notes.Count; i++)
                        if (string.Equals(Db.Notes[i].Id, dn.Id, StringComparison.OrdinalIgnoreCase)) { mine = Db.Notes[i]; break; }

                    if (mine == null) { Db.Notes.Add(dn); _written[dn.Id] = ""; changed = true; }
                    else if (dn.UpdatedAt > mine.UpdatedAt)
                    {
                        int idx = Db.Notes.IndexOf(mine);
                        Db.Notes[idx] = dn;
                        _written[dn.Id] = "";
                        changed = true;
                    }
                }

                // 3) 优先级名称：另一台改了名字，OneDrive 落到本地后 mtime 会变，这里跟上
                try
                {
                    if (File.Exists(LabelsPath))
                    {
                        DateTime lm = LastWriteUtc(LabelsPath);
                        if (lm > _labelsMtime)
                        {
                            var oldOrder = LabelOrder;
                            var nl = LoadLabels();      // 读的同时把 _labelsMtime / LabelOrder 更新掉
                            bool lch = nl != null && !SameLabelList(nl, Labels);
                            bool och = !SameIntList(LabelOrder, oldOrder);
                            if (lch) Labels = nl;
                            if (lch || och) changed = true;
                        }
                    }
                }
                catch { }
            }
            catch { return changed; }

            if (changed && writeBack) WriteChanged();
            return changed;
        }

        /// <summary>外面（窗口激活 / 文件监听）用：磁盘有新东西就并进内存并落盘，返回是否有变化。</summary>
        public bool PullFromDisk()
        {
            bool changed;
            try { changed = PullFromDiskCore(true); } catch { return false; }
            return changed;
        }

        public void SaveSettings()
        {
            WriteAtomic(Path.Combine(Root, "settings.json"), SetSer, Settings);
        }

        // ---------------- 优先级名称（v2813） ----------------

        private List<string> LoadLabels()
        {
            var list = new List<string>();
            for (int i = 0; i < LabelCount; i++) list.Add("");
            LabelOrder = DefaultOrder();
            if (!File.Exists(LabelsPath)) { _labelsMtime = DateTime.MinValue; return list; }

            // v2828：labels.json 躺在 OneDrive 目录里，另一端或 OneDrive 自己随时可能在写它。
            // 以前这里复用 LoadJson —— 它把"读的时候被占用/读到半截"也当成坏档，直接改名成 .corrupt，
            // 等于一次偶发占用就把用户起的名字从界面上抹掉了（2026-11-10 实际发生过一次）。
            // 现在：共享读 + 短重试；读不到就先当没改动，等下一次轮询；只有真的解析不了才留证，
            // 而且只往 backup/ 复制一份，绝不动原名。
            byte[] data = ReadSharedBytes(LabelsPath);
            if (data == null || data.Length == 0)
            {
                Log("labels.json 暂时读不到（可能正被写入），稍后重试");
                _labelsMtime = DateTime.MinValue;
                return list;
            }

            LabelFile f = null;
            try { using (var ms = new MemoryStream(data)) f = (LabelFile)LabSer.ReadObject(ms); }
            catch { f = null; }

            if (f == null)
            {
                // 也许是读到半截：等一下再读一次，成了就正常用
                System.Threading.Thread.Sleep(160);
                byte[] again = ReadSharedBytes(LabelsPath);
                if (again != null && again.Length > 0)
                {
                    try { using (var ms = new MemoryStream(again)) f = (LabelFile)LabSer.ReadObject(ms); }
                    catch { f = null; }
                    if (f != null) data = again;
                }
            }

            if (f == null)
            {
                try
                {
                    Directory.CreateDirectory(BackupDir);
                    File.WriteAllBytes(Path.Combine(BackupDir, "labels-broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), data);
                }
                catch { }
                Log("labels.json 解析失败，已复制一份到 backup/ 留证，本次先用默认值（原名不动）");
                _labelsMtime = LastWriteUtc(LabelsPath);
                return list;
            }

            if (f.Labels != null)
                for (int i = 0; i < LabelCount && i < f.Labels.Count; i++)
                    list[i] = ClipLabel(f.Labels[i]);
            if (f.Order != null && f.Order.Count > 0)
                LabelOrder = NormalizeOrder(f.Order);
            _labelsMtime = LastWriteUtc(LabelsPath);
            return list;
        }

        /// <summary>共享读：允许别人同时持有写句柄；失败就短退避重试几次，最后还是不行返回 null。</summary>
        private static byte[] ReadSharedBytes(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var ms = new MemoryStream())
                    {
                        fs.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                catch (IOException) { System.Threading.Thread.Sleep(80 + attempt * 60); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(80 + attempt * 60); }
            }
            return null;
        }

        // ---------------- chip 顺序（v2816） ----------------

        private static List<int> DefaultOrder()
        {
            var list = new List<int>();
            for (int i = 0; i < LabelCount; i++) list.Add(i);
            return list;
        }

        /// <summary>把外面传来的顺序理成 0..LabelCount-1 的一个完整排列：丢掉越界/重复的，缺的按原序补到后面。</summary>
        public static List<int> NormalizeOrder(IEnumerable<int> src)
        {
            var seen = new bool[LabelCount];
            var list = new List<int>();
            if (src != null)
                foreach (int v in src)
                {
                    if (v < 0 || v >= LabelCount || seen[v]) continue;
                    seen[v] = true;
                    list.Add(v);
                }
            for (int i = 0; i < LabelCount; i++) if (!seen[i]) list.Add(i);
            return list;
        }

        /// <summary>筛选条 chip 拖完的新顺序。返回是否真的有变化。</summary>
        public bool SetLabelOrder(List<int> order)
        {
            var v = NormalizeOrder(order);
            if (SameIntList(v, LabelOrder)) return false;
            LabelOrder = v;
            SaveLabels();
            return true;
        }

        /// <summary>名字最多 5 个字（用户定的口径）；去首尾空白，换行/制表压成空格。</summary>
        public static string ClipLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var t = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            if (t.Length > 5)
            {
                int cut = 5;
                if (char.IsHighSurrogate(t[cut - 1])) cut--;    // 别把代理对劈成半个字
                t = t.Substring(0, cut);
            }
            return t;
        }

        /// <summary>设置某个优先级的名字。返回是否真的有变化（没变就不用回推前端）。</summary>
        public bool SetLabel(int index, string name)
        {
            if (index < 0 || index >= LabelCount || Labels == null) return false;
            var v = ClipLabel(name);
            if (string.Equals(Labels[index], v, StringComparison.Ordinal)) return false;
            Labels[index] = v;
            SaveLabels();
            return true;
        }

        public void SaveLabels()
        {
            if (Labels == null) return;
            if (LabelOrder == null) LabelOrder = DefaultOrder();
            var f = new LabelFile { Version = 1, Labels = new List<string>(Labels), Order = new List<int>(LabelOrder) };
            WriteAtomic(LabelsPath, LabSer, f);
            try { _labelsMtime = File.Exists(LabelsPath) ? LastWriteUtc(LabelsPath) : DateTime.MinValue; } catch { }
        }

        private static bool SameIntList(List<int> a, List<int> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static bool SameLabelList(List<string> a, List<string> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static void WriteAtomic(string path, DataContractJsonSerializer ser, object obj)
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, obj);
                bytes = ms.ToArray();
            }
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; } catch { }
                try { File.Delete(path); } catch { }
            }
            File.Move(tmp, path);
        }

        /// <summary>每次启动备份一份（单文件、旧格式，方便人工回滚），只留最近 7 份。</summary>
        public void BackupOnStart()
        {
            TryBackupOnStart();
        }

        // ---------------- 笔记操作 ----------------

        public IEnumerable<Note> ActiveNotes
        {
            get
            {
                return Db.Notes.Where(n => n.Status != 2)
                    .OrderBy(n => n.Status)              // 待办在前，已完成沉底
                    .ThenByDescending(n => n.SortKey)
                    .ThenByDescending(n => n.CreatedAt);
            }
        }

        public IEnumerable<Note> TrashNotes
        {
            get { return Db.Notes.Where(n => n.Status == 2).OrderByDescending(n => n.UpdatedAt); }
        }

        public Note AddNote(string text, string color)
        {
            var n = new Note { Text = text ?? "", Color = color ?? "" };
            Db.Notes.Add(n);
            Save();
            return n;
        }

        public void Touch(Note n)
        {
            n.UpdatedAt = Note.NowMs();
            Save();
        }

        /// <summary>
        /// 添加附件。默认 **只引用原路径，不复制**。
        ///
        /// 之前的做法是每个附件都 File.Copy 进 blobs/，用户拖 10 个视频就凭空多出几 GB 副本，
        /// 而且删笔记时还容易和原文件搞混。用户明确说过"这不是我要的设计"。
        ///
        /// 现在：只存路径引用（Linked=true）。真正需要实体文件的场景只有一个 —— 拖出到
        /// 外部程序；那时直接把原路径交给系统拖放即可，依然不需要副本。
        /// 如果原文件被移动/删除，界面上会标为失效，而不是留一堆僵尸副本。
        /// </summary>
        public Attachment AddAttachment(Note n, string sourcePath, bool copyIntoLibrary = false)
        {
            string storedPath = sourcePath;
            string origName = Path.GetFileName(sourcePath);

            if (copyIntoLibrary)
            {
                // 仅用于确实需要纳管的场景（如剪贴板粘贴的临时图），默认不走这里
                string dir = Path.Combine(BlobDir, n.Id);
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, origName);
                int i = 1;
                while (File.Exists(target))
                {
                    string baseName = Path.GetFileNameWithoutExtension(origName);
                    string ext = Path.GetExtension(origName);
                    target = Path.Combine(dir, baseName + " (" + (++i) + ")" + ext);
                }
                File.Copy(sourcePath, target, false);
                storedPath = target;
                origName = Path.GetFileName(target);
            }

            long size = 0;
            try { size = Directory.Exists(storedPath) ? 0 : new FileInfo(storedPath).Length; } catch { }

            var a = new Attachment
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = Attachment.KindOf(storedPath),
                OrigName = origName,
                StoredPath = storedPath,
                Size = size,
                Linked = !copyIntoLibrary
            };
            n.Attachments.Add(a);
            Touch(n);
            return a;
        }

        /// <summary>附件实体移进 Windows 回收站（可从系统回收站恢复）。</summary>
        public void DeleteAttachment(Note n, Attachment a, bool physical = true)
        {
            n.Attachments.Remove(a);
            Touch(n);
            // 只有"纳管副本"才删实体；引用模式绝不碰用户的原始文件
            if (physical && !a.Linked)
            {
                try
                {
                    if (File.Exists(a.StoredPath)) Native.RecycleBin.DeleteToRecycleBin(a.StoredPath);
                }
                catch { }
            }
            DeleteThumbFiles(a);
        }

        public void MoveAttachment(Note n, Attachment a, int newIndex)
        {
            int old = n.Attachments.IndexOf(a);
            if (old < 0 || old == newIndex) return;
            n.Attachments.RemoveAt(old);
            if (newIndex < 0) newIndex = 0;
            if (newIndex > n.Attachments.Count) newIndex = n.Attachments.Count;
            n.Attachments.Insert(newIndex, a);
            Touch(n);
        }

        /// <summary>
        /// 拍一张「所有笔记当前顺序」的快照（顺序＝按 Status 再按 Sort 降序表达）。
        /// 拖动前拍、拖错了拿来还原。只存排序键，不动任何内容字段。
        /// </summary>
        public Dictionary<string, long> SnapshotOrder()
        {
            var d = new Dictionary<string, long>();
            foreach (var n in Db.Notes) d[n.Id] = n.SortKey;
            return d;
        }

        /// <summary>把顺序还原回快照。只回写排序键有变化的条目，然后存盘。</summary>
        public void RestoreOrder(Dictionary<string, long> snap)
        {
            if (snap == null) return;
            bool changed = false;
            foreach (var n in Db.Notes)
            {
                long v;
                if (snap.TryGetValue(n.Id, out v) && n.SortKey != v)
                {
                    n.Sort = v;
                    changed = true;
                }
            }
            if (changed) Save();
        }

        /// <summary>
        /// 按给定 id 顺序重排（拖动排序提交用）。只动排序键 Sort，不碰 CreatedAt/内容。
        /// 用整序提交而不是「移到第几」——增量索引在高度不等的列表里容易算错。
        /// </summary>
        public void ApplyOrder(IList<string> orderedIds)
        {
            if (orderedIds == null || orderedIds.Count == 0) return;

            long top = Note.NowMs() + orderedIds.Count + 10;
            var map = new Dictionary<string, long>();
            for (int i = 0; i < orderedIds.Count; i++) map[orderedIds[i]] = top - i;

            bool changed = false;
            foreach (var n in Db.Notes)
            {
                long v;
                if (map.TryGetValue(n.Id, out v) && n.Sort != v)
                {
                    n.Sort = v;
                    changed = true;
                }
            }
            if (changed) Save();
        }

        public void MoveNoteTo(Note n, int newIndex)
        {
            var list = Db.Notes.OrderBy(x => x.Status)
                               .ThenByDescending(x => x.SortKey)
                               .ThenByDescending(x => x.CreatedAt).ToList();
            int old = list.IndexOf(n);
            if (old < 0) return;
            list.RemoveAt(old);
            if (newIndex < 0) newIndex = 0;
            if (newIndex > list.Count) newIndex = list.Count;
            list.Insert(newIndex, n);

            // 用独立排序键表达顺序：从新到旧逐条递减 1ms（不再改写创建时间）
            long top = Note.NowMs() + list.Count + 10;
            foreach (var item in list) item.Sort = top--;
            Save();
        }

        public void SetStatus(Note n, int status)
        {
            n.Status = status;
            // 完成 / 回收站都不保留置顶标记（用户定：已完成或回收站里不需要置顶）
            if (status != 0) n.Pinned = false;
            Touch(n);
        }

        /// <summary>
        /// 永久删除一条笔记：写墓碑（别的机器同步到之后也会删掉，不会再"复活"）、
        /// 删掉这条自己的笔记文件、回收纳管的附件实体。
        /// </summary>
        public void Purge(Note n)
        {
            if (n == null) return;
            foreach (var a in (n.Attachments ?? new List<Attachment>()).ToList())
            {
                if (!a.Linked)
                {
                    try { if (File.Exists(a.StoredPath)) Native.RecycleBin.DeleteToRecycleBin(a.StoredPath); } catch { }
                }
                DeleteThumbFiles(a);
            }
            MarkDeleted(n.Id, Math.Max(Note.NowMs(), n.UpdatedAt));
            DeleteNoteFile(n.Id);
            Db.Notes.Remove(n);
            try
            {
                string dir = Path.Combine(BlobDir, n.Id);
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch { }
        }

        public string ThumbPath(Attachment a)
        {
            return Path.Combine(ThumbDir, a.Id + ".png");
        }

        /// <summary>
        /// 删掉这个附件的全部缩略图缓存。命名随版本换过三次（`&lt;id&gt;.png` → `&lt;id&gt;_v2.png` →
        /// `ico_v3_&lt;id&gt;.png`），只删 `<id>.png` 会让后两种缓存永远留在 thumbs 里。
        /// </summary>
        public void DeleteThumbFiles(Attachment a)
        {
            if (a == null || string.IsNullOrEmpty(a.Id)) return;
            foreach (var name in new[]
            {
                a.Id + ".png",              // 早期
                a.Id + "_v2.png",           // v2680
                "ico_v2_" + a.Id + ".png",  // 旧图标缓存
                "ico_v3_" + a.Id + ".png"   // 现行图标缓存（见 Bridge.IconCachePath）
            })
            {
                try { string p = Path.Combine(ThumbDir, name); if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }

        /// <summary>
        /// 迁移数据目录后，把纳管附件的 StoredPath 改成新根下的同名位置。
        /// 判据必须严格：路径里含 `\blobs\`、且其后第一段就是本条笔记的 Id —— 否则
        /// 用户自己的目录里恰好有 blobs 字样也会被误改。引用模式（Linked）一律不动。
        /// </summary>
        public static void RepathAttachments(Database db, string newRoot)
        {
            if (db == null || db.Notes == null) return;
            string blobNew = Path.Combine(newRoot, "blobs");
            foreach (var n in db.Notes)
            {
                if (n == null || n.Attachments == null) continue;
                foreach (var a in n.Attachments)
                {
                    if (a == null || a.Linked || string.IsNullOrEmpty(a.StoredPath)) continue;
                    int i = a.StoredPath.ToLowerInvariant().IndexOf("\\blobs\\");
                    if (i < 0) continue;
                    string rel = a.StoredPath.Substring(i + 7);
                    int sep = rel.IndexOf('\\');
                    if (sep <= 0) continue;
                    if (!string.Equals(rel.Substring(0, sep), n.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    string cand = Path.Combine(blobNew, rel);
                    if (File.Exists(cand)) a.StoredPath = cand;
                }
            }
        }

        /// <summary>
        /// 按「本条笔记的 blobs 子目录 + 文件名」修掉失效的绝对路径。
        /// 数据目录搬过家、或从另一台机器同步过来的 notes.json，路径都会指向别处；
        /// 不清掉的话缩略图打不开、清空回收站也删不掉实体。
        /// </summary>
        public int RepairAttachmentPaths()
        {
            int fixedCount = 0;
            foreach (var n in Db.Notes)
            {
                if (n == null || n.Attachments == null) continue;
                foreach (var a in n.Attachments)
                {
                    if (a == null || a.Linked || string.IsNullOrEmpty(a.StoredPath)) continue;
                    if (File.Exists(a.StoredPath)) continue;
                    string cand = Path.Combine(BlobDir, n.Id, Path.GetFileName(a.StoredPath));
                    if (File.Exists(cand)) { a.StoredPath = cand; fixedCount++; }
                }
            }
            if (fixedCount > 0) Save();
            return fixedCount;
        }

        /// <summary>
        /// 找出 blobs / thumbs 里不属于任何笔记的文件（删笔记、迁移、异常退出都会留下）。
        /// 判据：附件当前可达的 StoredPath、或按「blobs\&lt;noteId&gt;\文件名」能对上的，都算被占用。
        /// </summary>
        public List<string> FindOrphanFiles()
        {
            var list = new List<string>();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var n in Db.Notes)
            {
                if (n == null) continue;
                foreach (var a in (n.Attachments ?? new List<Attachment>()))
                {
                    if (a == null) continue;
                    if (!string.IsNullOrEmpty(a.StoredPath))
                    {
                        try { keep.Add(Path.GetFullPath(a.StoredPath)); } catch { }
                        try { keep.Add(Path.GetFullPath(Path.Combine(BlobDir, n.Id, Path.GetFileName(a.StoredPath)))); } catch { }
                    }
                    if (!string.IsNullOrEmpty(a.Id))
                    {
                        keep.Add(Path.Combine(ThumbDir, a.Id + ".png"));
                        keep.Add(Path.Combine(ThumbDir, a.Id + "_v2.png"));
                        keep.Add(Path.Combine(ThumbDir, "ico_v2_" + a.Id + ".png"));
                        keep.Add(Path.Combine(ThumbDir, "ico_v3_" + a.Id + ".png"));
                    }
                }
            }

            try
            {
                foreach (var f in Directory.GetFiles(BlobDir, "*", SearchOption.AllDirectories))
                {
                    string full;
                    try { full = Path.GetFullPath(f); } catch { continue; }
                    if (!keep.Contains(full)) list.Add(f);
                }
            }
            catch { }

            try
            {
                foreach (var f in Directory.GetFiles(ThumbDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string full;
                    try { full = Path.GetFullPath(f); } catch { continue; }
                    if (!keep.Contains(full)) list.Add(f);
                }
            }
            catch { }

            return list;
        }

        /// <summary>永久删除所有无主文件，返回删掉的个数与字节数。</summary>
        public int PurgeOrphans(out long bytes)
        {
            bytes = 0;
            int n = 0;
            foreach (var f in FindOrphanFiles())
            {
                try
                {
                    long sz = new FileInfo(f).Length;
                    File.Delete(f);
                    bytes += sz;
                    n++;
                }
                catch { }
            }
            // 顺手收掉 blobs 下变空的笔记目录
            try
            {
                foreach (var d in Directory.GetDirectories(BlobDir))
                {
                    try { if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d); } catch { }
                }
            }
            catch { }
            return n;
        }

    }
}

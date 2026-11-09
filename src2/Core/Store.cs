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

        // —— 双机共用一个 OneDrive 目录时的冲突检测 ——
        private long _loadedRev;                // 内存这份对应的磁盘 rev
        private DateTime _loadedWriteUtc;       // 载入时磁盘文件的最后写入时间
        private bool _saving;                   // 防止 Save ↔ Merge 互相递归

        private static readonly DataContractJsonSerializer DbSer =
            new DataContractJsonSerializer(typeof(Database));
        private static readonly DataContractJsonSerializer SetSer =
            new DataContractJsonSerializer(typeof(Settings));

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
                string targetDbPath = Path.Combine(newRoot, "notes.json");

                // ---- 1) 目标目录已有数据 ----
                Database target = null;
                try { if (File.Exists(targetDbPath)) target = LoadJson<Database>(targetDbPath, DbSer); } catch { }
                if (target == null) target = new Database();
                if (target.Notes == null) target.Notes = new List<Note>();

                // ---- 2) 合并去重 ----
                var byId = new Dictionary<string, Note>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in target.Notes) if (n != null && !string.IsNullOrEmpty(n.Id)) byId[n.Id] = n;

                int added = 0, dup = 0, mergedAtt = 0;
                foreach (var src in Db.Notes)
                {
                    if (src == null || string.IsNullOrEmpty(src.Id)) continue;
                    if (src.Attachments == null) src.Attachments = new List<Attachment>();

                    Note exist;
                    if (!byId.TryGetValue(src.Id, out exist))
                    {
                        target.Notes.Add(src); byId[src.Id] = src; added++;
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
                    if (ReferenceEquals(newer, src))
                    {
                        int idx = target.Notes.IndexOf(exist);
                        if (idx >= 0) target.Notes[idx] = src;
                        byId[src.Id] = src;
                    }
                }

                // ---- 3) 附件实体 / 缩略图 ----
                int copied = 0, skipped = 0;
                CopyTreeInto(BlobDir, Path.Combine(newRoot, "blobs"), ref copied, ref skipped);
                CopyTreeInto(ThumbDir, Path.Combine(newRoot, "thumbs"), ref copied, ref skipped);

                // 目标没有设置文件时，把本机设置带过去（有就以目标的为准）
                string targetSet = Path.Combine(newRoot, "settings.json");
                try
                {
                    if (!File.Exists(targetSet) && File.Exists(Path.Combine(old, "settings.json")))
                        File.Copy(Path.Combine(old, "settings.json"), targetSet, false);
                }
                catch { }

                // ---- 4) 落盘并校验 ----
                // 关键：写盘前先把纳管附件的绝对路径改到新根下。不重写的话，迁移完成后
                // 所有附件都指向已被清空的旧目录 —— 缩略图打不开、清空回收站也删不掉实体
                // （File.Exists 为 false，删除分支被跳过，新目录里就积一堆永久死文件）。
                RepathAttachments(target, newRoot);
                WriteAtomic(targetDbPath, DbSer, target);
                if (!File.Exists(targetDbPath)) { msg = "写入新目录失败"; return false; }
                Database check = LoadJson<Database>(targetDbPath, DbSer);
                int want = target.Notes.Count, got = (check != null && check.Notes != null) ? check.Notes.Count : -1;
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
            foreach (var dir in new[] { "blobs", "thumbs", "backup" })
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
            Db = LoadJson<Database>(DbPath, DbSer) ?? new Database();
            Settings = LoadJson<Settings>(Path.Combine(Root, "settings.json"), SetSer) ?? new Settings();
            if (Db.Notes == null) Db.Notes = new List<Note>();
            foreach (var n in Db.Notes)
            {
                if (n.Attachments == null) n.Attachments = new List<Attachment>();
                if (n.Text == null) n.Text = "";
                // 旧档没有 sort 字段：用创建时间补，顺序与旧版按 CreatedAt 排完全一致
                if (n.Sort == 0) n.Sort = n.CreatedAt;
            }
            _loadedRev = Db.Rev;
            try { _loadedWriteUtc = File.Exists(DbPath) ? File.GetLastWriteTimeUtc(DbPath) : DateTime.MinValue; }
            catch { _loadedWriteUtc = DateTime.MinValue; }
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
            // 写之前先看磁盘是不是被另一台机器改过 —— 直接覆盖会把它那边的改动整份吃掉。
            if (!_saving)
            {
                _saving = true;
                try { MergeFromDisk(); } catch { } finally { _saving = false; }
            }
            Db.Rev = Math.Max(Db.Rev, _loadedRev) + 1;
            WriteAtomic(DbPath, DbSer, Db);
            _loadedRev = Db.Rev;
            try { _loadedWriteUtc = File.Exists(DbPath) ? File.GetLastWriteTimeUtc(DbPath) : DateTime.MinValue; }
            catch { _loadedWriteUtc = DateTime.MinValue; }
        }

        /// <summary>
        /// 磁盘上的 notes.json 比内存这份新时，按 id 合并进来（同 id 取 UpdatedAt 新的，
        /// 磁盘多出来的加入，内存多出来的保留）。只改内存，不写盘 —— 由调用方决定何时落盘。
        /// 返回是否真的合进了东西。**不处理删除**：另一台删掉的条目在这里会“复活”，
        /// 这是本期已知取舍（见 versions/v2026-11-09-2762/README.md）。
        /// </summary>
        public bool MergeFromDisk(bool force = false)
        {
            // 注意：这里不能判 _saving —— Save() 会在 _saving=true 的情况下调用本方法。
            // 本方法只改内存、自己从不落盘，不存在 Save↔Merge 递归。
            DateTime diskTime;
            try
            {
                if (!File.Exists(DbPath)) return false;
                diskTime = File.GetLastWriteTimeUtc(DbPath);
            }
            catch { return false; }

            if (!force && diskTime <= _loadedWriteUtc) return false;   // 磁盘没动过，快路径

            Database disk = null;
            try { disk = LoadJson<Database>(DbPath, DbSer); } catch { }
            if (disk == null || disk.Notes == null) return false;

            if (!force && disk.Rev <= _loadedRev)
            {
                // 文件被动过（OneDrive 刷新元数据）但内容代数没前进，收下时间戳就走
                _loadedWriteUtc = diskTime;
                return false;
            }

            int added = 0, replaced = 0;
            var byId = new Dictionary<string, Note>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in Db.Notes) if (n != null && !string.IsNullOrEmpty(n.Id)) byId[n.Id] = n;

            foreach (var dn in disk.Notes)
            {
                if (dn == null || string.IsNullOrEmpty(dn.Id)) continue;
                if (dn.Attachments == null) dn.Attachments = new List<Attachment>();
                if (dn.Text == null) dn.Text = "";
                if (dn.Sort == 0) dn.Sort = dn.CreatedAt;

                Note mine;
                if (!byId.TryGetValue(dn.Id, out mine)) { Db.Notes.Add(dn); byId[dn.Id] = dn; added++; continue; }
                if (dn.UpdatedAt > mine.UpdatedAt)
                {
                    int idx = Db.Notes.IndexOf(mine);
                    if (idx >= 0) Db.Notes[idx] = dn;
                    byId[dn.Id] = dn;
                    replaced++;
                }
            }

            _loadedRev = Math.Max(_loadedRev, disk.Rev);
            _loadedWriteUtc = diskTime;
            return added + replaced > 0;
        }

        /// <summary>外面（窗口激活时）用：磁盘有了新东西就合并并落盘，返回是否有变化。</summary>
        public bool PullFromDisk()
        {
            bool changed;
            try { changed = MergeFromDisk(force: true); } catch { return false; }
            if (changed) Save();
            return changed;
        }

        public void SaveSettings()
        {
            WriteAtomic(Path.Combine(Root, "settings.json"), SetSer, Settings);
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

        /// <summary>每次启动备份一份，只留最近 7 份。</summary>
        public void BackupOnStart()
        {
            if (!File.Exists(DbPath)) return;
            try
            {
                string name = "notes-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
                File.Copy(DbPath, Path.Combine(BackupDir, name), true);
                var old = new DirectoryInfo(BackupDir).GetFiles("notes-*.json")
                    .OrderByDescending(f => f.CreationTimeUtc).Skip(7).ToList();
                foreach (var f in old) { try { f.Delete(); } catch { } }
            }
            catch { }
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

        public void Purge(Note n)
        {
            foreach (var a in n.Attachments.ToList())
            {
                if (!a.Linked)
                {
                    try { if (File.Exists(a.StoredPath)) Native.RecycleBin.DeleteToRecycleBin(a.StoredPath); } catch { }
                }
                DeleteThumbFiles(a);
            }
            Db.Notes.Remove(n);
            try
            {
                string dir = Path.Combine(BlobDir, n.Id);
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch { }
            Save();
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

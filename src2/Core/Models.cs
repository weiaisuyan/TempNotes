using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TempNotes
{
    [DataContract]
    public class Attachment
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "kind", Order = 1)] public string Kind { get; set; }      // image | video | file
        [DataMember(Name = "orig", Order = 2)] public string OrigName { get; set; }
        [DataMember(Name = "stored", Order = 3)] public string StoredPath { get; set; }
        [DataMember(Name = "size", Order = 4)] public long Size { get; set; }
        /// <summary>true = 只引用原路径（默认）；false = 已复制进库（仅剪贴板临时文件等场景）。</summary>
        [DataMember(Name = "linked", Order = 5)] public bool Linked { get; set; }

        public static string KindOf(string path)
        {
            string e = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (e)
            {
                case ".jpg": case ".jpeg": case ".png": case ".gif": case ".bmp":
                case ".webp": case ".ico": case ".tif": case ".tiff": case ".heic": case ".avif":
                    return "image";
                case ".mp4": case ".mkv": case ".avi": case ".mov": case ".wmv": case ".flv":
                case ".webm": case ".m4v": case ".mpg": case ".mpeg": case ".ts": case ".rmvb": case ".3gp":
                    return "video";
                default:
                    return "file";
            }
        }
    }

    [DataContract]
    public class Note
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "text", Order = 1)] public string Text { get; set; }
        [DataMember(Name = "color", Order = 2)] public string Color { get; set; }
        [DataMember(Name = "status", Order = 3)] public int Status { get; set; }   // 0=待办 1=完成 2=回收站
        [DataMember(Name = "created", Order = 4)] public long CreatedAt { get; set; }
        [DataMember(Name = "updated", Order = 5)] public long UpdatedAt { get; set; }
        [DataMember(Name = "atts", Order = 6)] public List<Attachment> Attachments { get; set; }
        /// <summary>
        /// 排序键（越大越靠前）。拖动排序只写这个字段。
        /// 旧版本直接改写 CreatedAt 当排序键，结果每拖一次，「建 …」时间全变成刚刚。
        /// 旧数据没有这个字段时为 0，Load 时会用 CreatedAt 补上，顺序与旧版完全一致。
        /// </summary>
        [DataMember(Name = "sort", Order = 7)] public long Sort { get; set; }

        /// <summary>置顶：只有 status=0（未完成）时有效。完成/进回收站都会清掉（Store.SetStatus 统一处理）。</summary>
        [DataMember(Name = "pinned", Order = 8)] public bool Pinned { get; set; }

        public Note()
        {
            Id = Guid.NewGuid().ToString("N");
            Text = "";
            Color = "";
            Status = 0;
            CreatedAt = UpdatedAt = Sort = NowMs();
            Attachments = new List<Attachment>();
        }

        public static long NowMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        /// <summary>排序键：新数据用 Sort，旧数据（无 sort 字段）退回 CreatedAt。</summary>
        public long SortKey { get { return Sort != 0 ? Sort : CreatedAt; } }

        public bool IsDone { get { return Status == 1; } }
        public bool IsTrash { get { return Status == 2; } }
    }

    /// <summary>
    /// 删除墓碑（v2810 引入）。永久删除一条笔记时写一个 deleted\&lt;id&gt;.json，
    /// 另一台机器同步到之后按它把本地还留着的同 id 笔记删掉 —— 旧版只能"加回来"，删不掉。
    /// </summary>
    [DataContract]
    public class Tombstone
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        /// <summary>删除时刻（ms）。更新时间早于它的同 id 笔记一律视为已删。</summary>
        [DataMember(Name = "at", Order = 1)] public long At { get; set; }
    }

    [DataContract]
    public class Database
    {
        [DataMember(Name = "version", Order = 0)] public int Version { get; set; }
        [DataMember(Name = "notes", Order = 1)] public List<Note> Notes { get; set; }

        /// <summary>
        /// 保存代数，每写一次 +1。两台机器共用同一个 OneDrive 目录时，靠它判断
        /// 磁盘上的那份是不是比本机内存里的新（旧档没有这个字段 = 0）。
        /// v2810 起数据拆成一注一文件，这个字段只为兼容旧档/备份保留，不再参与判断。
        /// </summary>
        [DataMember(Name = "rev", Order = 2)] public long Rev { get; set; }

        /// <summary>墓碑列表（只在旧格式备份里出现；正式存储是 deleted 目录下的每个文件）。</summary>
        [DataMember(Name = "deleted", Order = 3)] public List<Tombstone> Deleted { get; set; }

        public Database()
        {
            Version = 1;
            Notes = new List<Note>();
            Deleted = new List<Tombstone>();
        }
    }

    /// <summary>
    /// 优先级名称（v2813）。一个颜色一个名字，名字属于「颜色本身」——同色的笔记共用一个名字，
    /// 显示在待办条左上角，也拿来筛选。
    /// 单独存 labels.json，不塞进 settings.json：settings.json 是本机的窗口/主题这类偏好，
    /// 而名字属于数据，要和笔记一起随数据目录跨端同步；哪天换回旧版 exe，也不会被旧版整份覆盖掉。
    /// </summary>
    [DataContract]
    public class LabelFile
    {
        [DataMember(Name = "v", Order = 0)] public int Version { get; set; }
        [DataMember(Name = "labels", Order = 1)] public List<string> Labels { get; set; }
        // 筛选条里 chip 的先后顺序：0..9 的一个排列。空/缺失 = 按颜色本身的顺序
        [DataMember(Name = "order", Order = 2)] public List<int> Order { get; set; }

        public LabelFile()
        {
            Version = 1;
            Labels = new List<string>();
            Order = new List<int>();
        }
    }

    [DataContract]
    public class Settings
    {
        [DataMember(Name = "hotkeyMode", Order = 0)] public string HotkeyMode { get; set; }   // "combo" | "doubletap" | "none"
        [DataMember(Name = "hotkeyCombo", Order = 1)] public string HotkeyCombo { get; set; }  // e.g. "Ctrl+Alt+N"
        [DataMember(Name = "doubleTapKey", Order = 2)] public string DoubleTapKey { get; set; } // "Ctrl" | "Alt" | "Shift"
        [DataMember(Name = "autoStart", Order = 3)] public bool AutoStart { get; set; }
        [DataMember(Name = "topmost", Order = 4)] public bool Topmost { get; set; }
        [DataMember(Name = "theme", Order = 5)] public string Theme { get; set; }             // "dark" | "light"
        [DataMember(Name = "winW", Order = 6)] public double WinW { get; set; }
        [DataMember(Name = "winH", Order = 7)] public double WinH { get; set; }
        [DataMember(Name = "palette", Order = 8)] public int PaletteIndex { get; set; }
        [DataMember(Name = "glass", Order = 9)] public bool Glass { get; set; }
        [DataMember(Name = "glassAlpha", Order = 10)] public double GlassAlpha { get; set; }
        // 右键点在待办条空白处 = 切换完成（等同点左边的圈）
        [DataMember(Name = "rightDone", Order = 11)] public bool RightDone { get; set; }
        // 关闭窗口时收进托盘（默认 false = 关闭即退出程序）
        [DataMember(Name = "closeToTray", Order = 12)] public bool CloseToTray { get; set; }

        // DataContractJsonSerializer 反序列化不会调用构造函数，
        // 老 settings.json 里没有 rightDone 时会变成 false；这里先铺默认值再覆盖。
        [System.Runtime.Serialization.OnDeserializing]
        private void BeforeLoad(System.Runtime.Serialization.StreamingContext ctx)
        {
            RightDone = true;
        }

        public Settings()
        {
            HotkeyMode = "combo";
            HotkeyCombo = "Ctrl+Alt+N";
            DoubleTapKey = "Ctrl";
            AutoStart = false;
            Topmost = false;
            Theme = "dark";
            WinW = 460;
            WinH = 720;
            PaletteIndex = 9;   // 默认：深墨绿
            Glass = true;
            RightDone = true;
            GlassAlpha = 0.72;  // 窗口底色不透明度（越小越透）
        }
    }
}

# 随记 · TempNotes

Windows 桌面临时笔记工具。WPF + WebView2 外壳，界面全部用 HTML/CSS/JS。

随手记一条，拖一拖排顺序，拖完划掉。没有账户、没有云、没有同步服务器 —— 数据就是你指定的一个文件夹。

## 功能

- **待办 + 已完成双区**，拖动排序、跨区拖动
- **附件**：图片 / 视频 / 文档，缩略图网格、彩色标记、删除角标
- **拖文件进来**：资源管理器直接拖，落到卡片或输入框
- **回收站**：软删除，可还原，清空后物理删除
- **搜索过滤**：顶部输入框，拖动排序在筛选态下也正确
- **批量编辑**：多选、批量改色 / 删除 / 移动
- **16 套配色**（深色为主），窗口置顶 / 开机自启 / 收进托盘
- **数据目录可换**：指向 OneDrive 文件夹即可双机共用，保存时按 `rev` + `UpdatedAt` 合并
- **本地优先**：所有数据都是本地 JSON + 文件，无网络请求

## 技术栈

| 层 | 技术 |
|---|---|
| 外壳 | WPF (.NET Framework 4.8, x64) |
| 界面 | HTML + CSS + 原生 JS（无框架） |
| 内嵌浏览器 | Microsoft Edge WebView2 |
| 拖动排序 | SortableJS（forceFallback 模式） |

## 编译

需要：Visual Studio 2019+ 或 MSBuild 16+、.NET Framework 4.8 开发包、x64。

```bash
cd src2
MSBuild TempNotes2.csproj -p:Configuration=Release -p:Platform=x64
```

产物在 `src2/bin/Release/TempNotes2.exe`。

**关于 `src2/refs/`**：这个仓库不含它（154MB 的 .NET Framework 引用程序集）。本机编译时把
`C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\` 下的
`WindowsBase.dll / PresentationCore.dll / PresentationFramework.dll` 等拷进 `src2/refs/` 即可，
或者把 csproj 里的 `HintPath` 改成引用程序集的实际路径。

**前端**：`src2/web/` 是编译时的资源输入（会复制到输出目录），`web/` 是部署目录里正在跑的那份。
改完前端要两边同步，否则编译出来的 exe 带的还是旧页面。

## 数据

默认数据目录在 `%LOCALAPPDATA%\TempNotes2`（可在设置里改）。

```
notes.json     笔记与附件元数据（含 rev 版本号，用于多端合并）
blobs/<id>/    附件原文件
thumbs/        缩略图
settings.json  配色、置顶、开机自启等
```

## 已知限制

- 双机共用目录时**删除不同步**：A 机删除的笔记，B 机保存时可能被写回来（需要删除墓碑才能做对，未实现）
- 超高卡片（超过一屏）的拖动体验受几何限制：卡片顶部最多到列表底附近，再往下靠「拖出区外 → 落到末尾」
- 第三方输入法（如豆包的语音输入）在 WebView2 里失效时，通常是 Windows TSF 会话卡死，重启 `ctfmon.exe` 即可，与本程序无关

## 许可

个人项目，未声明开源许可。默认保留所有权利。

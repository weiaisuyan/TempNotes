# 随记 · TempNotes

Windows 桌面临时笔记工具。WPF + WebView2 外壳，界面全部用 HTML/CSS/JS。

随手记一条，拖一拖排顺序，拖完划掉。没有账户、没有云、没有同步服务器 —— 数据就是你指定的一个文件夹。

## 功能

- **待办 + 已完成双区**，拖动排序、跨区拖动（跨区松手按方向落到本区与目标区相邻的一侧）
- **附件**：图片 / 视频 / 文档，缩略图网格、彩色标记、删除角标
- **拖文件进来**：资源管理器直接拖，落到卡片或输入框
- **拖文字 / 链接进来**：从网页、记事本、Word 选中文字或链接拖进来，落卡片上追加、落空白处新建
- **粘贴链接自动转 Markdown**：整段粘贴只是一条链接时，自动落成 `[标题](链接)`
- **回收站**：软删除，可还原，清空后物理删除
- **搜索过滤**：顶部输入框，拖动排序在筛选态下也正确
- **批量编辑**：多选、批量改色 / 删除 / 移动
- **16 套配色**（深色为主），窗口置顶 / 开机自启 / 收进托盘
- **数据目录可换**：指向 OneDrive 文件夹即可双机共用；一条笔记一个文件，删除用墓碑表达，目录一变就自动重绘
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
MSBuild TempNotes2.csproj -p:Configuration=Release -p:Platform=x64 "/p:FrameworkPathOverride=$(Resolve-Path .\refs)"
```

产物在 `src2/bin/Release/随记.exe`。

> 机器上没装 .NET Framework 4.8 目标包时会报 `MSB3644: 找不到 .NETFramework,Version=v4.8 的引用程序集`，
> 上面那句 `FrameworkPathOverride` 就是让编译直接吃 `src2/refs/` 里那份引用程序集。

**关于 `src2/refs/`**：这个仓库不含它（154MB 的 .NET Framework 引用程序集）。本机编译时把
`C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\` 下的
`WindowsBase.dll / PresentationCore.dll / PresentationFramework.dll` 等拷进 `src2/refs/` 即可，
或者把 csproj 里的 `HintPath` 改成引用程序集的实际路径。

**前端**：`src2/web/` 是编译时的资源输入（会复制到输出目录），`web/` 是部署目录里正在跑的那份。
改完前端要两边同步，否则编译出来的 exe 带的还是旧页面。

## 数据

默认数据目录在 `%LOCALAPPDATA%\TempNotes2`（可在设置里改）。

```
notes/<id>.json     一条笔记一个文件（v2810 起）
deleted/<id>.json   删除墓碑，记录"这条在哪一刻被删了"
backup/             启动时自动备份，保留 7 份
blobs/<id>/         附件原文件
thumbs/             缩略图
settings.json       配色、置顶、开机自启等
```

**为什么一条一个文件**：双机共用一个 OneDrive 目录时，单文件 `notes.json` 是两台机器同时写同一份，
本质上是竞态。拆成一条一个文件后，冲突范围从"整个库"缩到"同一条笔记"，写盘也只写真正改过的那几条。

**合并规则**：按 `id` 比对，取 `updated` 更新的一方；`deleted/` 里有墓碑且删除时间不早于该条 `updated` 的，
两个端都会把它删掉（旧版做不到，删除会"复活"）。

**升级**：首次用 v2810 打开旧数据目录时，会自动把 `notes.json` 拆成 `notes/<id>.json`，
原文件改名 `notes.json.migrated-<时间戳>` 留档，不删。

**实时**：程序盯着数据根目录（含 `notes/`、`deleted/` 子目录），另一台改了目录，这边立刻合并重绘，不用重启；
监听万一失聪还有 1 秒兜底轮询（窗口最小化时降到 5 秒）。你正在打字、拖卡片、输入法组词时，重绘会自动推迟到松手/失焦之后。

## 版本记录

- **v2827**（2026-11-10）优先级名称 = 标签：设置里给 10 个优先级分别起名（最多 5 个字，留空不显示），同一颜色的待办自动在卡片左上角显示同名书签，颜色跟优先级一致，不占正文位置；名字存在数据目录的 labels.json，两端共用。新增常驻筛选条：有名字的颜色各一颗 chip，点一下只看该优先级，chip 上的数字只数没完成的，0 条仍显示但变暗；chip 可按住拖动换顺序（跟手拖影 + 让位动画 + 贴边自动滚动 + Esc 取消），顺序同样存在 labels.json 并两端同步。筛选是"视图"不是分组：里面能勾完成、置顶、改色（改到别的颜色会移出视图并提示）、编辑、删除、新建（继承当前筛选色），只禁拖拽排序（会提示先点「全部」）；切筛选清空已选、有淡入动画、不弹 toast；批量「全选」只选当前视图。
- **v2812**（2026-11-10）同步提速：文件监听改为「首个事件立刻重绘」，兜底轮询从 4 秒收到 1 秒（窗口最小化时降到 5 秒）；重绘遇到你正在打字、拖卡片、输入法组词时自动推迟，松手/失焦后补上。实测另一台的文件落地后 20–30ms 就上屏。
- **v2811**（2026-11-10）修「另一台加/删的笔记这边不刷新」：文件监听原先只盯 `notes/` 目录，OneDrive 整目录替换会让它悄悄失聪；改为监听数据根目录 + 出错自动重建 + 4 秒兜底轮询，双机同步最多 4 秒内可见，不用再重启。
- **v2810**（2026-11-10）数据模型重构：一条笔记一个文件 + 删除墓碑 + 数据目录实时监听。
  解决了双机共用目录时"删了又复活"和"要重启才看到"两个老问题；旧单文件数据自动迁移并留档。
- **v2799** 跨区飞行动画按距离变速（420–1200ms），不再固定 620ms。
- **v2798** 文字 / 链接拖入、粘贴链接自动转 Markdown、拖拽改为复制、松手后提示立即消失。
- **v2797** 修「收进托盘后点图标唤不回」（命名事件唤醒 + 允许前台化）。
- **v2796** 窗口置顶移入标题栏（图钉带按压反馈、颜色跟随配色）；跨区拖放按方向落位。
- **v2795 及更早** 见 `versions/` 下的快照说明。

## 发版清单（每次发版必做，别只打 tag）

1. 改版本号：`MainWindow.xaml.cs` 的启动 banner、`web/app.js` 的 `APPJS_VER`。
2. 编译：`src2` 下 MSBuild（见上文），确认 `随记.exe` 与 `web/` 都是新产物。
3. 留快照：`versions/v<日期>-<版本号>/`，放源码 + web + exe。
4. 打发布包：`_release/TempNotes-<版本号>/` 全量文件 + `TempNotes-<版本号>-win-x64.zip`。
5. README「版本记录」补一条：这一版改了什么、影响什么。
6. git：`git add -A` → commit → `git tag v<版本号>` → `git push origin main` → `git push origin v<版本号>`。
7. **在 GitHub 建 Release 并上传 zip**：
   `POST /repos/weiaisuyan/TempNotes/releases`（`tag_name`、`name` 写「随记 v<版本号>」、`body` 写本版说明、最新那版带 `make_latest=true`），
   再 `POST https://uploads.github.com/repos/weiaisuyan/TempNotes/releases/<id>/assets?name=TempNotes-<版本号>-win-x64.zip` 传包。
   **只 push tag 不建 Release，网页 Releases 就会一直停在旧版本**（2026-11-10 就因此停在 v2795，用户发现后才补）。
8. 两端安装最新版：笔记本 `D:\程序\TempNotes`、台式机 `%LOCALAPPDATA%\Programs\TempNotes`，装前备份 `随记.exe` 与 `web`。

## 已知限制

- 双机同用一个目录时，如果两边**同时**编辑同一条笔记，仍是"后写覆盖先写"（按 `updated` 取新）。
  真要长期双机并行，建议换 Syncthing（会生成 `*.sync-conflict` 副本，不互相覆盖）。
- **同步速度的下限在 OneDrive 和网速，不在程序**：程序侧检出并上屏是 20–30ms，但 OneDrive 把文件从一台传到另一台
  实测约 5 秒。要更快只能换同步方式（Syncthing / 局域网共享 / 只用一台）。
- **剪贴板被别的程序独占时，复制/剪切会失败**：典型是网易 UU 远程开着「剪贴板同步」（窗口类 `UURemoteClipboard`），
  `OpenClipboard` 会一直返回 `err=5`，随记写入剪贴板会提示"剪贴板被其他程序占用"。退出该程序或关掉它的剪贴板同步即可。
- 超高卡片（超过一屏）的拖动体验受几何限制：卡片顶部最多到列表底附近，再往下靠「拖出区外 → 落到末尾」
- **微信的链接卡片拖不进来**：微信发起拖拽时在系统层就没交出任何数据（实测 `dataTransfer` 里 0 种格式、0 个文件，
  拖到桌面或记事本同样什么都没掉下来）。用「复制 → 粘贴」代替，粘贴会自动转成 Markdown 链接。
- 第三方输入法（如豆包的语音输入）在 WebView2 里失效时，通常是 Windows TSF 会话卡死，重启 `ctfmon.exe` 即可，与本程序无关

## 许可

个人项目，未声明开源许可。默认保留所有权利。

# Changelog

All notable changes to **PinDock** will be documented in this file.

## [0.1.12] - 2026-11-11

### Fixed
- **修复「贴边自动隐藏」模式下 Dock 无法唤出**：触发条（`EdgeHandleWindow`）虽然带有 `WS_EX_TOPMOST`，但窗口一旦因全屏应用或其它置顶窗口掉出顶层窗口带，WPF 的 `Topmost` 属性赋值在值未变化时不会重新调用 `SetWindowPos`，`Show()` 也不会自动恢复层级，导致这条 3px 高的触发条被普通窗口盖住，既不可见也收不到鼠标悬停，Dock 收起后无法再唤出。现在每次显示触发条都会显式重新置顶，Dock 本体显示时也做同样处理。

---

## [0.1.11] - 2026-11-10

### Added
- **macOS 风格系统强调色**：新增「系统蓝 / 梦幻紫 / 樱花粉 / 活力橙 / 清爽绿 / 中性灰」6 套 macOS 配色，采用 macOS 中性窗口底色（浅色 `#F5F5F7`、深色 `#1C1C1E`）搭配系统强调色；文件夹图标、开关与选中态会跟随强调色着色，不再是固定暖金色。浅色与深色均有独立取值，可在「外观」页与原有 5 套配色自由切换。
- **Dock 玻璃材质升级**：Dock 与收藏组弹层的半透明底色由纯色改为纵向渐变，模拟 macOS 毛玻璃材质的顶部高光与底部沉淀；设置页与弹窗主按钮加入轻微高光与投影，窗口背景增加柔和的纵向明暗过渡。

---

## [0.1.10] - 2026-11-10

### Added
- **支持添加 Microsoft Store / MSIX 应用（如 Claude Desktop）**：这类应用没有可访问的 `.exe` / `.lnk` 文件路径，此前不会出现在「本机软件库」。现在 `InstalledAppsService` 额外枚举 shell 的 `AppsFolder` 虚拟文件夹，仅收录带 `PackageFamilyName!ApplicationId` 的打包应用，并以 `shell:AppsFolder\<AUMID>` 作为收藏路径；收藏项通过 ShellExecute 激活启动，图标改由 `IShellItemImageFactory` 提取，同时按「应用」类型参与 Dock 排序与分组。对于没有资源管理器定位意义的打包应用，隐藏「在资源管理器中显示」入口，避免无效操作。
- **「添加收藏」支持搜索本机已安装软件**：将原先只能滚动查找的下拉框改为带搜索框的候选列表，输入关键词即时过滤（支持空格分隔多个关键词），可用 `↑` / `↓` 选择、`Enter` 确认、`Esc` 或点击空白处关闭，选中后自动回填名称与路径；Microsoft Store 应用以「Microsoft Store 应用」标注，不再显示 `shell:AppsFolder` 内部路径。

### Changed
- **Dock 固定项改为 macOS 风格的容量自适应**：移除固定 9 个固定项的硬上限，改为按屏幕可用宽度、DPI、是否显示图标标签、分组与功能按钮数量计算可见数量；空间不足时先将图标从用户设置尺寸逐级缩小到 32px 下限，仍放不下才把末尾项目移入「更多」。因此新增第 10 个及更多固定应用时会直接显示在 Dock 上，不再一固定就落入「更多」。

---

## [0.1.9] - 2026-11-05

### Fixed
- **修复悬停应用图标时进程无提示崩溃**：`ShellLinkParser` 解析 JumpList（`*.automaticDestinations-ms` / `*.customDestinations-ms`）内嵌 `.lnk` 时，仅检查 `IDListSize` 是否落在文件范围内，就把原始字节当作 PIDL 传给 `SHGetPathFromIDListW`。容器文件中存在大量畸形/截断的 `LinkTargetIDList`（单项长度越过声明范围，或缺少 2 字节 0 结尾），shell32 会沿伪造的长度链越界解引用，抛出无法被 `catch` 捕获的 `ExecutionEngineException`（事件日志 `0xc0000005` / `HResult 0x80131506`），进程随即消失。现在先按 ItemID 的 `cb` 链校验整条 IDList 自洽并以 0 结尾，再复制到带显式结尾符的独立缓冲区交给 shell32；实测本机 185 个 JumpList 文件（含 35 个畸形候选）全部安全解析。
- **新增未处理异常落盘日志**：`CrashLog` 将 UI 线程、后台线程与未观察任务的异常追加到 `%LOCALAPPDATA%\PinDock\logs\crash.log`；UI 事件回调异常记录后不再直接终止常驻进程，便于后续定位「无提示退出」。

---

## [0.1.8] - 2026-11-04

### Fixed
- **修复锁屏 / UAC 安全桌面后 PinDock 进程崩溃**：`AutoHideController` 的 40ms 轮询把 `GetCursorPos` 失败直接 `throw`，异常抛出 `DispatcherTimer.FireTick` 后成为未处理异常并终止进程（事件日志 `System.InvalidOperationException: GetCursorPos 失败`，0.1.6 / 0.1.7 各累计多次）。输入桌面归属其他会话时无法定位光标，现在跳过本次轮询、下一拍重试。
- **修复单实例互斥体在退出时抛异常**：命中 `Local\PinDock.SingleInstance` 的第二实例走 `Shutdown()` 后也会调用 `ReleaseMutex`，触发 `ApplicationException: Object synchronization method was called from an unsynchronized block of code`。现只有创建者释放互斥体。

---

## [0.1.7] - 2026-11-02

### Added & Improved
- **通用「应用最近打开预览」引擎 (`AppRecentService` + `FolderPopupWindow`)**：
  - **VS Code 及同源 IDE 深度适配**：自动解析 `.lnk` 快捷方式或 `.exe` 目标，聚合 `workspaceStorage\*\workspace.json`（含 `state.vscdb` 精确活跃时间）、`globalStorage\storage.json`（当前打开窗口 `windowsState`、`backupWorkspaces` 与 `profileAssociations`）、`globalStorage\state.vscdb`（通过系统内置 `winsqlite3.dll` 读取 `history.recentlyOpenedPathsList`）以及 Windows `CustomDestinations` 跳转列表；同时支持 VS Code、VS Code Insiders、Cursor、Windsurf、Kiro、Trae、VSCodium 与 Antigravity，自动识别本地项目目录、`.code-workspace` 多根工作区及 `SSH / WSL / Container` 远程工作区；
  - **多类主流开发与效率工具原生支持**：内置适配 JetBrains 全家桶（CLion / IntelliJ IDEA / PyCharm / WebStorm / GoLand / Rider / RustRover / Android Studio `recentProjects.xml`）、Visual Studio (`ApplicationPrivateSettings.xml`)、Sublime Text (`Session.sublime_session`)、Obsidian (`obsidian.json` Vault 列表)、Notepad++、Typora、Microsoft Office 与 WPS Office；
  - **Windows 通用任务栏跳转列表与文件关联兜底**：实现纯 C# MS-SHLLINK 二进制流解析、`System.AppUserModel.ID` 提取与 Windows JumpList CRC64 哈希计算，自动读取任意应用的 `CustomDestinations` / `AutomaticDestinations` 跳转列表及通过 `AssocQueryStringW` 匹配的 `Recent` 最近文档；
  - **自定义关联项目与目录内联下钻浏览**：在应用最近预览弹层中支持单击直接用该应用打开项目、点击「展开 ▸」在弹层内直接下钻浏览项目目录结构（可随时一键「⬅ 返回最近」）、点击「＋ 目录 / ＋ 文件」或直接拖拽文件夹/工作区到面板进行手动关联与置顶，支持隐藏不需要的历史项及一键固定到 Dock；
  - **滚动防遮挡与悬停驻留详情提示 (`AttachRowHoverDetail`)**：目录浏览与应用最近打开列表默认均采用清爽单行展示，在滚动列表或快速移动鼠标时自动抑制详细名称与路径提示，仅当鼠标在某一行上静止停留约 `580ms` 后才同步弹出完整名称、完整路径与时间详情卡片及底部状态栏路径预览。

---

## [0.1.6] - 2026-10-30

### Added & Improved
- **长目录/长文件名称完整展示（多维成熟方案设计）**：
  - **智能悬浮全名卡片 (`ToolTip`)**：鼠标悬停在目录预览行时自动在行上方弹出支持多行自动换行的完整名称卡片，同时展示类型（文件夹/文件）、文件大小及修改/创建时间；配置快速首显（`160ms`）与连续扫读零延迟（`BetweenShowDelay = 80ms`），上下移动光标即可丝滑连读长项目目录名；
  - **底部状态栏 0 延迟联动全名预览**：鼠标移入目录/文件行时，弹层底部状态栏立即同步显示完整名称，移出自动恢复操作提示；
  - **渐进式行内操作显隐与拓宽预览面板**：将目录预览弹层拓宽至 `420px`，并将未固定目录的 `☆` 与 `隐藏` 按钮统一收拢为悬停显现（按时间排序时悬停自动让出日期列宽度），大幅增加默认状态下的目录名可见字数；
  - **右键快捷复制与全界面长文本提示覆盖**：目录预览右键菜单新增「复制完整名称」「复制完整路径」；同步补齐弹层标题栏、路径栏、收藏组弹层、全部收藏列表与托盘浮窗的完整名称/路径悬停提示。
- **多文件夹左右切换预览丝滑度与性能优化**：
  - **Dock 悬停切换防抖与同源去重**：在已展开目录预览时横向滑过多个文件夹图标采用 `45ms` 防抖，快速掠过中间文件夹不再阻塞 UI 线程；重入当前已展开文件夹直接跳过重绘；
  - **首屏极速渲染 + 后台分批挂载**：`FolderPopupWindow` 首屏仅同步构建可视区前 12 项以完成毫秒级布局定位，其余项在 `DispatcherPriority.Background` 异步分批追加，切换目录时通过版本号立即中止旧批次；
  - **单次目录枚举、内存缓存与扩展名图标共享缓存**：`DirectoryBrowserService` 合并目录/文件枚举为单次系统扫描并提供带时间戳校验的内存缓存；`ShellIconService` 新增普通文档按扩展名共享图标缓存及同步缓存直取通道，彻底消除多文件目录切换时的重复磁盘 I/O 与图标闪烁。

---

## [0.1.5] - 2026-10-29

### Fixed
- **修复左键点击 Dock 图标启动失效问题**：在 `DockWindow` 的 `PreviewMouseLeftButtonUp` 中仅当实际发生拖拽排序 (`isReordering == true`) 时才释放鼠标捕获 (`ReleaseMouseCapture`)，保留普通单击时 `Button` 原生的 `IsPressed` 状态，确保左键点击应用、文件夹与分组 100% 正常触发。
- **清理子进程 Electron/Chromium 环境变量污染**：启动时主动清理 `CHROME_CRASHPAD_PIPE_NAME` 与 `ELECTRON_RUN_AS_NODE`，确保从 PinDock 启动 Electron 类桌面应用（如百度翻译等）时正常弹出主窗口。

---

## [0.1.4] - 2026-10-29

### Added
- **字母与数字徽标图标 (`TextBadge`)**：在收藏项与收藏组图标样式中新增「字母 / 数字图标」，内置 `A–Z`、`0–9`、`01–03`、`AI / QT / DB / UI` 快捷点选，并支持输入任意 1~3 个自定义字符生成几何字标图标。
- **完善中英文 README 文档**：同步更新 [README.md](README.md) 与英文版 [README_EN.md](README_EN.md)。

### Improved & Fixed
- **修复切换图标样式时内置分类/自定义文件区未隐藏**：在 `ui.html` 中补充 `[hidden] { display: none !important; }`，解决 `.field-label` 的 `display: flex` 覆盖 HTML `hidden` 属性导致所有图标配置区同时展开的问题。
- **优化 Dock「图标 + 文字」显示对齐**：固定底部文字区高度为 `16px` 并将按钮内容设为顶部基准对齐，消除中英文混排（如 `百度翻译`、`E:`、`TOOLS`）导致的图标高低错位；同时将分隔符 `|` 与右侧折叠按钮 `>` 对齐至图标行中心。
- **修复贴边自动隐藏下点击右侧展开按钮 `>` 前被提前隐藏**：在 `ShowDock()` 时强制同步实际像素边界、扩充 `HitArea` 安全热区与展开按钮宽度，并在点击展开/折叠抽屉时保持 Dock 展开状态。
- **修复左键点击 Dock 应用偶发无法启动问题**：在 `PreviewMouseLeftButtonDown`、`Rebuild` 与 `HideDock` 时彻底重置 `dragStartCursor` 与拖拽重排状态，防止点击功能按钮后残留的拖拽坐标拦截后续应用点击事件；并为无显式工作目录的 `.exe` 自动回填所在目录。

---

## [0.1.3] - 2026-10-29

### Added
- **收藏项（文件夹 / 应用 / 文件）自定义图标**：支持为任意单个收藏项设置专属图标（默认图标、内置矢量图标库如磁盘/芯片/代码/文档、或本地 `.png/.ico/.exe/.lnk` 图标），在《全部收藏》列表点击左侧图标或在 Dock 右键菜单点击「编辑收藏 / 修改图标…」即可配置，并在 Dock、全部收藏、分组弹层与托盘浮窗同步展示。
- **Dock「纯图标」与「图标 + 文字」双模式切换**：支持手机桌面式（上图标 + 下方居中短文字）与 macOS 纯图标模式切换，可在设置页「外观」或 Dock 右键菜单中一键切换。
- **目录内容预览支持多维排序与按目录独立记忆**：
  - 支持 5 种排序模式：**名称 (A → Z，自然数字排序)**、**名称 (Z → A)**、**修改时间 (最新优先)**、**修改时间 (最早优先)**、**创建时间 (最新优先)**；
  - 为每个收藏目录独立记忆排序方式；按时间排序时在行内右侧柔和展示修改/创建日期。
- **全新高清应用图标 (`PinDock.ico`)**：采用现代悬浮 Dock 底座 + 彩色圆角应用块多尺寸（16px~256px）矢量设计，替换原有的字母 P 图标。

---

## [0.1.2] - 2026-10-29

### Improved & Fixed
- **搜索框光标与占位文字精准对齐**：修复 `Theme.xaml` 中 `TextBox` 的 `PART_ContentHost` 重复叠加 `Padding` 导致目录预览与托盘浮窗搜索框光标偏移到占位字中间的问题，并移除托盘浮窗搜索框的多余双层边框。
- **目录预览「已隐藏」交互与视觉升级**：修复 `ToggleHiddenBtn` 样式回退为系统默认按钮的问题，改为柔和胶囊徽标；将行内「隐藏」按钮改为鼠标悬停行时才显现，保持目录列表清爽，并将目录弹层背景设为实心卡片防止底层窗口文字透出。
- **修复目录预览移向其他 Dock 图标时不自动收起**：当鼠标从目录图标移向 Dock 上的其他应用、分组或功能按钮时立即收起目录预览弹层，并将离开判定收窄至当前目录按钮与弹层区域。
- **右键菜单（ContextMenu）UI 重构**：采用双列对齐布局（固定宽度 24px 图标列 + 左对齐文本列），消除混排符号导致文字参差不齐的问题；优化圆角阴影容器，消除四角背景残影。

---

## [0.1.1] - 2026-10-29

### Added
- **Dock 右侧展开/折叠抽屉按钮**：在 Dock 末端新增紧凑的 `‹ / ›`（纵向为 `▴ / ▾`）折叠开关，一键显示或隐藏「全部收藏」「＋添加」「⚙设置」功能按钮。
- **拖拽添加文件夹与应用增强**：
  - 贴边自动隐藏模式下，拖拽文件/文件夹靠近屏幕边缘可自动唤醒展开 Dock；
  - 拖拽文件/文件夹到 Dock 指定位置支持直接按位置插入；
  - 拖拽文件/文件夹到 Dock 上的「收藏组」图标可直接归入该分组；
  - 右下角托盘浮窗新增拖拽文件/文件夹添加收藏支持。
- **目录悬停预览支持隐藏与恢复指定子文件夹**：
  - 在目录预览弹层每行新增 `👁` 隐藏按钮与右键「在此预览中隐藏此文件夹」；
  - 底部状态栏新增 `👁 已隐藏 (N)` 切换开关，点击可查看已隐藏目录并一键「恢复显示 ↺」。
- **中英文双语 (`zh-CN` / `en-US`) 与快速切换亮暗主题**：
  - 全界面（Dock、目录弹层、分组弹层、托盘浮窗、全部收藏与设置面板）支持中英文双语实时切换；
  - 在《全部收藏/设置》顶部标题栏、右下角托盘浮窗及 Dock 右键菜单提供 `☀ / 🌙` 一键切换浅色/深色主题。
- **Windows 11 右键菜单完整适配**：
  - 资源管理器右键扩展覆盖全部文件/应用 (`*`)、文件夹 (`Directory`)、目录背景、磁盘根目录 (`Drive`) 及系统 `SendTo`（发送到 -> PinDock）；
  - 新增「Windows 11 一级右键菜单直显」设置开关（免点“显示更多选项”）。

### Fixed
- **修复贴边自动隐藏时弹出右键菜单导致 Dock 自动隐藏**：跟踪 `DockWindow` 与 `FolderPopupWindow` 的 `ContextMenu` 开启状态，右键菜单打开期间保持 Dock 展开。
- **修复磁盘根目录与目录背景右键参数引号转义问题**：兼容末尾带反斜杠的路径参数（如 `E:\`）。

---

## [0.1.0] - 2026-10-29

### Added
- **混合架构（C# .NET 10 + WebView2 HTML/CSS）**：原生系统集成 + GPU 加速现代界面。
- **分组自定义图标**：支持默认四宫格、组内前 4 项 `2×2` 缩略图、内置分类矢量图标库、自定义本地图片/程序图标。
- **Dock 图标拖拽排序与分隔符**：支持直接拖拽排序、右键插入/移除分隔符、按类型（应用/文件夹/分组）一键自动分隔。
- **右下角托盘左击快捷浮窗（Flyout）**：集成即时搜索、最近使用、固定项与分组速览、Dock 状态控制与底部工具栏。
- **开机自启、关闭行为控制、GitHub 在线升级与国内镜像加速**：提供一键编译安装包 `PinDock-Setup-0.1.0.exe`（支持自动检测历史安装路径与配置）。

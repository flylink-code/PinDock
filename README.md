# PinDock

Windows 独立快捷启动工具。悬浮 Dock、本地收藏、分组与主题，不替换系统任务栏。

## 已实现 (v0.1.1)

- **混合架构（C# .NET 10 + WebView2 HTML/CSS）**：系统底层（全局热键、48×48 高清图标提取、全屏避让、资源管理器右键、开机自启、单实例 IPC）采用 C# 原生实现；《全部收藏》《设置》《添加/编辑收藏》面板采用预热 WebView2 1:1 渲染 HTML/CSS 原型（[Assets/ui.html](src/PinDock/Assets/ui.html)），GPU 硬件加速
- **悬浮 Dock**：四边停靠、固定项、溢出入口、收藏组弹层、**文件夹悬停子目录快速浏览（支持多级展开、搜索、一键固定、子文件夹隐藏与恢复）**、**直接拖拽图标排序与自由插入/删除分隔符（支持按应用/文件夹一键自动分隔）**、**自定义分组图标（支持组内 2×2 缩略宫格、内置分类矢量图标、本地图片/程序图标）**、**右侧末端展开/折叠功能按钮抽屉 (`‹ / ›`)**
- **拖拽添加体验**：贴边自动隐藏模式下拖拽文件夹/应用靠近屏幕边缘自动唤醒 Dock；支持在 Dock 上按指定位置拖入插入，或直接拖到「收藏组」图标上归入该分组
- **中英文双语与快速主题切换**：支持 `简体中文 (zh-CN)` 与 `English (en-US)` 实时切换；在面板顶部、托盘浮窗及 Dock 右键菜单支持 `☀ / 🌙` 一键切换亮色/暗色主题
- **右下角托盘左击快捷浮窗（Flyout）**：左击系统托盘图标弹出近托盘浮窗，集成顶部即时搜索、主题/语言快切、最近使用快捷栏、固定项与分组速览、Dock 状态快捷切换及底部工具栏（支持直接拖入文件/文件夹）
- **Windows 11 右键与系统集成**：支持所有文件/应用/文件夹/磁盘右键“添加到 PinDock”及 `SendTo`（发送到）、支持 **Windows 11 一级右键菜单直显** 开关、开机自动启动、关闭面板最小化到托盘或直接退出、**GitHub Releases 在线检查更新与一键升级（支持国内镜像加速开关与自定义镜像源）**
- **设置**：5 套现代配色 × 浅色/深色/跟随系统、图标大小、透明度（40%–100%）、多屏策略、置顶、全屏避让、锁定位置、导入/导出
- 系统托盘、单实例、PerMonitorV2 DPI
- 收藏保存在 `%LocalAppData%\PinDock\library.json`（v2，可读 v1 并升级）；版本更新记录见 [CHANGELOG.md](CHANGELOG.md)

## 运行与打包安装包

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet build PinDock.sln
dotnet run --project tests/PinDock.Tests
dotnet run --project src/PinDock

# 一键编译生成安装包 (输出至 dist\PinDock-Setup-0.1.1.exe)
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

移除收藏不会删除目标文件。`prototype/index.html` 是交互概念原型，供对照设计。

## 已知限制

- 贴边自动隐藏：若停靠边与另一块显示器相邻，鼠标可能穿过边界而无法触发展开；可改停靠到不相邻的边。
- 全屏避让依赖 `SHQueryUserNotificationState`，部分全屏游戏/演示场景需实机确认。

# PinDock

Windows 独立快捷启动工具。悬浮 Dock、本地收藏、分组与主题，不替换系统任务栏。

## 已实现

- 悬浮 Dock：四边停靠、固定项、溢出入口、收藏组弹层、系统图标、直接拖入与右键快捷菜单
- 显示模式：常驻、快捷键呼出（支持自定义录制与清除停用）、贴边自动隐藏
- 全部收藏与本机应用检索：搜索、类型筛选、检索本机已安装应用（开始菜单/桌面）一键固定到 Dock、快速添加文件夹、分组管理
- 资源管理器右键集成：支持在 Windows 文件夹上右键“添加到 PinDock”直接固定到运行中的 Dock
- 设置：5 套现代配色 × 浅色/深色/跟随系统、图标大小、透明度（40%–100%）、多屏策略、置顶、全屏避让、锁定位置、导入/导出
- 系统托盘、单实例、PerMonitorV2 DPI
- 收藏保存在 `%LocalAppData%\PinDock\library.json`（v2，可读 v1 并升级）

## 运行

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet build PinDock.sln
dotnet run --project tests/PinDock.Tests
dotnet run --project src/PinDock
```

移除收藏不会删除目标文件。`prototype/index.html` 是交互概念原型，供对照设计。

## 已知限制

- 贴边自动隐藏：若停靠边与另一块显示器相邻，鼠标可能穿过边界而无法触发展开；可改停靠到不相邻的边。
- 全屏避让依赖 `SHQueryUserNotificationState`，部分全屏游戏/演示场景需实机确认。

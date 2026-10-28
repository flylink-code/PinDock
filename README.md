# PinDock

Windows 独立快捷启动工具。当前为开发预览，并非 v0.1 正式版。

目前实现了基础收藏窗口、文件和文件夹选择、拖入、路径添加、编辑名称与 EXE 启动配置、Shell 打开、系统图标提取及进程内缓存、移除收藏和本地 JSON 保存。Dock、多屏、托盘、分组、主题等功能尚未完成。

需要 Windows 和 .NET 10 SDK：

```powershell
dotnet build PinDock.sln
dotnet run --project tests/PinDock.Tests
dotnet run --project src/PinDock
```

收藏存放在 `%LocalAppData%\PinDock\library.json`。移除收藏不会删除目标文件。

`prototype/index.html` 是交互概念原型，不代表原生应用已具备其中的功能。

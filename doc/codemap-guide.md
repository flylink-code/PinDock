# PinDock 代码地图速查

1. 已知名称：`node doc/codemap.mjs --find Bookmark`。
2. 已知路径：`node doc/codemap.mjs --outline src/PinDock/Collections.cs`。
3. 范围不明：从 `doc/CODE_MAP_INDEX.md` 进入模块页，再打开文件符号页及源码行；必要时用 `rg` 查找未索引内容。
4. `node doc/codemap.mjs --map` 或 `doc/codemap/hubs.md` 仅显示支持的导入边；本项目 C# 无引用边，不能依靠它们分析 C# 架构。

| 模块 | 范围 | 用途 |
| --- | --- | --- |
| `app` | `src/PinDock/` | WPF 应用及数据存储声明 |
| `tests` | `tests/PinDock.Tests/` | 测试源码；当前顶层语句无声明条目 |
| `tooling` | `doc/` | 代码地图生成器自身 |

改动索引范围内的代码后执行 `node doc/codemap.mjs` 和 `node doc/codemap.mjs --check`，不要手改生成页。详细覆盖范围、依赖、诊断基线见 `doc/CODE_MAP.md`。

常见问题：找不到 XAML、调用方或顶层测试语句时直接查源码；找不到依赖包时配置 `CODEMAP_REQUIRE_FROM`；`--check` 漂移时重新生成并检查 diff。地图诊断不等同于编译错误，需结合源码检查。

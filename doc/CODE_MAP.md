# PinDock 代码地图

## 结构和用法

地图分为总索引 `CODE_MAP_INDEX.md`、模块页 `codemap/<模块>.md` 和文件符号页；`codemap/hubs.md` 展示支持的导入级引用排行。所有页面、缓存由 `codemap.mjs` 生成，禁止手改；本文件和 `codemap-guide.md` 为手写文档。

在项目根目录运行：

```powershell
node doc/codemap.mjs                 # 全量范围增量生成
node doc/codemap.mjs --check         # 只读检查漂移
node doc/codemap.mjs --find Bookmark # 按符号名查询
node doc/codemap.mjs --outline src/PinDock/Collections.cs
node doc/codemap.mjs --map           # 按导入引用排行
node doc/codemap.mjs --module app    # 热缓存且其他模块未变化时局部更新
node doc/codemap.mjs --help
```

生成器需要 Node.js 以及可解析的 `typescript`、`web-tree-sitter`、`tree-sitter-wasms`。可通过 `CODEMAP_REQUIRE_FROM` 指向包含这些依赖的目录或 package.json；不要将它们作为应用运行时依赖。配置变化或跨模块修改后使用无参数命令，缓存可删除重建。

## 覆盖契约

索引根为 `src`、`tests`、`doc`，模块为 `app`、`tests` 和 `tooling`；扩展名及目录排除列表见 `codemap.config.json`。`bin`、`obj`、`node_modules`、生成目录等不纳入。`doc` 中符合扩展名的生成器本身也会被索引；Markdown、XAML、项目文件和 `prototype/index.html` 不在覆盖范围。

C# 使用 tree-sitter 提取声明，不提取 C# `using` 引用边；顶层语句中的执行逻辑也不是声明。XAML 绑定、自动生成的代码、预处理、宏和运行时行为不在地图内。引用图只按支持的 Rust `use` 与 TS/JS `import` 做词法级解析，无法提供调用图或类型推断；外部依赖、动态导入、歧义和未解析项不能视为已确认依赖或无依赖。PinDock 的 C# 调用与界面关系需用源码、`rg` 或 LSP 继续验证。

## 首次基线

- 6 个文件、348 个符号、3 个模块、14 个生成页面；其中 tooling 的生成器自身有 319 个符号。
- 引用边：0 已解析、0 歧义、7 未解析；这些数字不代表 C# 的依赖情况，因此枢纽页为空是预期结果。
- 1 条解析恢复：`src/PinDock/Collections.cs:22` 的 `LibraryStore(string directory)` 主构造函数语法处报告 `ERROR`；该声明及部分成员已提取，但不能保证完整性。
- `tests/PinDock.Tests/Program.cs` 以顶层语句实现测试，当前索引为 0 个声明，并不表示没有测试代码。
- 首次生成后重复生成无变化，`--check` 无漂移；后续诊断增加时应核查原因并更新此基线。

## 维护清单

代码增删改后重新生成并执行 `--check`；审查索引、模块页和诊断变化。生成物应与代码一起提交；生成器、配置、缓存、索引和页面位于 `doc/` 内。

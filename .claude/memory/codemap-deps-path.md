---
name: codemap-deps-path
description: 本机运行 PinDock 代码地图生成器所需的 CODEMAP_REQUIRE_FROM 依赖路径
metadata:
  type: reference
---

运行 `node doc/codemap.mjs` 或 `node doc/codemap.mjs --check` 前需设置 `CODEMAP_REQUIRE_FROM=/c/Users/admin/.codemap-deps`，该目录的 `node_modules` 提供 `typescript`、`web-tree-sitter` 与 `tree-sitter-wasms`；未设置时会报 `Cannot load typescript`。

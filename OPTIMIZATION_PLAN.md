# BGIJSTool 优化方案与修改简介

## 项目现状

BGIJSTool 是一个基于 WPF / .NET 8 的 BetterGI 脚本管理工具，主要通过 `config.json` 驱动备份、删除、还原和复制脚本文件。当前结构较轻量，核心代码集中在：

- `MainWindow.xaml` / `MainWindow.xaml.cs`：界面、按钮事件、模块执行流程。
- `Services/FileManager.cs`：备份 zip、删除、还原、copy zip 解压等文件操作。
- `Services/ConfigService.cs`：配置读取与保存。
- `Models/Config.cs`：配置模型和 `files` 转换器。
- `build.ps1` / `.github/workflows/release.yml`：本地与 GitHub Actions 发布流程。

本地验证结果：`dotnet build --nologo` 已通过，0 warning / 0 error。

## 优先级 P0：建议立刻修复

### 1. 修复中文文案和配置乱码

当前多个文件里的中文内容疑似已经被错误编码保存，包括 `README.md`、`SPEC.md`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/*.cs`、`config.json`、`.github/workflows/release.yml`。这会直接影响：

- 软件界面显示。
- 日志内容可读性。
- 配置模块名称和 zip 文件匹配。
- 发布说明可读性。

建议修改：

- 统一将源码、文档、配置保存为 UTF-8。
- 恢复所有中文 UI 文案、日志文案、配置模块名、注释和发布说明。
- 在 `.editorconfig` 或 README 中明确编码要求。
- 对 `config.json` 中的路径和 zip 名称做一次人工核对，避免乱码名称导致 copy 匹配失败。

### 2. 统一路径展开逻辑，修复目录/通配符路径执行错误

`MainWindow.xaml.cs` 和 `FileManager.cs` 都实现了 `ResolveBgi`。其中 `MainWindow.xaml.cs` 的 `MakeRel` 只做了斜杠替换，没有把 `{BGIpath}\User\JsScript\` 前缀去掉。目录展开或通配符展开后，可能把完整磁盘路径再传给 `FileManager.GetFullPath()`，导致目标路径拼接错误。

建议修改：

- 删除 `MainWindow.xaml.cs` 中重复的路径解析逻辑。
- 将路径解析统一收敛到 `FileManager`，暴露一个公共方法，例如 `ResolveBgiPaths(string path)`。
- 所有 `del`、`restore`、`bak` 统计都使用同一套解析结果。
- 为精确路径、`*.json`、目录递归展开各补一组测试样例。

### 3. 加强 zip 解压安全校验

`ExecuteCopy` 和 `ExecuteRestoreFromZip` 会把 zip 条目写入 BetterGI 脚本目录。当前没有显式拦截 `../`、绝对路径、盘符路径等危险条目。

建议修改：

- 解压前规范化目标路径。
- 校验最终路径必须位于 `{BGIpath}\User\JsScript\` 内。
- 跳过并记录非法条目。
- 对 zip 条目名中的反斜杠、正斜杠统一处理。

### 4. 空备份 zip 不应被生成

`CreateBakZip` 在没有实际备份文件时仍可能创建空 zip，然后日志提示“未生成 zip”。这会误导用户，也会污染还原下拉列表。

建议修改：

- 先收集可备份文件和 restore manifest。
- 当没有任何有效备份/清理信息时，不创建 zip。
- 日志明确提示哪些路径未命中。

## 优先级 P1：提升稳定性和可维护性

### 1. 拆分窗口事件和业务流程

`MainWindow.xaml.cs` 同时负责 UI 状态、配置、路径解析、执行编排和日志输出，后续功能增加会越来越难维护。

建议修改：

- 新增 `ScriptOperationService`，负责模块执行编排。
- `FileManager` 只保留文件系统动作。
- `MainWindow` 只负责控件状态、选择项和日志展示。
- 后续可平滑迁移到 MVVM。

### 2. 修正按钮状态管理

当前 `EnableControls(pathValid)` 会直接启用执行按钮，但“路径有效”和“已选择模块”是两个条件。执行完成后的 `finally` 也会直接把按钮设为可用，可能绕过占位项和路径校验。

建议修改：

- 新增 `UpdateActionState()`。
- 执行按钮条件：BetterGI.exe 有效 + 已选择真实模块 + 当前未执行。
- 还原按钮条件：BetterGI.exe 有效 + 已选择真实备份 zip + 当前未执行。
- 删除备份按钮条件：已选择真实备份 zip + 当前未执行。

### 3. 配置校验前置

当前配置读取主要依赖反序列化成功，缺少结构化校验。

建议修改：

- 校验 `BGIpath`、`modules`、`module.name`、`files[].op`、`files[].paths`。
- 对空模块、空路径、未知 op、重复路径给出日志警告。
- 保存 `BGIpath` 时保留原 JSON 缩进和中文编码。

### 4. 备份文件名规范化

备份 zip 使用模块名作为文件名前缀。如果模块名包含 Windows 不允许的字符，例如 `\ / : * ? " < > |`，会导致备份失败。

建议修改：

- 增加 `SanitizeFileName()`。
- 文件名建议格式：`yyyyMMdd_HHmmss_模块名.zip`。
- manifest 中保留原始模块名，文件名只作为安全标识。

## 优先级 P2：体验和发布优化

### 1. 优化日志体验

当前日志已同时写入 UI 和文件，这是好基础。可以继续增强：

- 增加“打开日志目录”“清空当前日志”按钮。
- 日志文件写入使用 UTF-8。
- 长路径日志可折叠或仅显示相对路径，减少界面噪声。
- 批量操作结束后输出成功、跳过、失败统计。

### 2. 优化备份/还原体验

建议在还原下拉列表中展示更多信息：

- 模块名。
- 创建时间。
- 备份文件数量。
- 是否包含 copy 清理清单。

可通过读取 `_restore_manifest.json` 实现，不必打开整个 zip 解压。

### 3. 发布流程补强

当前本地 `build.ps1` 和 GitHub Actions 都能发布，但可继续强化：

- CI 增加 `dotnet build -c Release`。
- 增加最小化单元测试后在 CI 中执行 `dotnet test`。
- 发布包内附带 `README.md`、`SPEC.md` 和默认 `config.json`。
- 在 release body 中恢复中文说明，并列出 FDD / Self-contained 区别。

## 建议测试清单

建议新增测试项目 `BGIJSTool.Tests`，优先覆盖无 UI 的服务层：

- `ConfigService`：正常配置、缺字段、未知 op、空模块。
- `FileManager` 路径解析：精确文件、通配符、目录递归、中文路径。
- 备份 zip：存在文件、缺失文件、空备份、不合法模块名。
- copy zip 解压：中文文件名、GBK/UTF-8 文件名、非法 `../` 条目。
- restore manifest：先删除 copy 文件，再恢复原备份文件。

## 推荐实施顺序

1. 恢复 UTF-8 中文文案和配置，确保界面、日志、文档可读。
2. 收敛路径解析到 `FileManager`，修复目录/通配符展开的相对路径问题。
3. 增加 zip 解压路径安全校验。
4. 修正空备份 zip 和备份文件名规范化。
5. 重构执行编排服务，并补充测试。
6. 优化日志、还原列表和发布流程。

## 修改简介模板

后续实际改动可以按以下格式记录：

```markdown
## 修改简介

- 修复：恢复源码、配置、文档中的中文 UTF-8 文案。
- 修复：统一路径解析逻辑，目录和通配符展开后始终返回 JsScript 相对路径。
- 安全：解压 zip 前校验目标路径，阻止路径穿越。
- 稳定性：无有效备份内容时不再生成空 zip。
- 体验：执行按钮状态同时受路径、选择项和执行状态控制。
- 工程：增加服务层测试，覆盖配置、路径解析、备份、还原和 copy 解压。
```

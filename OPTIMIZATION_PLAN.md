# BGIJSTool 优化方案与完成记录

## 项目现状

BGIJSTool 是一个基于 WPF / .NET 8 的 BetterGI 脚本管理工具，通过 `config.json` 驱动备份、删除、还原和复制脚本文件。

核心代码：

- `MainWindow.xaml` / `MainWindow.xaml.cs`：界面、控件状态、选择项和日志展示。
- `Services/FileManager.cs`：备份 zip、删除、还原、copy zip 解压、备份摘要读取等文件系统动作。
- `Services/ScriptOperationService.cs`：模块执行编排。
- `Services/ConfigService.cs`：配置读取、保存和结构化校验。
- `Models/Config.cs`：配置模型和 `files` 转换器。
- `build.ps1` / `.github/workflows/release.yml`：本地与 GitHub Actions 发布流程。
- `BGIJSTool.Tests`：服务层测试项目。

本地验证：

- `dotnet build --nologo`：通过，0 warning / 0 error。
- `dotnet build -c Release --nologo`：通过，0 warning / 0 error。
- `dotnet test BGIJSTool.Tests\BGIJSTool.Tests.csproj --nologo`：通过，6 tests。

## 优先级 P0：建议立刻修复 ✅ 已完成

### 1. 修复中文文档和配置乱码 ✅ 已完成

完成情况：

- 新增 `.editorconfig`，统一声明 UTF-8、LF、末尾换行等基础格式规则。
- 更新 `.gitattributes`，统一文本文件 LF 换行，并标记 zip/exe/dll 为二进制文件。
- 修复 README、SPEC 中发现的残留错误文案。
- 日志文件写入方式使用 UTF-8。
- 验证 `config.json` 可正常解析，copy zip 文件名能匹配到 `copy/` 目录。

### 2. 统一路径展开逻辑，修复目录/通配符路径执行错误 ✅ 已完成

完成情况：

- 删除 `MainWindow.xaml.cs` 中重复的路径展开逻辑。
- 在 `FileManager` 中新增公共方法 `ResolveBgiPaths(string path)`。
- `del`、`restore`、`bak` 操作均调用同一套路径展开逻辑。
- `totalFiles` 统计改为基于展开后的路径数量，并覆盖 `bak`、`del`、`restore` 和 `copy` 步骤。

### 3. 加强 zip 解压安全校验 ✅ 已完成

完成情况：

- 新增 `IsSafePath()`，拒绝 `..`、盘符路径、绝对路径等危险条目。
- 拒绝写入目标目录外的路径，防止路径穿越。
- 非法条目跳过并记录警告日志。
- 条目名中的反斜杠、正斜杠统一处理。

### 4. 空备份 zip 不应被生成 ✅ 已完成

完成情况：

- 当无有效备份文件且无 copy 清理内容时，不再创建空 zip。
- 新增 `notFoundPaths` 记录列表，遍历后输出备份路径未找到文件警告。
- 日志明确提示未命中路径和最终 zip 统计信息。

## 优先级 P1：提升稳定性和可维护性 ✅ 已完成

### 1. 拆分窗口事件和业务流程 ✅ 已完成

完成情况：

- 新增 `Services/ScriptOperationService.cs`，集中处理模块执行编排、预备份、删除、还原和 copy 调用。
- `MainWindow.xaml.cs` 收敛为配置加载、控件状态、选择项展示和日志触发。
- 文件系统动作继续由 `FileManager` 承担，窗口层不再直接展开路径并逐项执行。

### 2. 修正按钮状态管理 ✅ 已完成

完成情况：

- 新增 `_isBusy`、`_isBgiPathValid` 和 `UpdateActionState()`。
- 执行、还原、删除备份按钮均由统一状态函数控制。
- 执行完成、还原完成、删除完成、刷新配置和浏览路径后都会重新计算按钮状态。

### 3. 配置校验前置 ✅ 已完成

完成情况：

- `ConfigService` 新增 `ValidateConfig()`，在反序列化前校验 JSON 结构。
- 对缺失字段、未知 `op`、非数组 `files/paths` 作为错误处理。
- 对空模块、空路径、同一 `op` 内重复路径输出警告；`bak` 和 `del` 共用路径不再误报。
- 保存 `BGIpath` 时优先更新原 JSON 对象，并使用 UTF-8 与中文直写输出。

### 4. 备份文件名规范化 ✅ 已完成

完成情况：

- `FileManager` 新增 `SanitizeFileName()`。
- 备份 zip 文件名调整为 `yyyyMMdd_HHmmss_安全模块名.zip`。
- `_restore_manifest.json` 中继续保留原始模块名。

## 优先级 P2：体验和发布优化 ✅ 已完成

### 1. 优化日志体验 ✅ 已完成

完成情况：

- 主界面新增“打开日志目录”和“清空当前日志”按钮。
- `Logger` 继续使用 UTF-8 写入日志文件，并新增当前日志清空能力。
- 模块执行结束后输出批量操作统计摘要，详细结果继续按成功/警告/错误日志呈现。

### 2. 优化备份/还原体验 ✅ 已完成

完成情况：

- 新增 `BackupInfo` 和 `FileManager.GetBackupInfos()`，通过 zip 内 `_restore_manifest.json` 读取备份摘要。
- 还原下拉列表显示模块名、创建时间、备份文件数、是否包含 copy 清理清单和 zip 文件名。
- 还原和删除操作改为基于真实备份文件对象执行，避免显示文本与文件名耦合。

### 3. 发布流程补强 ✅ 已完成

完成情况：

- 本地 `build.ps1` 在发布前执行 Release build，并在发现测试项目时执行 `dotnet test`。
- GitHub Actions 增加 `dotnet build -c Release` 和测试步骤。
- 发布包附带 `README.md`、`SPEC.md`、默认 `config.json` 和 `copy/` 目录。
- release body 恢复中文说明，并说明 Framework-Dependent 与 Self-Contained 的区别。

## 建议测试清单 ✅ 已完成

完成情况：

- 新增 `BGIJSTool.Tests` 测试项目。
- 已覆盖配置校验、`bak/del` 共用路径不误报、保存配置中文直写、备份文件名规范化和 restore manifest 摘要读取。
- 已验证 `dotnet test BGIJSTool.Tests\BGIJSTool.Tests.csproj --nologo` 通过。

后续可继续扩展：

- `FileManager.ResolveBgiPaths()`：精确文件、通配符、目录递归、中文路径。
- 备份 zip：存在文件、缺失文件、空备份。
- copy zip 解压：中文文件名、GBK/UTF-8 文件名、非法 `../` 条目。
- restore manifest：先删除 copy 文件，再恢复原备份文件。

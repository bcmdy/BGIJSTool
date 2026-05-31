# BetterGI 脚本管理工具（BGIJSTool）

BGIJSTool 是一个基于 WPF / .NET 8 的 BetterGI 脚本管理工具。它通过 `config.json` 描述模块和步骤，批量完成 BetterGI 脚本文件的备份、删除、还原和 copy 包解压覆盖。

## 功能

| 操作 | 说明 |
| --- | --- |
| `bak` | 将 `config.json` 中指定的脚本文件按原目录结构打包到 `backup/` 下的备份 zip |
| `del` | 删除目标脚本文件，并清理空目录 |
| `restore` | 从备份内容还原目标脚本文件 |
| `copy` | 从程序目录 `copy/` 中匹配 zip，并解压覆盖到 BetterGI 的 `User/JsScript/` |

补充能力：

- 按 `files` 数组顺序执行步骤。
- `paths` 支持精确文件、同目录通配符和目录递归展开。
- copy zip 解压时校验路径安全，阻止 `../`、绝对路径和盘符路径。
- 备份文件名会自动清理 Windows 非法字符，格式为 `yyyyMMdd_HHmmss_模块名.zip`。
- 还原列表显示模块名、创建时间、备份文件数和是否包含 copy 清理清单。
- 日志同时写入界面和 `logs/yyyy-MM-dd.log`，支持打开日志目录和清空当前日志。

## 快速开始

1. 启动程序后点击“浏览...”，选择 `BetterGI.exe`。
2. 根据 `SPEC.md` 编辑 `config.json`。
3. 在“选择模块”下拉框中选择模块，点击“执行”。
4. 如需还原，在“选择备份”下拉框中选择备份 zip，点击“执行还原”。

所有 `paths` 都是相对于：

```text
{BGIpath}\User\JsScript\
```

## 配置示例

```jsonc
{
  "BGIpath": "D:\\YS\\BetterGI",
  "modules": [
    {
      "name": "删除示例脚本",
      "files": [
        {
          "op": "bak",
          "paths": [
            "Example\\script.json",
            "Example\\routes\\*.json",
            "Example\\extra\\"
          ]
        },
        {
          "op": "del",
          "paths": [
            "Example\\script.json",
            "Example\\routes\\*.json",
            "Example\\extra\\"
          ]
        }
      ]
    },
    {
      "name": "替换示例脚本",
      "files": [
        {
          "op": "bak",
          "paths": [
            "Example\\"
          ]
        },
        {
          "op": "del",
          "paths": [
            "Example\\"
          ]
        },
        {
          "op": "copy",
          "paths": [
            "替换示例脚本.zip"
          ]
        }
      ]
    }
  ]
}
```

路径写法：

- `Example\script.json`：精确文件。
- `Example\routes\*.json`：展开同目录下匹配的文件。
- `Example\extra\`：递归展开目录下所有文件。
- `copy` 步骤中的路径对应程序目录 `copy/` 下的 zip 文件名或匹配前缀。

## 项目结构

```text
.
├─ App.xaml
├─ MainWindow.xaml
├─ MainWindow.xaml.cs
├─ Models/
│  └─ Config.cs
├─ Services/
│  ├─ ConfigService.cs
│  ├─ FileManager.cs
│  ├─ ILogger.cs
│  ├─ Logger.cs
│  └─ ScriptOperationService.cs
├─ BGIJSTool.Tests/
│  ├─ ConfigServiceTests.cs
│  ├─ FileManagerTests.cs
│  └─ TestWorkspace.cs
├─ config.json
├─ copy/
├─ build.ps1
├─ SPEC.md
└─ README.md
```

## 运行要求

- Windows 10 / 11
- .NET 8 Desktop Runtime（框架依赖发布包需要）
- BetterGI 已安装

开发环境：

- .NET 8 SDK
- Windows + WPF 支持

仓库包含 `global.json`，默认使用 .NET SDK `8.0.421`，允许滚动到同一主版本的更新 feature band。

## 开发

```powershell
dotnet run
```

构建：

```powershell
dotnet build --nologo
dotnet build -c Release --nologo
```

测试：

```powershell
dotnet test BGIJSTool.Tests\BGIJSTool.Tests.csproj --nologo
```

发布：

```powershell
.\build.ps1
.\build.ps1 -SelfContained
```

发布脚本会执行 Release build，并在发现测试项目时执行 `dotnet test`。发布目录会附带：

- `BGIJSTool.exe`
- `config.json`
- `copy/`
- `README.md`
- `SPEC.md`

## 发布模式

- Framework-Dependent：默认模式，体积更小，目标机器需要安装 .NET 8 Desktop Runtime。
- Self-Contained：使用 `.\build.ps1 -SelfContained`，体积更大，目标机器无需额外安装 .NET 运行时。

## 编码说明

源码、配置和文档统一使用 UTF-8。请避免用非 UTF-8 编码保存 `.cs`、`.xaml`、`.json`、`.md` 文件。

## 说明

本工具是 BetterGI 脚本配套管理工具，仅用于脚本文件维护、备份和还原场景。

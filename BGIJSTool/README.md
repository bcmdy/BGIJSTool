# BetterGI 脚本管理工具 (BGI Script Manager)

> **BetterGI**（Better Genshin Impact）辅助脚本文件自动化管理工具，集成 WPF 图形界面、JSON 配置文件驱动的批量操作。

---

## ✨ 功能

| 操作 | 说明 |
|------|------|
| **bak**   | 将 `config.json` 中指定的文件，按原目录结构备份到程序目录下的 `backup/` |
| **del**   | 直接删除目标文件 |
| **restore** | 将 `backup/` 中的备份文件还原到指定目录 |
| **copy**  | 从程序目录下的 `copy/` 文件夹复制新脚本到目标位置 |

- **顺序执行**：所有操作按 `files` 数组中步骤的排列顺序依次执行
- **通配符匹配**：`paths` 支持 `*.json` 等通配符，展开同目录下所有匹配文件
- **目录展开**：` paths` 以 `\` 或 `/` 结尾则视为目录，递归展开其下所有文件
- **中文路径支持**：自动支持中文路径/文件名

---

## 📋 Quick Start

1. **设置 BetterGI 路径**：程序启动后右上角通过 "浏览…" 按钮选择 BetterGI.exe。
2. **配置 `config.json`**：等参照 `BGIJSTool/SPEC.md` 撰写，内含操作顺序、路径、模块安排。
3. **十分 selon 需求选择操作**：点击 "手册操作" 下拉列表选择模做执行后点击 "执行"。

---

## 📁 Project Structure

```
BGITools/
├── BGIJSTool/
│   ├── SPEC.md               # 详细规格说明书
│   ├── build.ps1             # 编译脚本（自动复制 copy 和 config.json）
│   ├── src/
│   │   ├── BGIJSTool.csproj
│   │   ├── icon.ico          # 应用图标
│   │   ├── MainWindow.xaml
│   │   ├── MainWindow.xaml.cs
│   │   ├── Models/
│   │   │   └── Config.cs     # 配置模型
│   │   ├── Services/
│   │   │   ├── ConfigService.cs
│   │   │   ├── FileManager.cs  # 核心文件操作
│   │   │   ├── ILogger.cs
│   │   │   └── Logger.cs
│   │   └── config.json       # 实际配置文件（运行时用）
│   ├── copy/                 # 脚本修改压缩包目录
│   └── BGIJSTool.slnx
└── README.md                 # 本文件
```

---

## 🔧 Config Reference

`config.json` 关键字段说明（完整版见 **SPEC.md §4**）：

```jsonc
{
    "BGIpath": "E:\\YS\\BetterGI",          // BetterGI 根目录
    "modules": [
        {
            "name": "模块名",
            "files": [
                {
                    "op": "bak",           // 操作：bak / del / restore / copy
                    "paths": [
                        "精确路径\\文件名.json",           // 精确路径
                        "子目录\\*.json",                  // 通配符（本目录所有 json）
                        "子目录\\"                         // 目录展开（递归子目录）
                    ]
                }
            ]
        }
    ]
}
```

> 所有 `paths` 均为相对于 `{BGIpath}\User\JsScript\` 的相对路径。

---

## 🛠 Requirements

- Windows 7 / 10 / 11
- .NET 6+ (SDK)
- BetterGI 已安装

---

## 📝 开发

```bash
cd BGIJSTool/src
dotnet run
```

## 🔨 编译

```powershell
cd BGIJSTool
.\build.ps1          # 框架依赖单文件（推荐）
.\build.ps1 -SelfContained  # 自包含单文件
```

编译后 `copy/` 和 `config.json` 会自动复制到 `publish/` 目录。

---

## 📄 License

本项目为 BetterGI 辅助脚本配套管理工具，仅供学习/研究用途。

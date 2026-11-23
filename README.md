# desktop-jump-list

`desktop-jump-list` 是一个运行在 Windows 上的原生桌面快捷启动器。它以一个可拖动的悬浮按钮常驻桌面边缘，点击后展开快捷面板，让用户快速打开常用文件夹、文件、应用程序和网页，减少在资源管理器、开始菜单或任务栏中反复查找的操作。

## 首版目标

- 提供一个始终置顶、可拖动、可收起的悬浮入口。
- 点击入口后展示快捷项面板；再次点击空白处或按 `Esc` 收起。
- 支持添加和打开以下快捷项：
  - 文件夹
  - 文件
  - Windows 应用程序或 `.exe` 文件
  - 网页链接
- 支持为快捷项设置名称、图标和打开参数。
- 支持分组和排序，例如“工作”“开发”“常用”。
- 在面板内提供新增、编辑、删除和搜索。
- 配置自动保存到用户本地目录，关闭后仍保留。
- 提供“开机启动”开关，通过当前用户的 Windows 登录启动项运行。
- 关闭主窗口时最小化到系统托盘；可从托盘重新显示或退出程序。

## 使用方式

1. 启动程序后，屏幕边缘会出现圆形悬浮入口。
2. 点击入口打开快捷面板。
3. 点击“添加”并选择文件、文件夹、应用或输入网址。
4. 点击快捷项即可按系统默认方式打开目标。
5. 在设置中开启“开机启动”，下次登录 Windows 时程序会自动运行。

## 界面原则

- 悬浮入口保持小巧，不遮挡工作内容。
- 快捷项以图标和名称呈现，常用操作不超过两次点击。
- 所有编辑操作在面板内完成，不要求用户手工修改配置文件。
- 默认使用系统主题；首版提供浅色和深色两种外观。

## 技术方案

首版采用 **C# + WPF + .NET 8 for Windows**，原因是它可以直接调用 Windows 能力，并能实现无边框置顶窗口、系统托盘、文件选择器和开机启动。

项目预计包含：

```text
desktop-jump-list/
├── README.md
├── src/
│   └── DesktopJumpList/        # WPF 应用
├── tests/
│   └── DesktopJumpList.Tests/  # 核心逻辑测试
└── docs/                       # 产品和设计说明
```

## 数据设计

快捷项保存在 `%LocalAppData%\\DesktopJumpList\\shortcuts.json`，每项包含：

```json
{
  "id": "唯一标识",
  "name": "项目目录",
  "target": "D:\\\\work\\\\project",
  "type": "folder",
  "group": "开发",
  "icon": "可选图标路径",
  "arguments": "可选启动参数",
  "sortOrder": 0
}
```

## 后续迭代

- 全局快捷键呼出或隐藏面板。
- 从文件资源管理器右键菜单添加目标。
- 导入和导出快捷项配置。
- 多显示器位置记忆。
- 备份和同步配置。

## 开发环境

- Windows 10 或 Windows 11
- .NET 8 SDK
- Visual Studio 2022 或 JetBrains Rider（可选）

创建应用后可使用以下命令运行：

```powershell
dotnet run --project .\src\DesktopJumpList\DesktopJumpList.csproj
```

首次构建发布版本：

```powershell
dotnet publish .\src\DesktopJumpList\DesktopJumpList.csproj -c Release -r win-x64 --self-contained false
```

发布后的程序位于 `src\DesktopJumpList\bin\Release\net8.0-windows\win-x64\publish`。启动该程序后，可从托盘菜单退出，或切换开机启动。

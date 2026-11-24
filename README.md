# Jump · Desktop Jump List

一个极简的 Windows 快捷启动器。常驻系统托盘，屏幕边缘一枚白色小圆点，点开就是常用的文件、文件夹、应用和网页 —— 只为「少翻几层窗口，最快打开常用的东西」。

## 使用

1. **系统托盘常驻**：程序不会在任务栏留下按钮，也不会出现在 Alt+Tab 里。左键单击托盘图标打开面板；右键图标可设置、切换外观/布局、开机启动、打开数据目录或退出。全局快捷键 `Ctrl+Alt+J` 同样可以打开/收起面板。Windows 11 若把新图标折叠进了 `^` 里，可在「设置 → 个性化 → 任务栏 → 其他系统托盘图标」中把 Jump 设为显示。
2. **悬浮白色小圆点**：可拖到屏幕四边（靠近边缘才吸附）或任意摆放；吸附后会自动动画缩成一条贴边细条，鼠标移到边缘即可滑出。支持多显示器，面板会出现在鼠标所在的显示器上。可在设置里调整圆点大小、颜色，或直接隐藏悬浮球，只用托盘/快捷键。
3. **面板**：列出常用条目。直接输入即筛选（支持拼音首字母），`↑` `↓` `←` `→` / `Tab` 选择，`Enter` 打开，`Esc` 返回或收起；在分组内按 `Esc` / `Backspace` 返回上一层。列表支持鼠标滚轮滚动。
4. **添加**：点击 `+` 添加文件 / 应用、文件夹、**Windows 商店应用**；也可以按 `Ctrl+V` 粘贴路径或网址，或直接把文件拖进面板。
5. **整理**：**长按任意磁贴**进入编辑模式，拖动可调整顺序；把一个磁贴拖到另一个上可合并成**分组**（右键分组可重命名/解散）。右键磁贴可固定、重命名、打开所在位置、移除（`Del` 移除后 `Ctrl+Z` 撤销）。
6. **设置**：托盘右键 →「设置…」，或在面板内按 `Ctrl+,`。可修改外观（跟随系统 / 浅色 / 深色）、面板每行数量 / 最大行数、**面板图标大小 / 文字大小 / 间隔**、**悬浮球显示 / 大小 / 颜色**、自动收起延时、全局快捷键、开机启动。目标已失效的磁贴会显示警告角标，悬停可看完整路径。

## 安装

下载 Release 中的 `Jump-*-setup.exe` 安装包，一路下一步即可。安装程序会创建桌面和开始菜单快捷方式，并可勾选「开机自动启动」。程序只会在系统托盘运行，不会占用任务栏。

也可以使用 `Jump-*-win-x64-portable.zip` 免安装运行。

## 数据

所有数据只保存在本机，不联网、不上传：

```text
%LocalAppData%\DesktopJumpList\
├── shortcuts.json   # 快捷项（含固定状态、使用次数、最近打开时间）
└── settings.json    # 贴边、悬浮球、面板、主题、快捷键、布局
```

## 开发

要求：Windows 10/11、.NET 8 SDK。

```powershell
dotnet build   .\src\DesktopJumpList\DesktopJumpList.csproj
dotnet run     --project .\src\DesktopJumpList\DesktopJumpList.csproj
```

界面预览（不需要交互桌面，直接把悬浮圆点 / 面板 / 设置页 / 托盘菜单渲染成 PNG）：

```powershell
dotnet run --project .\src\DesktopJumpList\DesktopJumpList.csproj -- --render-preview .\artifacts\preview
```

发布自包含版本（安装包 / 便携版）：

```powershell
dotnet publish .\src\DesktopJumpList\DesktopJumpList.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish
# 然后用 Inno Setup 6 编译 installer\DesktopJumpList.iss
```

## 许可

个人项目，供学习与自用。

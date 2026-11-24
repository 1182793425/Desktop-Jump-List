# Jump · Desktop Jump List

一个极简的 Windows 快捷启动器。屏幕边缘一枚小圆球，点开就是常用的文件、文件夹、应用和网页 —— 只为「少翻几层窗口，最快打开常用的东西」。

## 使用

1. 圆球可吸附到屏幕四边（靠近边缘松手才吸附）或任意摆放在屏幕上；吸附后悬停片刻会自动吸进边缘、只留一小条，鼠标移过去会自动滑出。支持多显示器。
2. 单击圆球（或按全局快捷键 `Ctrl+Alt+J`），屏幕中间展开面板；输入关键字即筛，点选条目立即打开，点击面板外或 `Esc` 收起。
3. 点击 `+` 添加：文件 / 应用、文件夹、**Windows 商店应用**，或按 `Ctrl+V` 粘贴路径/网址。
4. **长按任意磁贴**进入编辑模式：拖动可调整顺序，把一个磁贴拖到另一个上可合并成**分组**（右键分组可重命名/解散），像苹果桌面一样。
5. 右键圆球可切换外观（跟随系统 / 浅色 / 深色）、布局（每行数量 / 最大行数）、开机自启、打开数据目录或退出。

## 安装

下载 Release 中的 `Jump-*-setup.exe` 安装包，一路下一步即可；会创建桌面快捷方式和开始菜单快捷方式（可右键固定到任务栏）。也可用 `Jump-*-win-x64-portable.zip` 免安装运行。

## 数据

所有数据只保存在本机，不联网、不上传：

```text
%LocalAppData%\DesktopJumpList\
├── shortcuts.json   # 快捷项（含使用次数、最近打开时间）
└── settings.json    # 贴边方向、位置、自动隐藏延时、主题、全局快捷键
```

## 开发

要求：Windows 10/11、.NET 8 SDK。

```powershell
dotnet build   .\src\DesktopJumpList\DesktopJumpList.csproj
dotnet run     --project .\src\DesktopJumpList\DesktopJumpList.csproj
```

发布自包含版本（安装包 / 便携版）：

```powershell
dotnet publish .\src\DesktopJumpList\DesktopJumpList.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish
# 然后用 Inno Setup 6 编译 installer\DesktopJumpList.iss
```

## 许可

个人项目，供学习与自用。

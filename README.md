# Jump · Desktop Jump List

一个极简的 Windows 快捷启动器。屏幕边缘一枚小圆球，点开就是常用的文件、文件夹、应用和网页 —— 只为「少翻几层窗口，最快打开常用的东西」。

## 使用

1. 圆球贴在屏幕边缘，悬停片刻后会自动吸进边缘、只留一小条；鼠标移过去它会自己滑出来。
2. 单击圆球（或按全局快捷键 `Ctrl+Alt+J`），屏幕中间展开面板；输入关键字即筛，点选条目立即打开，点击面板外或 `Esc` 收起。
3. 把文件、文件夹直接拖进面板即可添加；在条目上右键可以打开所在位置、固定、重命名或移除。

## 数据

所有数据只保存在本机，不联网、不上传：

```text
%LocalAppData%\DesktopJumpList\
├── shortcuts.json   # 快捷项（含使用次数、最近打开时间）
└── settings.json    # 贴边方向、位置、自动隐藏延时、全局快捷键
```

## 开发

要求：Windows 10/11、.NET 8 SDK。

```powershell
dotnet build   .\src\DesktopJumpList\DesktopJumpList.csproj
dotnet run     --project .\src\DesktopJumpList\DesktopJumpList.csproj
```

发布单文件自包含版本：

```powershell
dotnet publish .\src\DesktopJumpList\DesktopJumpList.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 许可

个人项目，供学习与自用。

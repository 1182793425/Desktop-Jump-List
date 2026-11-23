# Jump · Desktop Jump List

一个纯净的 Windows 桌面快捷启动器。屏幕边缘一枚小胶囊，点开就是常用的文件、文件夹、应用和网页。

## 使用

1. 胶囊贴在屏幕边缘，按住可以上下拖动，松手自动吸附。
2. 单击展开面板，输入关键字筛选，点选条目即可打开；点击面板外或再次单击胶囊收起。
3. 把文件、文件夹直接拖进面板即可添加；在条目上右键可以编辑或删除。

## 数据

所有数据只保存在本机，不联网、不上传：

```text
%LocalAppData%\DesktopJumpList\
├── shortcuts.json   # 快捷项
└── settings.json    # 主题、贴边位置、快捷键
```

## 开发

要求：Windows 10/11、.NET 8 SDK。

```powershell
dotnet build .\src\DesktopJumpList\DesktopJumpList.csproj
dotnet run   --project .\src\DesktopJumpList\DesktopJumpList.csproj
```

## 许可

个人项目，供学习与自用。

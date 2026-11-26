# Jump · Desktop Jump List

<p align="center"><img src="docs/app-icon.png" width="96" alt="Jump"></p>

<p align="center">常驻屏幕边缘的 Windows 快捷启动器：一个悬浮圆点，收纳并秒开常用的文件、文件夹、应用和网页。</p>

## 功能

- **悬浮球 + 系统托盘**：圆点可吸附任意屏幕边缘，鼠标移入滑出；吸边后自动收成细条，不占任务栏、不进 Alt+Tab。
- **磁贴面板**：图标 + 名称网格，支持拖拽排序、合并分组、固定、最近使用；图标以 48px 从源文件提取，任意 DPI 下清晰，快捷方式不显示小箭头。
- **即时搜索**：中文、英文、拼音首字母均可命中，键盘全程可操作（方向键／Enter／Esc）。
- **本地文件检索 `/s`**：普通搜索不触碰磁盘；`/s 关键词` 检索本地文件、文件夹与已安装应用（含商店应用）。索引目录、扩展名白名单（`.pathactive`）与文件夹黑名单均可配置，系统与缓存目录默认排除。
- **网页搜索**：开启后在结果末尾追加百度／谷歌／必应，回车用默认浏览器打开。
- **外观**：深浅主题、面板透明度、图标／文字／间距可调，悬浮球大小、颜色、圆角、吸附距离与自动收起全部可配。
- **轻量本地**：单进程常驻，数据只存本机 JSON，无账号、不联网。

## 技术栈

- WPF / .NET 8（`net8.0-windows`），自绘控件与动画，PerMonitorV2 DPI 感知
- 图标经 `PrivateExtractIcons` / 注册表 `DefaultIcon` / `imageres.dll` 提取，并解析 `.lnk` 目标
- 安装包由 Inno Setup 6 生成，另有自包含便携版

## 下载

见 [Releases](https://github.com/1182793425/Desktop-Jump-List/releases) 页面：安装包（推荐）或免安装便携版（zip / 7z）。

## 构建

```powershell
dotnet build   .\src\DesktopJumpList\DesktopJumpList.csproj
dotnet publish .\src\DesktopJumpList\DesktopJumpList.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish
# 安装包：用 Inno Setup 6 编译 installer\DesktopJumpList.iss
```

## 许可

个人项目，供学习与自用。

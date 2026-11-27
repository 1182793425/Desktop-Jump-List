# Jump · Desktop Jump List

<p align="center"><img src="docs/app-icon.png" width="96" alt="Jump"></p>

<p align="center">常驻屏幕边缘的 Windows 快捷启动器：一个悬浮圆点，收纳并秒开常用的文件、文件夹、应用和网页。</p>

## 功能

- **悬浮球 + 系统托盘**：圆点可吸附任意屏幕边缘，鼠标移入滑出；吸边后自动收成细条，不占任务栏、不进 Alt+Tab。
- **磁贴面板**：图标 + 名称网格，支持拖拽排序、合并分组、固定、最近使用；图标以 48px 从源文件提取，任意 DPI 下清晰，快捷方式不显示小箭头。
- **即时搜索**：中文、英文、拼音首字母均可命中，键盘全程可操作（方向键／Enter／Esc）。
- **本地文件检索 `/s`**：`/s 关键词` 检索本地文件、文件夹与已安装应用（含商店应用）。索引**只在手动重建时**才建立（搜索框右侧 ⟳、设置里的 ⟳ 或 Ctrl+Enter），不会因为输入或改设置自动扫描磁盘；可选开关让普通搜索也带本地文件结果（面板结果在上、分割线、本地文件在下），两种方式都是分页加载——列表向下滚动即自动补下一批。索引目录、扩展名白名单（`.pathactive`）与黑名单均可配置——黑名单条目可以是文件夹路径，也可以是 `*.bak`、`~$*` 这类通配符；内置还会排除以 `~$` 开头的 Office 临时文件和带「隐藏」「系统」属性的项（回收站 `$I/$R`、系统元数据等），系统与缓存目录默认排除。
- **索引实时增量**：重建之后索引自己保持最新，无需再手动刷新。主键用 NTFS 文件 ID，路径由父级链派生，所以文件夹改名会自动带动整棵子树。变更来源优先用 USN 日志增量（锚点持久化，关机期间的改动也能补上，日志回绕或 JournalId 变化则静默重建）；当前进程读不到日志时改用目录监听兜底。全部跑在低优先级后台线程：用户活跃时拉长轮询，空闲时才追平与静默重建；筛选规则变化后也在空闲时重建。
- **网页搜索**：开启后搜索结果末尾只有一个「跳转」入口，回车进入二级列表再选百度／谷歌／必应等目标，用默认浏览器打开。
- **外观**：深浅主题、面板透明度、图标／文字／间距可调，悬浮球大小、颜色、圆角、吸附距离与自动收起全部可配。
- **轻量本地**：单进程常驻，数据只存本机 JSON，无账号、不联网。

## 技术栈

- WPF / .NET 8（`net8.0-windows`），自绘控件与动画，PerMonitorV2 DPI 感知
- 图标经 `PrivateExtractIcons` / 注册表 `DefaultIcon` / `imageres.dll` 提取，并解析 `.lnk` 目标
- 文件索引：`GetFileInformationByHandleEx(FileIdBothDirectoryInfo)` 枚举（拿到文件 ID，无需逐个打开）、USN 日志增量、`FileSystemWatcher` 兜底、`SetThreadPriority(THREAD_MODE_BACKGROUND_BEGIN)` 低优先级调度
- 安装包由 Inno Setup 6 生成，另有自包含便携版

## 下载

见 [Releases](https://github.com/1182793425/Desktop-Jump-List/releases) 页面。**每个 Release 只提供安装包 `Jump-<version>-setup.exe`**（不再附带 zip / 7z 便携包）。

## 发版规则

应用内置「检查更新」，规则如下，发布 Release 时必须遵守，否则检查更新会失灵：

1. **版本号**：`v主.次.修订`（如 `v3.14.22`）。**唯一来源是仓库根目录的 `.env`**（`[App]` 段的 `APP_VERSION=`），csproj 与安装脚本都自动读取它，不要手改代码里的版本号；tag 与资产名必须与该值完全一致。
2. **Tag**：`v<版本号>` 打在 dev 分支的最新提交上；Release 的 target 分支为 `dev`。
3. **资产命名**：每个 Release 必须携带且只携带一个名为 `Jump-<版本号>-setup.exe` 的安装包（Inno Setup 产物，自包含）。应用按这个后缀识别安装包，名字不符将无法下载更新。
4. **Latest 标记**：应用通过 GitHub `releases/latest` 接口取最新版，该接口只返回**非草稿、非预发布**的最新 Release。所以正式发版不要勾选「pre-release」；替换已发布版本的安装包会清零其下载计数，推荐始终发新版本号。
5. **比较逻辑**：应用把当前版本与 Latest 的版本号逐段比较（数值比较，忽略 `v` 前缀），仅当 Latest **严格大于**当前版本时提示更新并提供下载。已发布后又替换资产但未升版本号的，用户端不会收到更新提示。
6. **Release 说明**：使用中文，格式参照历史版本（「## 修复与改进」+ 要点列表）。

## 构建

```powershell
dotnet build   .\src\DesktopJumpList\DesktopJumpList.csproj
dotnet publish .\src\DesktopJumpList\DesktopJumpList.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish
# 安装包：用 Inno Setup 6 编译 installer\DesktopJumpList.iss
```

## 许可

[MIT License](LICENSE)。个人项目，供学习与自用，欢迎试用、修改与分发。

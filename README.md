# Xpdf WinUI

XpdfReader 是优秀的 PDF 阅读器，老旧的 Qt UI 除外。本项目旨在将 Xpdf 适配到现代的 WinUI 3 界面。

本项目在 Codex 的辅助下完成。

> 这是一个非官方项目，与 Xpdf 的版权所有者 Glyph & Cog, LLC 没有隶属关系。
>
> 本仓库只包含 WinUI 3 适配层，不包含 Xpdf 上游源码。构建时需要先准备原版 Xpdf 4.06 源码。

## 项目简介

Xpdf WinUI 保留 Xpdf 4.06 的 PDF 解析和 Splash 渲染能力，但不再使用旧的 Qt 界面。桌面端界面使用 C#、.NET 8 和 WinUI 3 重写，并通过一个很薄的 C ABI 原生桥接层调用 Xpdf。

项目目前处于早期阶段，主要目标是把 Xpdf 稳定地运行在现代 Windows 桌面界面上，并保持原生渲染性能。

## 本仓库包含什么

- `XpdfReader.WinUI/`：WinUI 3 桌面应用
- `native/`：连接 WinUI 和 Xpdf 的 C ABI 原生桥接层
- `msi/`：WiX 5 MSI 安装包定义
- `build.ps1`：构建原生 DLL 和 WinUI 应用
- `package-msi.ps1`：发布、签名并生成 MSI
- `integrate.ps1`：把本仓库集成到原版 Xpdf 源码树

本仓库不包含上游 Xpdf 源码，也不包含 Xpdf 的 Qt 界面代码。

## 主要功能

- 打开本地 PDF 文件，也支持命令行传入 PDF 路径
- 多标签页阅读，每个标签页独立保存页码、缩放、旋转和显示模式
- 单页、垂直连续、双页单页、双页连续、水平连续等显示模式
- 适应页面、适应宽度、25% 到 400% 缩放
- 页面旋转、页码跳转、上一页和下一页
- PDF 文本搜索和复制
- 文档大纲和页面缩略图侧边栏
- 密码保护的 PDF 支持重试输入密码
- 拖放打开 PDF
- 一体化标题栏，标签页和窗口控制按钮位于同一行

## 项目结构

```text
XPDF-WinUI/
├─ XpdfReader.WinUI/           # WinUI 3 桌面应用
│  ├─ App.xaml                 # 应用入口和全局资源
│  ├─ MainWindow.xaml          # 主窗口、标签页、工具栏和侧边栏
│  ├─ MainWindow.xaml.cs       # 阅读器界面逻辑
│  └─ Services/                # PDF 文档封装和标签页状态
├─ native/                     # C ABI 原生桥接层
│  ├─ xpdf_winui_native.h      # 导出的 C 接口
│  ├─ xpdf_winui_native.cc     # Xpdf 调用、渲染和文本提取实现
│  └─ tests/                   # 原生桥接层测试
├─ msi/                        # WiX MSI 安装包定义
├─ build.ps1                   # 构建原生 DLL 和 WinUI 应用
├─ package-msi.ps1             # 发布、签名并生成 MSI
├─ integrate.ps1               # 集成到原版 Xpdf 源码树
└─ LICENSE                     # GPL v3
```

## 技术架构

### Xpdf 核心

本仓库直接复用原版 Xpdf 4.06 的 C++ 核心，包括：

- `PDFDoc`、`Catalog`、`XRef` 等 PDF 解析组件
- Splash 页面渲染器
- 字体、编码和文本提取相关组件
- 大纲、页面尺寸和权限检查相关组件

WinUI 版本不使用 `xpdf-qt` 中的 Qt 界面，只复用 Xpdf 的核心库和渲染器。

### 原生桥接层

`native/` 是一个 C++17 DLL，通过 `extern "C"` 导出稳定的 C ABI。托管层只依赖这组接口，不直接接触 Xpdf 的 C++ 类型。

主要接口包括：

- `xpdf_open_document` / `xpdf_close_document`
- `xpdf_get_page_count`
- `xpdf_get_page_size` / `xpdf_get_page_sizes`
- `xpdf_render_page`
- `xpdf_render_page_tile`
- `xpdf_get_outline`
- `xpdf_get_page_text`
- `xpdf_free_buffer`
- `xpdf_get_last_error`

其中 `xpdf_render_page_tile` 用于按矩形切片渲染页面，适合后续实现更细粒度的分块加载和局部重绘。所有 Xpdf 访问都会在原生层串行化，因为上游引擎包含进程级共享状态。

### WinUI 3 应用

托管应用使用：

- .NET 8
- C#
- WinUI 3
- Windows App SDK 1.8
- `Microsoft.WindowsAppSDK` 自包含部署

`PdfDocument` 负责 P/Invoke 调用原生桥接层，`PdfSession` 保存每个标签页的独立状态，`MainWindow` 负责标签页、工具栏、搜索、大纲和页面渲染。

页面尺寸、布局和大纲都会按标签页缓存。连续显示模式下只批量查询一次页面尺寸；单页和双页模式只测量当前可见页面，避免打开大型文档时枚举所有页面。

渲染结果会在窗口缩放或切换侧边栏时复用，不会因为界面重新布局就重新解码页面。对于高分辨率扫描件，页面解码是主要耗时来源，因此阅读器会优先复用已经渲染的位图。

## 配合原版 Xpdf 构建

### 1. 准备 Xpdf 4.06 源码

从 Xpdf 官方网站下载并解压 Xpdf 4.06 源码：

```text
https://www.xpdfreader.com/
```

解压后应该能看到：

```text
Xpdf-4.06/
├─ CMakeLists.txt
├─ xpdf/
├─ splash/
├─ fofi/
├─ goo/
├─ xpdf-qt/
└─ ...
```

### 2. 把本仓库放到 Xpdf 源码树中

推荐把本仓库克隆到 Xpdf 源码根目录下的 `xpdf-winui`：

```powershell
cd Xpdf-4.06
git clone https://github.com/bwahoji/XPDF-WinUI.git xpdf-winui
```

如果已经下载了本仓库的 ZIP，也可以把解压后的目录重命名为 `xpdf-winui`，然后放到 Xpdf 源码根目录。

### 3. 集成 CMake

在 Xpdf 源码根目录运行：

```powershell
.\xpdf-winui\integrate.ps1
```

脚本会在 `CMakeLists.txt` 中加入 WinUI 构建开关和原生桥接层：

```cmake
option(XPDF_BUILD_WINUI "Build the native bridge for the WinUI 3 reader" OFF)

if (XPDF_BUILD_WINUI)
  include(CTest)
  add_subdirectory(xpdf-winui/native)
endif ()
```

如果本仓库目录名不是 `xpdf-winui`，`integrate.ps1` 会自动使用实际目录名。

也可以手动修改 `CMakeLists.txt`：把 `option(...)` 放在 `project(xpdf)` 后面，把 `if (XPDF_BUILD_WINUI) ... endif ()` 放在 `add_subdirectory(xpdf-qt)` 后面。

### 4. 构建应用

在 Xpdf 源码根目录运行：

```powershell
.\xpdf-winui\build.ps1 -Configuration Release -Platform x64
```

构建脚本会：

1. 配置顶层 CMake 项目，并启用 `XPDF_BUILD_WINUI=ON`
2. 编译 `xpdf_winui_native` 原生 DLL
3. 运行原生桥接层测试
4. 编译 WinUI 3 应用
5. 把原生 DLL 和运行时依赖复制到应用输出目录

### 5. 生成 MSI

```powershell
.\xpdf-winui\package-msi.ps1 -Configuration Release -Platform x64
```

输出目录：

```text
artifacts\winui\package\XPDF-WinUI_0.0.0.1_x64\
├─ XPDF-WinUI_0.0.0.1_x64.msi
├─ XPDF-WinUI_Test.cer
├─ Install-Certificate.ps1
├─ Install-XPDF-WinUI.ps1
└─ README.txt
```

## 构建要求

- Windows 10 1809 或更高版本
- .NET 8 SDK
- CMake 3.10 或更高版本
- C++17 编译器
- 与编译器匹配的 FreeType 开发库
- Windows App SDK NuGet 依赖

适配层目录或 Xpdf 源码根目录下的 `.tools` 可以放置本地工具链和依赖。存在以下目录时，构建脚本会自动使用：

- `.tools\cmake`
- `.tools\dotnet`
- `.tools\nuget`
- `.tools\freetype`

如果使用仓库自带的 FreeType，默认会选择 Ninja 和 MSYS2 UCRT64 编译器。使用其他编译器时，需要提供匹配的 FreeType：

```powershell
.\xpdf-winui\build.ps1 `
  -FreetypeDir D:\deps\freetype `
  -CompilerBinDir C:\msys64\ucrt64\bin `
  -Generator Ninja `
  -Configuration Release `
  -Platform x64
```

如果编译出的原生 DLL 依赖 GCC 运行时，可以显式复制这些 DLL：

```powershell
.\xpdf-winui\build.ps1 `
  -RuntimeDependencyDir C:\msys64\ucrt64\bin `
  -RuntimeDependencyName libgcc_s_seh-1.dll,libstdc++-6.dll,libwinpthread-1.dll `
  -Configuration Release `
  -Platform x64
```

只构建、不运行测试：

```powershell
.\xpdf-winui\build.ps1 -Configuration Release -Platform x64 -SkipTests
```

离线构建已经还原过的项目：

```powershell
.\xpdf-winui\build.ps1 -Configuration Release -Platform x64 -NoRestore
```

## 使用正式证书签名

如果已经有代码签名 PFX：

```powershell
.\xpdf-winui\package-msi.ps1 `
  -CertificatePath C:\path\to\codesign.pfx `
  -CertificatePassword '<pfx-password>'
```

脚本会自动检测并使用该证书，同时保留时间戳签名。`-CertificateSubject` 只用于自动生成测试证书，不会限制外部证书的主题。

离线构建时可以跳过时间戳：

```powershell
.\xpdf-winui\package-msi.ps1 -Configuration Release -Platform x64 -NoTimestamp
```

## 安装

1. 从 GitHub Releases 下载 `XPDF-WinUI_0.0.0.1_x64.msi`。
2. 双击 MSI，按照安装向导完成安装。
3. 安装完成后，可以从开始菜单启动 Xpdf WinUI。

安装程序会：

- 将程序安装到 `Program Files\XPDF-WinUI`
- 创建开始菜单快捷方式
- 注册 XPDF-WinUI 为可用的 PDF 打开程序
- 注册 PDF 文件类型图标

安装程序不会强制覆盖用户已经选择的默认 PDF 应用。如果没有设置默认应用，Windows 可能会在首次打开 PDF 时询问使用哪个应用。

## 开发说明

- 原生桥接层的公开接口在 `native/xpdf_winui_native.h`。
- WinUI 界面逻辑在 `XpdfReader.WinUI/MainWindow.xaml.cs`。
- MSI 安装逻辑在 `msi/Product.wxs`。
- 集成逻辑在 `integrate.ps1`。

## 许可证

本项目采用 GNU General Public License version 3，见 `LICENSE`。

上游 Xpdf 采用 GPL v2 或 GPL v3 双许可证，版权归 Glyph & Cog, LLC 所有。本仓库不包含上游 Xpdf 源码，使用时请遵守原项目的许可证。

本项目不是 Xpdf 官方项目，也不提供 Xpdf 商业许可证。

## 致谢

- Xpdf 及其作者 Glyph & Cog, LLC
- FreeType 字体引擎
- Microsoft WinUI 3 和 Windows App SDK
- WiX Toolset
- Codex 在本项目开发过程中的辅助

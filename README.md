# Xpdf WinUI

XpdfReader 是优秀的 PDF 阅读器，老旧的 Qt UI 除外。本项目旨在将 Xpdf 适配到现代的 WinUI 3 界面。（这个项目和 Xpdf 官方没有任何关系！）

本项目在 Codex 的辅助下完成。

## 项目简介

本项目基于 Xpdf 4.06，保留了其解析与渲染能力，只是在其基础上将 Qt 界面替换为了 WinUI 3。WinUI 3 的部分通过一层桥接层来调用 Xpdf。由于本项目含有许许多多的 AI 生成内容并且开发时间很短，所以问题大概率也不少。

## 现在能做什么？

- 打开 PDF
  - 密码保护也可以打开
  - 可以拖进去也可以选择（好基本啊）
- ~~关掉 PDF（废话）~~
- XpdfReader 原本就有的五种显示模式（这也是我开始用 XpdfReader 的原因）：
  - 单页
  - 双页
  - 单页垂直连续
  - 双页垂直连续
  - **水平连续**
- 大小调整（目前还没有自由调整，只有几种固定模式，抱歉啦）
- 页面的旋转和跳转（十分基本）
- 文档内文本搜索复制（还没有测试过，因为手头全是扫描版课本……）
- 能显示大纲和页面的侧边栏（现在大纲还有些显示问题）

## 这里都有些什么？

以下是 AI 生成的目录介绍：

- `XpdfReader.WinUI/`：WinUI 3 桌面应用
- `native/`：连接 WinUI 和 Xpdf 的 C ABI 原生桥接层
- `msi/`：WiX 5 MSI 安装包定义
- `build.ps1`：构建原生 DLL 和 WinUI 应用
- `package-msi.ps1`：发布并生成 MSI，可选使用 PFX 签名
- `integrate.ps1`：把本仓库集成到原版 Xpdf 源码树

如上，这里并没有 Xpdf 的源码，只有 WinUI 的部分。

## 如何构建？

就像上面两行的位置说的那样，这里并没有 Xpdf 的源码，所以请自行下载。

```
https://www.xpdfreader.com/
```

解压出来就是：

```
Xpdf-4.06/
├─ CMakeLists.txt
├─ xpdf/
├─ splash/
├─ fofi/
├─ goo/
├─ xpdf-qt/
└─ ...
```

然后把本仓库克隆到 ``Xpdf-4.06`` 下

```
cd Xpdf-4.06
git clone https://github.com/bwahoji/XPDF-WinUI.git xpdf-winui
```

然后运行仓库里附带的修改脚本，这样可以在原项目的 ``CMakeLists.txt`` 下加入本项目所需要的一些配置。

```
.\xpdf-winui\integrate.ps1
```

然后构建就好啦！

```
.\xpdf-winui\build.ps1 -Configuration Release -Platform x64
```

在装有 WiX 5 的主机上，还可以生成可安装的 MSI：

```
.\xpdf-winui\package-msi.ps1 -Configuration Release -Platform x64
```

输出应该在：

```
artifacts\winui\package\XPDF-WinUI_0.0.0.1_x64\
└─ XPDF-WinUI_0.0.0.1_x64.msi
```

## 致谢

- Xpdf 及其作者 Glyph & Cog, LLC，谢谢
- FreeType 字体引擎，谢谢
- Microsoft WinUI 3 和 Windows App SDK，谢谢
- WiX Toolset，谢谢
- Codex 在本项目开发过程中的辅助，谢谢

## 最后附上由 AI 撰写的 “技术架构” 介绍

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
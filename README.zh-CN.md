# osu! Storyboard Studio

[English](README.md) | 简体中文

用于制作和预览 osu! 故事板的 Windows 桌面编辑器。

## 开发状态

> **当前为测试版，仅供体验。** 项目仍在开发中，功能与设计随时可能重构或调整，请谨慎使用。使用前请备份重要工程与素材。

## 主要功能

- **工程与素材管理：** 新建和保存工程、导入谱面文件夹、切换难度，管理图片、动画、音频和视频素材。
- **故事板可视化编辑：** 通过时间轴、预览和属性面板编辑精灵、命令与关键帧，支持撤销重做。
- **编组与自定义缓动曲线：** 支持嵌套编组、整体运动和自定义缓动曲线。
- **Timing工具：** BPM/Offset 编辑与计算、Timing 红线管理、节拍辅助音和 BPM 分析。
- **视频导出功能：** 渲染带音乐和故事板音效的视频。

## 外部引用包

主程序与效果宿主直接引用的 NuGet 包：

| 包名 | 版本 |
| --- | --- |
| [ppy.ManagedBass](https://www.nuget.org/packages/ppy.ManagedBass/2022.1216.0) | 2022.1216.0 |
| [ppy.ManagedBass.Fx](https://www.nuget.org/packages/ppy.ManagedBass.Fx/2022.1216.0) | 2022.1216.0 |
| [ppy.osu.Framework.NativeLibs](https://www.nuget.org/packages/ppy.osu.Framework.NativeLibs/2025.806.0-nativelibs) | 2025.806.0-nativelibs |
| [Silk.NET.Windowing](https://www.nuget.org/packages/Silk.NET.Windowing/2.22.0) | 2.22.0 |
| [Silk.NET.Input](https://www.nuget.org/packages/Silk.NET.Input/2.22.0) | 2.22.0 |
| [Silk.NET.OpenGL](https://www.nuget.org/packages/Silk.NET.OpenGL/2.22.0) | 2.22.0 |
| [Silk.NET.GLFW](https://www.nuget.org/packages/Silk.NET.GLFW/2.22.0) | 2.22.0 |
| [ImGui.NET](https://www.nuget.org/packages/ImGui.NET/1.90.8.1) | 1.90.8.1 |
| [StbImageSharp](https://www.nuget.org/packages/StbImageSharp/2.30.15) | 2.30.15 |
| [NAudio](https://www.nuget.org/packages/NAudio/2.2.1) | 2.2.1 |
| [NativeFileDialogSharp](https://www.nuget.org/packages/NativeFileDialogSharp/0.5.0) | 0.5.0 |
| [FFmpeg.AutoGen](https://www.nuget.org/packages/FFmpeg.AutoGen/7.1.1) | 7.1.1 |
| [Sdcb.FFmpeg.runtime.windows-x64](https://www.nuget.org/packages/Sdcb.FFmpeg.runtime.windows-x64/7.1.0) | 7.1.0 |
| [Microsoft.CodeAnalysis.CSharp](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.0.0) | 5.0.0 |

## 版权声明

版权所有 © 2026 wanjia。

第三方包、库及素材的版权归各自权利人所有，并保留各自原有的许可条款。

- BASS 与 BASS_FX 由 `ppy.osu.Framework.NativeLibs` 提供，保留原有许可。随附的 FFmpeg 运行库为 GPL 构建。
- osu! 素材版权归 ppy Pty Ltd 所有。参见[默认皮肤许可](src/SbStudio.Resources/Assets/DefaultSkin/!LICENCE.txt)、[osu! 资源许可](src/SbStudio.Resources/Assets/Argon/osu-resources-LICENCE.txt)和 [osu! 源码归属声明](src/SbStudio.Resources/Assets/Argon/osu-MIT-LICENCE.txt)。
- Font Awesome 由 Fonticons, Inc. 提供。参见 [Font Awesome 许可](src/SbStudio.Resources/Assets/Argon/FontAwesome-LICENCE.txt)。

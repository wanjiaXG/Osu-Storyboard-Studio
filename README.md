# osu! Storyboard Studio

English | [简体中文](README.zh-CN.md)

A Windows desktop editor for creating and previewing osu! storyboards.

## Development status

> **Beta — for evaluation only.** This project is under active development. Features and design may be reworked or refactored at any time. Please use with caution and back up important projects and assets before use.

## Main features

- **Project and asset management:** create and save projects, import beatmap folders, switch difficulties, and manage images, animations, audio and video assets.
- **Visual storyboard editing:** edit sprites, commands and keyframes through the timeline, preview and properties panels, with undo/redo support.
- **Groups and custom easing curves:** nested groups, shared motion and custom easing curves.
- **Timing tools:** BPM/Offset editing and calculation, Timing point management, beat clicks and BPM analysis.
- **Video export:** render videos with music and storyboard audio.

## External packages

Direct NuGet dependencies of the application and effects host:

| Package | Version |
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

## Copyright

Copyright © 2026 wanjia.

Third-party packages, libraries and assets retain their respective copyrights and license terms.

- BASS and BASS_FX are provided through `ppy.osu.Framework.NativeLibs` and retain their original licenses. The bundled FFmpeg runtime is a GPL build.
- osu! assets are copyright ppy Pty Ltd. See the [default skin license](src/SbStudio.Resources/Assets/DefaultSkin/!LICENCE.txt), [osu! resource license](src/SbStudio.Resources/Assets/Argon/osu-resources-LICENCE.txt) and [osu! source attribution](src/SbStudio.Resources/Assets/Argon/osu-MIT-LICENCE.txt).
- Font Awesome by Fonticons, Inc. See the [Font Awesome license](src/SbStudio.Resources/Assets/Argon/FontAwesome-LICENCE.txt).

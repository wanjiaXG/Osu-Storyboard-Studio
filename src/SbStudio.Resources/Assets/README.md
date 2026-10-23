# 内置资源

此目录由 `SbStudio.Resources` 项目统一管理，保存内置资源的源码。构建和发布无需根目录的 `Default Skin` 或 `Skins` 文件夹。

- `DefaultSkin/`：从默认皮肤模板中选取的预览用素材、音效、配置及原始版权说明；嵌入 `SbStudio.Resources.dll`，供默认皮肤和自定义皮肤回退使用。
- `Skins/README.txt`：自定义皮肤使用说明；构建后位于 `Skins/README.txt`。
- `Argon/`：lazer 预览的图片、字体图集、音效和版权说明，同样嵌入 `SbStudio.Resources.dll`。

发布时只分发资源 DLL，不再复制 `Assets/Argon/` 或 `Skins/default/`。图片和配置直接从资源流读取，内置音效通过 BASS 从内存解码，不会释放为散装文件。源码中的图片、音效仍保留在这里，便于编辑。

用户皮肤统一放在 exe 所在目录的 `Skins/` 中，导入、扫描和“打开 Skins 文件夹”使用同一路径；运行源码时也不会改用 `run.bat` 旁的目录。

默认皮肤只保留圆圈、数字、滑条，以及 taiko、mania、catch 预览实际读取的图片和击打/滑条/转盘音效，包括动画帧与原有高分辨率版本。未使用的菜单、光效、评分素材和其他模板子目录不随项目分发。版权说明见 `DefaultSkin/!LICENCE.txt` 和 `DefaultSkin/!README.txt`。

根目录的完整 `Default Skin/` 已被 `.gitignore` 排除。新增内置素材请直接放入此目录，不要新增指向根目录原始素材的构建引用。NuGet 原生依赖仍由包管理器提供；翻译文件仍以 `src/SbStudio.Core/Lang/` 为唯一源码来源。用户选择的谱面、皮肤和系统字体继续按各自路径读取。

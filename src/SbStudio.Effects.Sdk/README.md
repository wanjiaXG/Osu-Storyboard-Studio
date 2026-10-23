# 编写效果

在效果库选择「管理效果 → 新建效果」，即可得到可运行的源码目录、动画素材和 IDE 工程。编辑器监视这个目录；保存 `.cs`、素材或 `effect.json` 后约 300 ms 开始更新。源码编译使用 C# 14 / .NET 10 与固定 Roslyn 5.0.0，不执行 MSBuild、NuGet 还原、分析器或源码生成器。使用 IDE 时可以自行安装 .NET 10 SDK。

发布的效果与源码效果实现同一接口：

```csharp
using SbStudio.Effects;

public sealed class MyEffect : IEffectModule
{
    private ParameterKey<int> count = null!;
    private ParameterKey<AssetReference> image = null!;

    public void Define(EffectDefinitionBuilder d)
    {
        d.Name("My particles").Category("Particles").AdjustableDuration(5000);
        count = d.Integer("count").Default(20).Range(1, 1000).Label("Count").Key;
        image = d.Image("image").Default(AssetReference.Package("assets/particle.png")).Key;
    }

    public void Generate(EffectContext ctx, StoryboardBuilder b)
    {
        // This constant is deliberately not exposed in the editor.
        const double speed = 50;
        var random = ctx.Random(123);
        for (int i = 0; i < ctx.Parameters.Get(count); i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            double x = random.Between(0, 640);
            b.Sprite(ctx.Parameters.Get(image))
                .Move(0, ctx.Duration, x, 0, x, speed * ctx.Duration / 1000)
                .Scale(0, .3)
                .Fade(0, ctx.Duration, 1);
        }
    }
}
```

目录中的 `effect.json` 指定稳定的包 ID、效果 ID 和入口类型，不扫描所有类来猜入口：

```json
{
  "formatVersion": 1,
  "packageId": "example.particles",
  "packageVersion": "1.0.0",
  "sdkMajor": 1,
  "minHostVersion": "1.0.0",
  "entryAssembly": "lib/Effect.dll",
  "effects": [{ "effectId": "example.particles.snow", "entryType": "MyEffect" }],
  "dependencies": []
}
```

把图片放在 `assets/particle.png`。可将代码拆为多个 `.cs`，包括自己的扩展方法；`bin/`、`obj/`、`.git/`、`.build/`、`.vs/` 不参与源码编译。纯托管依赖放在 `lib/` 并显式列入 `dependencies`；不要携带另一份 SDK。一个包可声明多个效果入口。

`Define` 只描述参数和输入，必须保持确定性。使用稳定参数 ID，不用显示名称作为 ID。可声明 Integer、Number、Time、Toggle、Choice、Text、Multiline、Point2、Color、Image、Animation、File，配合范围、默认值、单位、分组、条件显示。只有声明的参数才可以在软件中修改；未声明种子/速度不会自动出现控件。Choice/Toggle 可作为显示条件。类型与参数 ID 应保持版本兼容，未知参数会保留以便后续恢复。

`Generate` 每次收到新的模块实例、参数快照和独立 Builder；不能依赖上次运行的字段或全局生成列表。用 `ctx.Random(seed)` 获得可复现的随机序列。检查 `ctx.Cancellation` 可快速响应用户取消；不合作的代码会由宿主结束工作进程。`ctx.Log(...)` 的输出有长度上限，可在实例属性中查看。

Builder 保留链式 API：Sprite/Animation、嵌套 Group、Move/MoveX/MoveY、Fade、Scale/ScaleXY、Rotate/RotateRad、Color、FlipX/FlipY/Additive、Loop、Trigger 及编组深度命令。Loop/Trigger 的回调只能添加自己的命令，不允许嵌套命令块；封存后的 Builder 不能继续修改。默认图层为 Background。输入的时间单位为毫秒，转入编辑器时四舍五入到整数毫秒，半数远离零；Rotate 使用度，RotateRad 使用弧度。

全部命令使用效果局部时间，编辑器物化时只加一次放置时间。只有调用 `Uses(EffectInputs.AbsoluteTime)` 的效果才应读取绝对时间；它们随实例时间移动重新生成。Timing/HitObjects/Metadata 也需要在 `Define` 用 `Uses(...)` 声明。输入难度在添加时绑定，切换预览难度不会更换这个输入。标准物件坐标用 `ctx.StoryboardPosition(hit)` 转成故事板坐标（+64,+56）；其他模式保留原坐标，由作者解释。

`.AdjustableDuration(defaultMs)` 允许用户调整时长；固定内容效果不提供通用的时长编辑。用户加在效果编组外层的运动与生成内容独立，刷新不会覆盖。

资源用 `AssetReference.Project(...)`、`.Package(...)` 或 `ctx.Assets.Write(...)` 返回的生成引用。只使用相对路径，不使用绝对路径或 `..`。`ctx.Assets.Read` 与 `ImageSize` 可以读取资源；输出所引用的图片和动画帧会收集到工程的 `assets/generated/effects/`，因此解除绑定后也能使用。动画路径是基名，例如 `assets/frame.png`，实际帧为 `frame0.png`、`frame1.png`。

在效果库右键开发效果，选择「导出效果包」。生成的 `.sbeffect` 包含 DLL、静态参数清单、声明依赖、资源和文件哈希，安装时校验并固定版本。相同包 ID/版本不能覆盖不同内容，修改后应递增版本号。更新安装包不会自动升级旧工程实例。源码、DLL 与运行的输入相同时应得到相同结果。

执行故障不会替换上次成功输出。保存可以同时保留当前参数和旧画面，重开后会重新生成。转换普通编组保留整个生成动画与外层运动；转换后编辑的内容不会反推成效果参数。分享完整工程时包含 `effects/` 与 `assets/`；游戏导出只输出故事板和所需素材。

第三方代码运行在独立进程，但拥有当前用户的文件和网络权限。请只安装和授权可信来源。

效果包可以附带 `presets/*.sbpreset`，用户在实例的「管理效果 → 随包预设」中选择。先在软件中保存一份参数预设，再放入源码目录的 `presets/` 后打包即可；预设只保存公开参数与可迁移素材，不携带放置时间、外层运动或信任授权。内置效果各附一套示例预设。

SDK V1 的程序集身份固定为 `SbStudio.Effects.Sdk, Version=1.0.0.0`，与编辑器产品版本独立。托管依赖的文件名须匹配程序集简单名称，同包内不得声明重名依赖，也不得私带 SDK DLL。早期试验版 SDK 0.x 编译的效果请重新打包。

源码修改会使用新工作进程；同一版本的工作进程也会定期回收，不能依赖静态变量跨次持久化。编译结果按源码、编译器和 SDK 身份缓存并校验完整性。请求临时目录在资源收集完成后清理；工程历史、保存文件和视频导出引用的结果会保留。缓存清理不会删除已物化的素材或工程固定效果包。

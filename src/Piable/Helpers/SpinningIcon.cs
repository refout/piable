using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Styling;
using FluentIcons.Avalonia;

namespace Piable.Helpers;

/// <summary>
/// 持续旋转的图标控件，用于按钮加载态（替代 Avalonia 12 不支持的
/// Style.Animations 方案）。在 MainWindow.axaml 的「保存/测试连接/读取模型」等
/// 异步按钮里，用 <c>&lt;helpers:SpinningIcon Icon="SpinnerIos" .../&gt;</c> 与
/// 静止图标通过 IsVisible 切换：忙碌时显示旋转图标，空闲时显示静止图标。
/// 注意：不可见时动画仍在后台跑（不渲染，开销可忽略）。
/// </summary>
public sealed class SpinningIcon : FluentIcon
{
    public SpinningIcon()
    {
        var rotate = new RotateTransform();
        RenderTransform = rotate;
        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(1),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(RotateTransform.AngleProperty, 0d) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(RotateTransform.AngleProperty, 360d) },
                },
            },
        };

        // 注意：动画必须跑在控件自身（Visual）上，而不是 RotateTransform 上。
        // TransformAnimator.Apply 内部会把 Animatable 转成 Visual 去取 RenderTransform，
        // 若传 rotate 会抛 InvalidCastException。Setter 用 RotateTransform.AngleProperty
        // 由动画系统顺着 RenderTransform 找到这个 RotateTransform 来驱动角度。
        _ = animation.RunAsync(this);
    }
}

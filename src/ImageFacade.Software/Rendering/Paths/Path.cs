using System.Numerics;

namespace ImageFacade.Software.Rendering;

/// <summary>
/// 通用轮廓：由 <see cref="PathBuilder"/> 构建，命令序列不可变。
/// 曲线在展平时按 <see cref="DefaultFlatteningTolerance"/> 自适应细分。
/// </summary>
internal sealed class Path : IPath, IFlattenable
{
    /// <summary>展平容差（像素）。0.25 在 8 位画布上足够，再小只是浪费折线点数。</summary>
    public const float DefaultFlatteningTolerance = 0.25f;

    private readonly PathCommand[] _commands;
    private FlattenedPath? _flattened;
    private RectF? _bounds;

    internal Path(PathCommand[] commands)
    {
        _commands = commands;
    }

    public RectF Bounds => _bounds ??= Flattened.Bounds;

    internal FlattenedPath Flattened => _flattened ??= FlattenCore(DefaultFlatteningTolerance);

    public IPath Transform(Matrix3x2 transform)
    {
        var commands = new PathCommand[_commands.Length];
        for (int i = 0; i < _commands.Length; i++)
        {
            PathCommand command = _commands[i];
            commands[i] = new PathCommand(
                command.Kind,
                command.A.Transform(transform),
                command.B.Transform(transform),
                command.C.Transform(transform));
        }

        return new Path(commands);
    }

    void IFlattenable.Flatten(PathFlattener flattener)
    {
        foreach (PathCommand command in _commands)
        {
            switch (command.Kind)
            {
                case PathCommandKind.MoveTo:
                    flattener.MoveTo(command.A);
                    break;
                case PathCommandKind.LineTo:
                    flattener.LineTo(command.A);
                    break;
                case PathCommandKind.CubicTo:
                    flattener.CubicTo(command.A, command.B, command.C);
                    break;
                default:
                    flattener.CloseFigure();
                    break;
            }
        }
    }

    internal FlattenedPath FlattenCore(float tolerance)
    {
        var flattener = new PathFlattener(tolerance);
        ((IFlattenable)this).Flatten(flattener);
        return flattener.Build();
    }

    /// <summary>把任意 <see cref="IPath"/> 展平；内置实现之外的自定义类型会被拒绝。</summary>
    internal static FlattenedPath Flatten(IPath path, float tolerance = DefaultFlatteningTolerance)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path is Path concrete)
        {
            return MathF.Abs(tolerance - DefaultFlatteningTolerance) < float.Epsilon
                ? concrete.Flattened
                : concrete.FlattenCore(tolerance);
        }

        if (path is IFlattenable flattenable)
        {
            var flattener = new PathFlattener(tolerance);
            flattenable.Flatten(flattener);
            return flattener.Build();
        }

        throw new ArgumentException(
            $"不支持的 IPath 实现 {path.GetType().FullName}；请用 PathBuilder 构造 Path。", nameof(path));
    }
}

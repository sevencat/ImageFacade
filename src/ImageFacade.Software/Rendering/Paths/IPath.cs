using System.Numerics;

namespace ImageFacade.Software.Rendering;

/// <summary>
/// 可填充/描边的轮廓。命名与 ImageSharp.Drawing 的 <c>IPath</c> 对齐：
/// 内置实现是 <see cref="Path"/>、<see cref="RectangularPolygon"/>、<see cref="EllipsePolygon"/>、<see cref="Polygon"/>。
/// <para>自定义轮廓请用 <see cref="PathBuilder"/> 构造 <see cref="Path"/>——光栅化器只认内置实现。</para>
/// </summary>
internal interface IPath
{
    /// <summary>轮廓的外接矩形（曲线按展平后的实际几何计算，偏保守但不会偏小）。</summary>
    RectF Bounds { get; }

    /// <summary>按仿射矩阵变换出一个新轮廓，原对象不变。</summary>
    IPath Transform(Matrix3x2 transform);
}

/// <summary>光栅化器只依赖「展平成折线」这一件事，因此单独抽一个 internal 接口。</summary>
internal interface IFlattenable
{
    void Flatten(PathFlattener flattener);
}

internal enum PathCommandKind : byte
{
    MoveTo,
    LineTo,
    CubicTo,
    Close,
}

/// <summary>路径命令。CubicTo 用 A(控制点1)、B(控制点2)、C(终点)；LineTo/MoveTo 用 A。</summary>
internal readonly struct PathCommand
{
    public PathCommand(PathCommandKind kind, PointF a, PointF b = default, PointF c = default)
    {
        Kind = kind;
        A = a;
        B = b;
        C = c;
    }

    public PathCommandKind Kind { get; }

    public PointF A { get; }

    public PointF B { get; }

    public PointF C { get; }
}

/// <summary>展平结果：连续点数组 + 每条子路径的起始索引（末尾多一个 <c>Points.Length</c> 作哨兵）。</summary>
internal sealed class FlattenedPath
{
    public FlattenedPath(PointF[] points, int[] contourStarts, RectF bounds)
    {
        Points = points;
        ContourStarts = contourStarts;
        Bounds = bounds;
    }

    public PointF[] Points { get; }

    /// <summary>长度 = 子路径数 + 1；<c>[i]..[i+1]</c> 是第 i 条子路径的点区间。</summary>
    public int[] ContourStarts { get; }

    public RectF Bounds { get; }
}

using System.Numerics;

namespace ImageFacade.Software.Rendering;

/// <summary>
/// 轴对齐矩形轮廓。命名与 ImageSharp.Drawing 的 <c>RectangularPolygon</c> 对齐；
/// 与整数像素矩形 <see cref="Rectangle"/> 不是一回事（后者用于裁剪与画布尺寸）。
/// </summary>
internal sealed class RectangularPolygon : IPath, IFlattenable
{
    private readonly Path _path;

    public RectangularPolygon(float x, float y, float width, float height)
    {
        _path = new PathBuilder()
            .MoveTo(x, y)
            .LineTo(x + width, y)
            .LineTo(x + width, y + height)
            .LineTo(x, y + height)
            .CloseFigure()
            .Build();
    }

    public RectangularPolygon(PointF location, SizeF size)
        : this(location.X, location.Y, size.Width, size.Height)
    {
    }

    public RectangularPolygon(RectF rect)
        : this(rect.X, rect.Y, rect.Width, rect.Height)
    {
    }

    public RectF Bounds => _path.Bounds;

    public IPath Transform(Matrix3x2 transform) => _path.Transform(transform);

    void IFlattenable.Flatten(PathFlattener flattener) => ((IFlattenable)_path).Flatten(flattener);
}

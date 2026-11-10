using System.Numerics;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>任意多边形（隐式闭合）。与 ImageSharp.Drawing 的 <c>Polygon</c> 对齐。</summary>
internal sealed class Polygon : IPath, IFlattenable
{
    private readonly Path _path;

    public Polygon(params PointF[] points)
        : this((IEnumerable<PointF>)points)
    {
    }

    public Polygon(IEnumerable<PointF> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var builder = new PathBuilder();
        bool first = true;
        foreach (PointF point in points)
        {
            if (first)
            {
                builder.MoveTo(point);
                first = false;
            }
            else
            {
                builder.LineTo(point);
            }
        }

        if (first)
        {
            throw new ArgumentException("多边形至少需要一个点。", nameof(points));
        }

        _path = builder.CloseFigure().Build();
    }

    public RectF Bounds => _path.Bounds;

    public IPath Transform(Matrix3x2 transform) => _path.Transform(transform);

    void IFlattenable.Flatten(PathFlattener flattener) => ((IFlattenable)_path).Flatten(flattener);
}

/// <summary>
/// 椭圆轮廓，四段三次贝塞尔近似（kappa = 0.5522847498，误差远小于 1 像素）。
/// 与 ImageSharp.Drawing 的 <c>EllipsePolygon</c> 对齐。
/// </summary>
internal class EllipsePolygon : IPath, IFlattenable
{
    private const float Kappa = 0.5522847498f;

    private readonly Path _path;

    public EllipsePolygon(PointF center, SizeF size)
        : this(center.X, center.Y, size.Width, size.Height)
    {
    }

    public EllipsePolygon(float centerX, float centerY, float width, float height)
    {
        float rx = width * 0.5f;
        float ry = height * 0.5f;
        float kx = rx * Kappa;
        float ky = ry * Kappa;

        _path = new PathBuilder()
            .MoveTo(centerX + rx, centerY)
            .CubicTo(centerX + rx, centerY + ky, centerX + kx, centerY + ry, centerX, centerY + ry)
            .CubicTo(centerX - kx, centerY + ry, centerX - rx, centerY + ky, centerX - rx, centerY)
            .CubicTo(centerX - rx, centerY - ky, centerX - kx, centerY - ry, centerX, centerY - ry)
            .CubicTo(centerX + kx, centerY - ry, centerX + rx, centerY - ky, centerX + rx, centerY)
            .CloseFigure()
            .Build();
    }

    public RectF Bounds => _path.Bounds;

    public IPath Transform(Matrix3x2 transform) => _path.Transform(transform);

    void IFlattenable.Flatten(PathFlattener flattener) => ((IFlattenable)_path).Flatten(flattener);
}

/// <summary>圆：<see cref="EllipsePolygon"/> 的便捷包装。</summary>
internal sealed class CirclePolygon : EllipsePolygon
{
    public CirclePolygon(PointF center, float radius)
        : base(center, new SizeF(radius * 2f, radius * 2f))
    {
    }

    public CirclePolygon(float centerX, float centerY, float radius)
        : base(centerX, centerY, radius * 2f, radius * 2f)
    {
    }
}

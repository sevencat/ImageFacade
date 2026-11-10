using System.Numerics;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 圆角矩形：四条直线 + 四个 90° 圆弧（弧在 <see cref="PathBuilder.ArcTo(PointF, float, bool, bool)"/> 里转成 cubic）。
/// 半径会被夹到 <c>min(width, height) / 2</c>，因此极端半径退化成胶囊形而不是自交。
/// </summary>
internal sealed class RoundRectanglePolygon : IPath, IFlattenable
{
    private readonly Path _path;

    public RoundRectanglePolygon(RectF rectangle, float radius)
        : this(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, radius)
    {
    }

    public RoundRectanglePolygon(float x, float y, float width, float height, float radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        float limit = MathF.Min(width, height) * 0.5f;
        float r = Math.Clamp(radius, 0f, limit);

        if (width <= 0f || height <= 0f)
        {
            _path = new PathBuilder().Build();
            return;
        }

        float right = x + width;
        float bottom = y + height;

        if (r < 1e-6f)
        {
            _path = new PathBuilder()
                .MoveTo(x, y)
                .LineTo(right, y)
                .LineTo(right, bottom)
                .LineTo(x, bottom)
                .CloseFigure()
                .Build();
            return;
        }

        _path = new PathBuilder()
            .MoveTo(x + r, y)
            .LineTo(right - r, y)
            .ArcTo(right, y + r, r)
            .LineTo(right, bottom - r)
            .ArcTo(right - r, bottom, r)
            .LineTo(x + r, bottom)
            .ArcTo(x, bottom - r, r)
            .LineTo(x, y + r)
            .ArcTo(x + r, y, r)
            .CloseFigure()
            .Build();
    }

    public RectF Bounds => _path.Bounds;

    public IPath Transform(Matrix3x2 transform) => _path.Transform(transform);

    void IFlattenable.Flatten(PathFlattener flattener) => ((IFlattenable)_path).Flatten(flattener);
}

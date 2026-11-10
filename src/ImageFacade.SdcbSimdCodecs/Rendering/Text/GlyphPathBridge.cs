using ImageFacade.Text.Engine;

namespace ImageFacade.SdcbSimdCodecs.Rendering;

/// <summary>
/// 把文字引擎的字形轮廓（<see cref="GlyphPath"/>/<see cref="PathCollection"/>）
/// 转成核心的 <see cref="Path"/>，让文字走通用填充/描边路径。
/// <para>
/// 快速路径（字形掩码直接当 coverage）与慢路径（轮廓 → 光栅化 → 覆盖）灰度值应当一致，
/// 差异只来自展平容差，测试用容差守住。
/// </para>
/// </summary>
internal static class GlyphPathBridge
{
    public static Path ToPath(GlyphPath glyphPath) => ToPath(glyphPath.ToPathCollection());

    public static Path ToPath(PathCollection collection)
    {
        var builder = new PathBuilder();
        foreach (VectorPath vector in collection)
        {
            builder.MoveTo(vector.Start.X, vector.Start.Y);
            foreach (PathSegment segment in vector.Segments)
            {
                if (segment.Verb == PathVerb.QuadraticTo)
                {
                    builder.QuadraticTo(segment.X1, segment.Y1, segment.X2, segment.Y2);
                }
                else
                {
                    builder.LineTo(segment.X2, segment.Y2);
                }
            }

            if (vector.IsClosed)
            {
                builder.CloseFigure();
            }
        }

        return builder.Build();
    }
}

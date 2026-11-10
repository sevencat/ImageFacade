using ImageFacade.Text.Engine;

namespace ImageFacade;

/// <summary>
/// 字体：TrueType 解析 + 字形光栅化 + 排版/测量。**创建一次、反复复用**，因此不要每次绘制都
/// <see cref="Load"/>：一门字体解析要读几 MB 的表，代价远高于一次绘制。
/// </summary>
/// <remarks>
/// <para>
/// 字形位图按 (码点, 字号) 缓存（上限 <c>CacheLimit</c>，超出整体清空），所以同一字号反复画同一批字只光栅化一次。
/// </para>
/// <para>
/// 通过 <see cref="TextStyle.FontFace"/> 把实例交给后端，托管文字后端（软件后端 / OpenCV 5 适配）直接复用，
/// 不再解析；不传时用 <see cref="BuiltIn"/>（文泉驿微米黑，覆盖中日韩）。
/// </para>
/// </remarks>
public sealed partial class Font
{
    private const int CacheLimit = 4096;

    private readonly TrueTypeFont _font;
    private readonly Dictionary<long, GlyphMask> _maskCache = [];
    private readonly object _gate = new();

    private Font(TrueTypeFont font)
    {
        _font = font;
    }

    private static readonly Lazy<Font> BuiltInInstance = new(
        static () => Load(BuiltInFont.Data.ToArray()),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>从 TTF 字节加载（会解析字体表）。</summary>
    public static Font Load(byte[] data) => new(TrueTypeFont.Load(data));

    /// <summary>从 .ttf 文件加载（会解析字体表）。</summary>
    public static Font LoadFromFile(string path) => new(TrueTypeFont.LoadFromFile(path));

    /// <summary>从流加载（会解析字体表）。</summary>
    public static Font LoadFromStream(Stream stream) => new(TrueTypeFont.LoadFromStream(stream));

    /// <summary>
    /// 内置字体共享实例（文泉驿微米黑，覆盖中日韩）。首次访问时解压并解析字体表，
    /// 之后复用同一实例；内部字形缓存带锁，可跨线程共享。
    /// </summary>
    public static Font BuiltIn => BuiltInInstance.Value;

    /// <summary>字体族名。</summary>
    public string FamilyName => _font.FamilyName;

    /// <summary>em 单位数。</summary>
    public int UnitsPerEm => _font.UnitsPerEm;

    /// <summary>基线到字身顶部的高度（像素）。</summary>
    public float GetAscent(float size) => size * _font.Ascender / _font.UnitsPerEm;

    /// <summary>基线到字身底部的距离（像素，通常为负）。</summary>
    public float GetDescent(float size) => size * _font.Descender / _font.UnitsPerEm;

    /// <summary>单行行高（像素），含行间距。</summary>
    public float GetLineHeight(float size)
        => size * (_font.Ascender - _font.Descender + _font.LineGap) / _font.UnitsPerEm;

    /// <summary>码点是否有字形。</summary>
    public bool HasGlyph(int codePoint) => _font.HasGlyph(codePoint);

    /// <summary>
    /// 取字形覆盖位图。<paramref name="size"/> 是 em 高度（像素），例如 16、24、40。
    /// </summary>
    internal GlyphMask GetGlyphMask(int codePoint, float size)
    {
        var key = ((long)codePoint << 32) | (uint)(int)MathF.Round(size * 4f);
        lock (_gate)
        {
            if (_maskCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var mask = Rasterize(codePoint, size);

        lock (_gate)
        {
            if (_maskCache.Count >= CacheLimit)
            {
                _maskCache.Clear();
            }

            _maskCache[key] = mask;
        }

        return mask;
    }

    /// <summary>
    /// 取字形矢量轮廓（已缩放到像素、y 轴向下、原点在笔位的基线上）。
    /// 想自己用 GDI/Skia/GPU 绘制时用这个，不经过本库的光栅化。
    /// </summary>
    internal GlyphPath GetGlyphPath(int codePoint, float size)
    {
        var glyphIndex = _font.GetGlyphIndex(codePoint);
        if (!_font.TryGetOutline(glyphIndex, out var outline))
        {
            return EmptyPath;
        }

        return BuildPath(outline, size / _font.UnitsPerEm);
    }

    /// <summary>
    /// 取字形矢量路径集合（已缩放到像素、y 轴向下、原点在笔位基线上）。
    /// 内容与 <see cref="GetGlyphPath"/> 一致，但按轮廓切分成多条 <see cref="VectorPath"/>，
    /// 便于逐条路径绘制、做几何运算或导出 SVG。
    /// </summary>
    internal PathCollection GetGlyphPaths(int codePoint, float size)
        => GetGlyphPath(codePoint, size).ToPathCollection();

    /// <summary>字形前进宽度（像素）。</summary>
    public float GetAdvance(int codePoint, float size)
    {
        var glyphIndex = _font.GetGlyphIndex(codePoint);
        return _font.GetAdvanceWidth(glyphIndex) * size / _font.UnitsPerEm;
    }

    private GlyphMask Rasterize(int codePoint, float size)
    {
        var glyphIndex = _font.GetGlyphIndex(codePoint);
        var scale = size / _font.UnitsPerEm;
        var advance = (int)MathF.Round(_font.GetAdvanceWidth(glyphIndex) * scale);

        if (glyphIndex == 0 || !_font.TryGetOutline(glyphIndex, out var outline))
        {
            return GlyphMask.Blank(advance);
        }

        return GlyphRasterizer.Rasterize(outline, scale, advance);
    }

    private static readonly GlyphPath EmptyPath = new([], 0f, 0f, 0f, 0f);

    private static GlyphPath BuildPath(GlyphOutline outline, float scale)
    {
        var segments = new List<PathSegment>(outline.ContourCount * 8);
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        for (var c = 0; c < outline.ContourCount; c++)
        {
            var points = outline.Contours[c];
            var count = points.Count;
            if (count < 2)
            {
                continue;
            }

            var startIndex = -1;
            for (var i = 0; i < count; i++)
            {
                if (points[i].OnCurve)
                {
                    startIndex = i;
                    break;
                }
            }

            float startX;
            float startY;
            int first;
            int length;
            if (startIndex >= 0)
            {
                startX = points[startIndex].X * scale;
                startY = -points[startIndex].Y * scale;
                first = (startIndex + 1) % count;
                length = count - 1;
            }
            else
            {
                startX = (points[count - 1].X + points[0].X) * 0.5f * scale;
                startY = -(points[count - 1].Y + points[0].Y) * 0.5f * scale;
                first = 0;
                length = count;
            }

            Track(startX, startY);
            segments.Add(new PathSegment(PathVerb.MoveTo, 0f, 0f, startX, startY));

            var currentX = startX;
            var currentY = startY;
            var controlX = 0f;
            var controlY = 0f;
            var hasControl = false;

            for (var k = 0; k < length; k++)
            {
                var point = points[(first + k) % count];
                var x = point.X * scale;
                var y = -point.Y * scale;
                Track(x, y);

                if (point.OnCurve)
                {
                    if (hasControl)
                    {
                        segments.Add(new PathSegment(PathVerb.QuadraticTo, controlX, controlY, x, y));
                        hasControl = false;
                    }
                    else
                    {
                        segments.Add(new PathSegment(PathVerb.LineTo, 0f, 0f, x, y));
                    }

                    currentX = x;
                    currentY = y;
                }
                else
                {
                    if (hasControl)
                    {
                        var midX = (controlX + x) * 0.5f;
                        var midY = (controlY + y) * 0.5f;
                        Track(midX, midY);
                        segments.Add(new PathSegment(PathVerb.QuadraticTo, controlX, controlY, midX, midY));
                        currentX = midX;
                        currentY = midY;
                    }

                    controlX = x;
                    controlY = y;
                    hasControl = true;
                }
            }

            if (hasControl)
            {
                segments.Add(new PathSegment(PathVerb.QuadraticTo, controlX, controlY, startX, startY));
            }
            else if (currentX != startX || currentY != startY)
            {
                segments.Add(new PathSegment(PathVerb.LineTo, 0f, 0f, startX, startY));
            }

            segments.Add(new PathSegment(PathVerb.Close, 0f, 0f, startX, startY));
        }

        if (segments.Count == 0)
        {
            return EmptyPath;
        }

        return new GlyphPath(segments, minX, minY, maxX, maxY);

        void Track(float x, float y)
        {
            if (x < minX)
            {
                minX = x;
            }

            if (x > maxX)
            {
                maxX = x;
            }

            if (y < minY)
            {
                minY = y;
            }

            if (y > maxY)
            {
                maxY = y;
            }
        }
    }
}

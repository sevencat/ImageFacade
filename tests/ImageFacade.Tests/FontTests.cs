using ImageFacade.Backends;
using ImageFacade.SdcbSimdCodecs;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// 核心字体（<see cref="Font"/>）的加载/复用/测量，以及 <see cref="TextStyle.FontFace"/> 的优先级。
/// </summary>
public sealed class FontTests
{
    private static ImageEngine CreateEngine()
        => new(new SoftwareBackend(), new ImageEngineOptions { Fallback = FallbackMode.Disabled });

    private static int CountInk(FacadeImage image)
    {
        byte[] pixels = image.CopyPixels();
        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 128 && pixels[i + 1] < 128 && pixels[i + 2] < 128)
            {
                ink++;
            }
        }

        return ink;
    }

    [Fact]
    public void BuiltIn_Is_A_Shared_Singleton()
    {
        Assert.Same(Font.BuiltIn, Font.BuiltIn);
        Assert.True(Font.BuiltIn.UnitsPerEm > 0);
        Assert.False(string.IsNullOrWhiteSpace(Font.BuiltIn.FamilyName));
    }

    [Fact]
    public void BuiltIn_Has_Cjk_Glyphs()
    {
        Assert.True(Font.BuiltIn.HasGlyph('中'));
        Assert.True(Font.BuiltIn.HasGlyph('A'));
    }

    [Fact]
    public void Load_From_Data_Matches_BuiltIn_Metrics()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(10, 10, Rgba32.White);

        Font loaded = Font.Load(BuiltInFont.Data.ToArray());
        var fromRef = new TextStyle { Size = 24, Color = Rgba32.Black };
        var fromFace = new TextStyle { Size = 24, Color = Rgba32.Black, FontFace = loaded };

        SizeF a = image.MeasureText("中文Ab", fromRef);
        SizeF b = image.MeasureText("中文Ab", fromFace);

        Assert.Equal(a.Width, b.Width, 3);
        Assert.Equal(a.Height, b.Height, 3);
        Assert.Equal(Font.BuiltIn.FamilyName, loaded.FamilyName);
    }

    [Fact]
    public void FontFace_Takes_Precedence_Over_Unresolvable_FontRef()
    {
        using var engine = CreateEngine();
        using FacadeImage image = engine.Create(200, 80, Rgba32.White);

        // 系统里不存在这个字体族：没有 FontFace 时软后端会退回内置字体，
        // 这里用 FontFace 明确接管，验证优先级与"不用每次创建字体"的复用路径
        var style = new TextStyle
        {
            Font = FontRef.Family("NoSuchFamily-For-ImageFacade-Tests"),
            FontFace = Font.BuiltIn,
            Size = 32,
            Color = Rgba32.Black,
        };

        SizeF measured = image.MeasureText("中文Ab", style);
        image.DrawText("中文Ab", new PointF(8, 8), style);

        Assert.True(measured.Width > 0f);
        Assert.True(CountInk(image) > 50);
    }

    [Fact]
    public void Repeated_Draws_With_Same_Font_Instance_Are_Stable()
    {
        using var engine = CreateEngine();
        using FacadeImage first = engine.Create(160, 60, Rgba32.White);
        using FacadeImage second = engine.Create(160, 60, Rgba32.White);

        Font font = Font.Load(BuiltInFont.Data.ToArray());
        var style = new TextStyle { Size = 28, Color = Rgba32.Black, FontFace = font };

        first.DrawText("复用Ab", new PointF(4, 4), style);
        second.DrawText("复用Ab", new PointF(4, 4), style);

        byte[] a = first.CopyPixels();
        byte[] b = second.CopyPixels();

        Assert.Equal(a, b);
        Assert.Equal(first.MeasureText("复用Ab", style).Width, second.MeasureText("复用Ab", style).Width, 3);
    }

    [Fact]
    public void Load_From_File_Matches_Loaded_Data()
    {
        string path = Path.Combine(Path.GetTempPath(), $"imagefacade-font-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(path, BuiltInFont.Data.ToArray());

        try
        {
            Font fromFile = Font.LoadFromFile(path);
            Assert.Equal(Font.BuiltIn.FamilyName, fromFile.FamilyName);
            Assert.Equal(Font.BuiltIn.GetAscent(20f), fromFile.GetAscent(20f), 3);
            Assert.Equal(Font.BuiltIn.GetLineHeight(20f), fromFile.GetLineHeight(20f), 3);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

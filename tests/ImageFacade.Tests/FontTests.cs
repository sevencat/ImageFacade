using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// 核心字体（<see cref="Font"/>）的加载/复用/测量。纯托管，不依赖任何渲染后端。
/// </summary>
public sealed class FontTests
{
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
    public void BuiltIn_Exposes_Consistent_Metrics()
    {
        Assert.True(Font.BuiltIn.Measure("中文Ab", 24f) > 0f);
        Assert.True(Font.BuiltIn.GetAscent(24f) > 0f);
        Assert.True(Font.BuiltIn.GetLineHeight(24f) > Font.BuiltIn.GetAscent(24f));
        Assert.True(Font.BuiltIn.GetAdvance('A', 24f) > 0f);
    }

    [Fact]
    public void Load_From_Data_Matches_BuiltIn_Metrics()
    {
        Font loaded = Font.Load(BuiltInFont.Data.ToArray());

        Assert.Equal(Font.BuiltIn.FamilyName, loaded.FamilyName);
        Assert.Equal(Font.BuiltIn.Measure("中文Ab", 24f), loaded.Measure("中文Ab", 24f), 3);
        Assert.Equal(Font.BuiltIn.GetAscent(24f), loaded.GetAscent(24f), 3);
        Assert.Equal(Font.BuiltIn.GetLineHeight(24f), loaded.GetLineHeight(24f), 3);
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

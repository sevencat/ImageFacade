namespace ImageFacade.Internal;

/// <summary>六种 Resize 模式的几何规划结果（plan §3.3）。</summary>
internal readonly struct ResizePlan
{
    public ResizePlan(int scaledWidth, int scaledHeight, int outputWidth, int outputHeight, int placeX, int placeY)
    {
        ScaledWidth = scaledWidth;
        ScaledHeight = scaledHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        PlaceX = placeX;
        PlaceY = placeY;
    }

    /// <summary>拉伸后的中间尺寸（这一步交给后端 <c>TryResize</c>）。</summary>
    public int ScaledWidth { get; }

    public int ScaledHeight { get; }

    /// <summary>最终帧尺寸。</summary>
    public int OutputWidth { get; }

    public int OutputHeight { get; }

    /// <summary>拉伸结果左上角在输出帧内的位置（可为负，负值表示裁掉左上部分）。</summary>
    public int PlaceX { get; }

    public int PlaceY { get; }

    /// <summary>true 表示需要额外的"裁剪 / 补边"合成步骤。</summary>
    public bool NeedsComposite
        => PlaceX != 0 || PlaceY != 0 || ScaledWidth != OutputWidth || ScaledHeight != OutputHeight;
}

/// <summary>
/// 把"目标尺寸 + 模式"翻译成"拉伸尺寸 + 贴图位置"，纯几何、可单测。
/// 裁剪/补边由门面用像素搬运完成，所以所有后端的几何完全一致。
/// </summary>
internal static class ResizePlanner
{
    public static ResizePlan Plan(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, ResizeMode mode)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源尺寸必须大于 0。");
        }

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "目标尺寸必须大于 0。");
        }

        switch (mode)
        {
            case ResizeMode.Stretch:
                return new ResizePlan(targetWidth, targetHeight, targetWidth, targetHeight, 0, 0);

            case ResizeMode.Crop:
            {
                float scale = MathF.Max((float)targetWidth / sourceWidth, (float)targetHeight / sourceHeight);
                int scaledWidth = Math.Max(targetWidth, Round(sourceWidth * scale));
                int scaledHeight = Math.Max(targetHeight, Round(sourceHeight * scale));
                int cropX = (scaledWidth - targetWidth) / 2;
                int cropY = (scaledHeight - targetHeight) / 2;
                return new ResizePlan(scaledWidth, scaledHeight, targetWidth, targetHeight, -cropX, -cropY);
            }

            case ResizeMode.Pad:
            case ResizeMode.BoxPad:
            {
                float scale = MathF.Min((float)targetWidth / sourceWidth, (float)targetHeight / sourceHeight);
                int scaledWidth = mode == ResizeMode.BoxPad
                    ? Math.Clamp((int)MathF.Floor(sourceWidth * scale), 1, targetWidth)
                    : Math.Clamp(Round(sourceWidth * scale), 1, targetWidth);
                int scaledHeight = mode == ResizeMode.BoxPad
                    ? Math.Clamp((int)MathF.Floor(sourceHeight * scale), 1, targetHeight)
                    : Math.Clamp(Round(sourceHeight * scale), 1, targetHeight);

                return new ResizePlan(
                    scaledWidth,
                    scaledHeight,
                    targetWidth,
                    targetHeight,
                    (targetWidth - scaledWidth) / 2,
                    (targetHeight - scaledHeight) / 2);
            }

            case ResizeMode.Max:
            {
                float scale = MathF.Min((float)targetWidth / sourceWidth, (float)targetHeight / sourceHeight);
                int scaledWidth = Math.Max(1, Round(sourceWidth * scale));
                int scaledHeight = Math.Max(1, Round(sourceHeight * scale));
                return new ResizePlan(scaledWidth, scaledHeight, scaledWidth, scaledHeight, 0, 0);
            }

            case ResizeMode.Min:
            {
                float scale = MathF.Max((float)targetWidth / sourceWidth, (float)targetHeight / sourceHeight);
                int scaledWidth = Math.Max(1, Round(sourceWidth * scale));
                int scaledHeight = Math.Max(1, Round(sourceHeight * scale));
                return new ResizePlan(scaledWidth, scaledHeight, scaledWidth, scaledHeight, 0, 0);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的 ResizeMode。");
        }
    }

    private static int Round(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);
}

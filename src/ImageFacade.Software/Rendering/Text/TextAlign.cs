namespace ImageFacade.Software.Rendering;

/// <summary>文本行内对齐方式。与 ImageSharp.Drawing 的 <c>TextAlignment</c> 概念一致。</summary>
internal enum TextAlign
{
    /// <summary>左对齐（默认）。</summary>
    Left,

    /// <summary>居中：相对 <c>TextOptions.MaxWidth</c>（为 0 时相对最宽行）居中。</summary>
    Center,

    /// <summary>右对齐：相对 <c>TextOptions.MaxWidth</c>（为 0 时相对最宽行）右对齐。</summary>
    Right,
}

namespace ImageFacade.Software.Rendering;

/// <summary>填充规则。与 Canvas / SVG / ImageSharp.Drawing 的语义一致。</summary>
internal enum FillRule
{
    /// <summary>非零环绕数（默认）。</summary>
    NonZero,

    /// <summary>奇偶规则。</summary>
    EvenOdd,
}

namespace ImageFacade.SdcbSimdCodecs.Rendering;


/// <summary>折线转角的外侧连接方式，命名与 SVG / Canvas 的 <c>lineJoin</c> 对齐。</summary>
internal enum LineJoin
{
    /// <summary>尖角：外侧延长两条边直到相交，超过 <see cref="IPen.MiterLimit"/> 时退回 <see cref="Bevel"/>。</summary>
    Miter,

    /// <summary>斜切：外侧用一条直线切掉尖角。</summary>
    Bevel,

    /// <summary>圆角：外侧用半径 = 半线宽的圆弧补上。</summary>
    Round,
}

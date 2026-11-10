namespace ImageFacade.SdcbSimdCodecs.Rendering;


/// <summary>开放路径端点的形状，命名与 SVG / Canvas 的 <c>lineCap</c> 对齐。</summary>
internal enum LineCap
{
    /// <summary>平头：描边在线段端点处直接结束。</summary>
    Butt,

    /// <summary>圆头：以端点为圆心补一个半径 = 半线宽的半圆。</summary>
    Round,

    /// <summary>方头：沿切向向外延伸半个线宽，再以平头结束。</summary>
    Square,
}

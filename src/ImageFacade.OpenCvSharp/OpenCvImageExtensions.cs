using CvMat = OpenCvSharp.Mat;
using CvRect = OpenCvSharp.Rect;

namespace ImageFacade.OpenCvSharp;

/// <summary>
/// <see cref="FacadeImage"/> 与 OpenCvSharp <c>Mat</c> 的互操作：拿共享的 Mat、拿 ROI 视图、拿独立副本。
/// </summary>
/// <remarks>
/// <para>
/// 只在 <see cref="OpenCvBackend"/> 的帧上有效，其他后端调用抛 <see cref="InvalidOperationException"/>。
/// 帧内部是 <c>CV_8UC4</c> 的 <b>BGRA</b>（直通 alpha），与门面层的 RGBA 语义差一次通道交换，
/// 用 <c>Cv2</c> 直接画/读时按 BGRA 理解 <c>Scalar</c> 的通道顺序。
/// </para>
/// <para>
/// 三种拿法的区别：<see cref="SharedMat"/> 与 <see cref="SubMat(FacadeImage, CvRect)"/> 都是<b>共享像素</b>
/// （改 Mat 等于改这张图），<see cref="CloneMat"/> 是<b>深拷贝</b>（与图片生命周期无关）。
/// 共享出来的 Mat 归门面所有：<b>不要 Dispose</b>，也不要跨线程用，图片 Dispose 后不要再碰。
/// </para>
/// </remarks>
public static class OpenCvImageExtensions
{
    /// <summary>
    /// 后端当前保存的 <c>Mat</c>（不复制）。用于把帧直接交给 OpenCV 原生代码（<c>Cv2.*</c>、DNN、标定）。
    /// </summary>
    /// <exception cref="InvalidOperationException">当前后端不是 <see cref="OpenCvBackend"/>。</exception>
    /// <exception cref="ObjectDisposedException">图片已释放。</exception>
    public static CvMat SharedMat(this FacadeImage image) => GetMat(image);

    /// <summary>
    /// ROI 视图：共享同一块像素、不复制。写入它就是写入原图，但**只影响这块区域**。
    /// </summary>
    /// <param name="image">OpenCV 后端的图片。</param>
    /// <param name="roi">像素坐标的矩形（左上角 + 宽高），必须完全落在图像内。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="roi"/> 越界或宽高不为正。</exception>
    public static CvMat SubMat(this FacadeImage image, CvRect roi)
    {
        CvMat mat = GetMat(image);
        if (roi.X < 0 || roi.Y < 0 || roi.Width <= 0 || roi.Height <= 0
            || (long)roi.X + roi.Width > mat.Cols
            || (long)roi.Y + roi.Height > mat.Rows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roi),
                roi,
                $"ROI 必须完全落在图像内（{mat.Cols}x{mat.Rows}），且宽高大于 0。");
        }

        return mat.SubMat(roi);
    }

    /// <summary><see cref="SubMat(FacadeImage, CvRect)"/> 的分量形式。</summary>
    public static CvMat SubMat(this FacadeImage image, int x, int y, int width, int height)
        => SubMat(image, new CvRect(x, y, width, height));

    /// <summary>
    /// 当前帧的深拷贝（对应 <c>Mat.Clone()</c>）：独立内存，改它不影响这张图，也不受图片 Dispose 影响。
    /// </summary>
    /// <remarks>名字带 <c>Mat</c> 后缀是为了不和门面自带的 <see cref="FacadeImage.Clone()"/>（复制整张 <see cref="FacadeImage"/>）混淆。</remarks>
    public static CvMat CloneMat(this FacadeImage image) => GetMat(image).Clone();

    private static CvMat GetMat(FacadeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // image.Frame 在已释放时抛 ObjectDisposedException
        return image.Frame is OpenCvFrame frame
            ? frame.Mat
            : throw new InvalidOperationException(
                $"当前后端是 '{image.BackendName}'，没有 Mat 可拿；SharedMat / SubMat / CloneMat 只适用于 OpenCvBackend。" +
                "需要像素级互操作请用 CopyPixels / AcquirePixels。");
    }
}

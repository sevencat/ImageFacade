using System.Buffers;

namespace ImageFacade.Pipeline;

/// <summary>贪心 NMS / IoU：纯几何 + 排序，不依赖任何推理库。</summary>
/// <remarks>
/// 典型 n ≤ 300，O(n²) 足够；不使用 LINQ，避免每帧分配。
/// 两个入口都不做分数阈值过滤——那应该发生在解码阶段。
/// </remarks>
public static class Nms
{
    private const float DefaultIouThreshold = 0.45f;

    /// <summary>
    /// 原地抑制：按分数降序重排后把保留项前移到开头，返回保留数量
    /// （调用方用 <c>detections[..count]</c> 取有效段）。
    /// </summary>
    public static int Suppress(Span<Detection> detections, float iouThreshold = DefaultIouThreshold, bool classAware = true)
    {
        ValidateThreshold(iouThreshold);

        int count = detections.Length;
        if (count == 0)
        {
            return 0;
        }

        SortByScoreDescending(detections);

        bool[] suppressed = ArrayPool<bool>.Shared.Rent(count);
        try
        {
            Array.Clear(suppressed, 0, count);

            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                if (suppressed[i])
                {
                    continue;
                }

                Detection winner = detections[i];
                detections[kept++] = winner;

                for (int j = i + 1; j < count; j++)
                {
                    if (suppressed[j])
                    {
                        continue;
                    }

                    if (classAware && detections[j].ClassId != winner.ClassId)
                    {
                        continue;
                    }

                    if (IoU(winner.Box, detections[j].Box) > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }

            return kept;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(suppressed);
        }
    }

    /// <summary>返回保留项在**原数组**中的下标，按分数降序。</summary>
    public static int[] Select(ReadOnlySpan<Detection> detections, float iouThreshold = DefaultIouThreshold, bool classAware = true)
    {
        ValidateThreshold(iouThreshold);

        int count = detections.Length;
        if (count == 0)
        {
            return [];
        }

        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        SortIndicesByScoreDescending(detections, order);

        bool[] suppressed = ArrayPool<bool>.Shared.Rent(count);
        int[] kept = new int[count];
        int keptCount = 0;
        try
        {
            Array.Clear(suppressed, 0, count);

            for (int k = 0; k < count; k++)
            {
                int i = order[k];
                if (suppressed[i])
                {
                    continue;
                }

                kept[keptCount++] = i;

                for (int m = k + 1; m < count; m++)
                {
                    int j = order[m];
                    if (suppressed[j])
                    {
                        continue;
                    }

                    if (classAware && detections[j].ClassId != detections[i].ClassId)
                    {
                        continue;
                    }

                    if (IoU(detections[i].Box, detections[j].Box) > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(suppressed);
        }

        if (keptCount == count)
        {
            return kept;
        }

        int[] result = new int[keptCount];
        Array.Copy(kept, result, keptCount);
        return result;
    }

    /// <summary>交并比；任一框退化（宽或高 ≤ 0）时返回 0。</summary>
    public static float IoU(RectF a, RectF b)
    {
        float left = MathF.Max(a.Left, b.Left);
        float top = MathF.Max(a.Top, b.Top);
        float right = MathF.Min(a.Right, b.Right);
        float bottom = MathF.Min(a.Bottom, b.Bottom);

        float width = right - left;
        float height = bottom - top;
        if (width <= 0f || height <= 0f)
        {
            return 0f;
        }

        float intersection = width * height;
        float union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;
        return union <= 0f ? 0f : intersection / union;
    }

    private static void ValidateThreshold(float iouThreshold)
    {
        if (!(iouThreshold >= 0f) || iouThreshold > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iouThreshold),
                iouThreshold,
                "IoU 阈值必须在 0..1 之间。");
        }
    }

    /// <summary>插入排序：分数降序，相同分数保持原相对顺序（结果可复现）。</summary>
    private static void SortByScoreDescending(Span<Detection> detections)
    {
        for (int i = 1; i < detections.Length; i++)
        {
            Detection current = detections[i];
            int j = i - 1;
            while (j >= 0 && detections[j].Score < current.Score)
            {
                detections[j + 1] = detections[j];
                j--;
            }

            detections[j + 1] = current;
        }
    }

    private static void SortIndicesByScoreDescending(ReadOnlySpan<Detection> detections, Span<int> order)
    {
        for (int i = 1; i < order.Length; i++)
        {
            int current = order[i];
            int j = i - 1;
            while (j >= 0 && detections[order[j]].Score < detections[current].Score)
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = current;
        }
    }
}

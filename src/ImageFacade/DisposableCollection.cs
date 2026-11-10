using System.Runtime.ExceptionServices;

namespace ImageFacade;

/// <summary>
/// 一组 <see cref="IDisposable"/> 的批量释放工具：用于 <c>Cv2.Split</c> 出来的 <c>CvMat[]</c>、
/// 通道数组这类"成组创建、一起释放"的临时对象。
/// </summary>
/// <remarks>
/// <para>替换这种写法：</para>
/// <code>
/// foreach (CvMat plane in planes)
/// {
///     plane.Dispose();
/// }
/// </code>
/// <para>用 <c>planes.DisposeAll();</c>，或者把它纳入作用域：<c>using (DisposableCollection.Adopt(planes)) { ... }</c>。</para>
/// <para>约定：null 元素跳过；某个对象释放抛异常不影响其余对象继续释放，全部结束后再抛出
/// （单个异常保留原始堆栈，多个包成 <see cref="AggregateException"/>）。</para>
/// </remarks>
public static class DisposableCollection
{
    /// <summary>把一组对象纳入 <c>using</c> 作用域，离开时全部释放。</summary>
    public static IDisposable Adopt<T>(IEnumerable<T> items)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(items);
        return new Scope<T>(items);
    }

    /// <summary>依次释放。null 跳过；单个失败不打断其余，结束后统一抛出。</summary>
    public static void DisposeAll<T>(this IEnumerable<T> items)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(items);

        List<Exception>? errors = null;
        foreach (T item in items)
        {
            DisposeOne(item, ref errors);
        }

        ThrowIfAny(errors);
    }

    /// <summary>数组重载：避免 <see cref="IEnumerable{T}"/> 的枚举器分配（OpenCV 的通道数组走这条）。</summary>
    public static void DisposeAll<T>(this T[] items)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(items);

        List<Exception>? errors = null;
        for (int i = 0; i < items.Length; i++)
        {
            DisposeOne(items[i], ref errors);
        }

        ThrowIfAny(errors);
    }

    private static void DisposeOne<T>(T item, ref List<Exception>? errors)
        where T : IDisposable
    {
        if (item is null)
        {
            return;
        }

        try
        {
            item.Dispose();
        }
        catch (Exception ex)
        {
            (errors ??= []).Add(ex);
        }
    }

    private static void ThrowIfAny(List<Exception>? errors)
    {
        if (errors is null)
        {
            return;
        }

        if (errors.Count == 1)
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }

        throw new AggregateException("批量释放时有对象释放失败。", errors);
    }

    private sealed class Scope<T> : IDisposable
        where T : IDisposable
    {
        private IEnumerable<T>? _items;

        internal Scope(IEnumerable<T> items) => _items = items;

        public void Dispose()
        {
            IEnumerable<T>? items = _items;
            _items = null;
            items?.DisposeAll();
        }
    }
}

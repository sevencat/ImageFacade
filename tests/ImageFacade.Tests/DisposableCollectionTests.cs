using Xunit;

namespace ImageFacade.Tests;

/// <summary>批量释放工具：null 跳过、失败不打断其余、作用域幂等。</summary>
public sealed class DisposableCollectionTests
{
    [Fact]
    public void Array_DisposeAll_Releases_Every_Item_in_Order()
    {
        var log = new List<string>();
        var items = new[] { new Tracker("a", log), new Tracker("b", log), new Tracker("c", log) };

        items.DisposeAll();

        Assert.Equal(["a", "b", "c"], log);
        Assert.All(items, static item => Assert.True(item.Disposed));
    }

    [Fact]
    public void Enumerable_DisposeAll_Releases_Every_Item()
    {
        var log = new List<string>();
        IEnumerable<Tracker> items = new List<Tracker> { new("a", log), new("b", log) };

        items.DisposeAll();

        Assert.Equal(["a", "b"], log);
    }

    [Fact]
    public void DisposeAll_Skips_Null_Items()
    {
        var log = new List<string>();
        var items = new Tracker[3];
        items[1] = new Tracker("a", log);

        items.DisposeAll();

        Assert.Equal(["a"], log);
    }

    [Fact]
    public void Single_Failure_Is_Rethrown_After_Releasing_The_Rest()
    {
        var log = new List<string>();
        var boom = new InvalidOperationException("boom");
        var items = new[] { new Tracker("a", log, boom), new Tracker("b", log) };

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => items.DisposeAll());

        Assert.Same(boom, thrown);
        Assert.Equal(["a", "b"], log);   // 后面的对象仍然释放
    }

    [Fact]
    public void Multiple_Failures_Are_Aggregated()
    {
        var log = new List<string>();
        var items = new[]
        {
            new Tracker("a", log, new InvalidOperationException("1")),
            new Tracker("b", log, new InvalidOperationException("2")),
            new Tracker("c", log),
        };

        AggregateException thrown = Assert.Throws<AggregateException>(() => items.DisposeAll());

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.Equal(["a", "b", "c"], log);
    }

    [Fact]
    public void Adopt_Releases_On_Scope_Exit_And_Is_Idempotent()
    {
        var log = new List<string>();
        var first = new Tracker("a", log);
        var second = new Tracker("b", log);

        using (DisposableCollection.Adopt(new[] { first, second }))
        {
            Assert.False(first.Disposed);
        }

        Assert.Equal(["a", "b"], log);

        // 再次 Dispose 不应重复释放
        IDisposable scope = DisposableCollection.Adopt(new[] { first });
        scope.Dispose();
        scope.Dispose();
        Assert.Equal(["a", "b"], log);
    }

    private sealed class Tracker : IDisposable
    {
        private readonly string _name;
        private readonly List<string> _log;
        private readonly Exception? _failure;

        internal Tracker(string name, List<string> log, Exception? failure = null)
        {
            _name = name;
            _log = log;
            _failure = failure;
        }

        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }

            Disposed = true;
            _log.Add(_name);

            if (_failure is not null)
            {
                throw _failure;
            }
        }
    }
}

using ImageFacade.Backends;
using Xunit;

namespace ImageFacade.Tests;

/// <summary>
/// <see cref="PixelLease.Dispose"/> 必须幂等：后端回调里要归还池化缓冲，
/// 重复释放会让同一块缓冲被 <c>ArrayPool</c> 收两次。
/// </summary>
public sealed class PixelLeaseTests
{
    [Fact]
    public void Dispose_Invokes_Callback_Exactly_Once()
    {
        int calls = 0;
        var lease = new PixelLease(new byte[16], 16, () => calls++);

        lease.Dispose();
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Dispose_Without_Callback_Is_Safe()
    {
        var lease = new PixelLease(new byte[8], 8);

        lease.Dispose();
        lease.Dispose();
    }

    [Fact]
    public void Default_Lease_Is_Invalid_And_Disposable()
    {
        PixelLease lease = default;

        Assert.False(lease.IsValid);
        lease.Dispose();
    }
}

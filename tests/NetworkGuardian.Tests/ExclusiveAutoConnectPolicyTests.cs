using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class ExclusiveAutoConnectPolicyTests
{
    [Fact]
    public void DisabledPhysicalAdapter_RemainsPartOfStableOwnerSelection()
    {
        var lower = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var higher = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var ownerWhileLowerIsDisabled = ExclusiveAutoConnectPolicy.SelectOwner(
            new[] { higher, lower },
            new[] { higher });
        var ownerAfterLowerReturns = ExclusiveAutoConnectPolicy.SelectOwner(
            new[] { higher, lower },
            new[] { higher, lower });

        Assert.Equal(lower, ownerWhileLowerIsDisabled);
        Assert.Equal(lower, ownerAfterLowerReturns);
    }

    [Fact]
    public void AvailableAdapters_AreFallbackWhenPnpCorrelationIsUnavailable()
    {
        var first = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var second = Guid.Parse("33333333-3333-3333-3333-333333333333");

        Assert.Equal(first, ExclusiveAutoConnectPolicy.SelectOwner(Array.Empty<Guid>(), new[] { second, first }));
    }
}

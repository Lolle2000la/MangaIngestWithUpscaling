using MangaIngestWithUpscaling.Shared.Helpers;
using Xunit;

namespace MangaIngestWithUpscaling.Shared.Tests.Helpers;

public class ExceptionChainTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Enumerate_PlainInnerChain_YieldsOutermostFirst()
    {
        var inner = new IOException("disk gone");
        var outer = new InvalidOperationException("wrapped", inner);

        List<Exception> chain = ExceptionChain.Enumerate(outer).ToList();

        Assert.Equal(new Exception[] { outer, inner }, chain);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Enumerate_Aggregate_VisitsEverySiblingIncludingTheInnerException()
    {
        // InnerException on an AggregateException is only its first sibling; the walk must reach the
        // rest too or a caller looking for "an IOException inside an AggregateException" never sees it.
        var permanent = new InvalidOperationException("permanent");
        var io = new IOException("disk gone");
        var aggregate = new AggregateException(permanent, io);

        List<Exception> chain = ExceptionChain.Enumerate(aggregate).ToList();

        Assert.Equal(new Exception[] { aggregate, permanent, io }, chain);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Enumerate_RepeatedInstance_IsYieldedOnce()
    {
        // Two siblings wrapping the same failure walk it once, not twice.
        var shared = new IOException("shared");
        var aggregate = new AggregateException(shared, shared);

        List<Exception> chain = ExceptionChain.Enumerate(aggregate).ToList();

        Assert.Equal(new Exception[] { aggregate, shared }, chain);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Enumerate_Diamond_VisitsTheSharedCauseOnce()
    {
        var shared = new IOException("shared");
        var first = new InvalidOperationException("first", shared);
        var second = new InvalidOperationException("second", shared);
        var aggregate = new AggregateException(first, second);

        List<Exception> chain = ExceptionChain.Enumerate(aggregate).ToList();

        Assert.Equal(4, chain.Count);
        Assert.Single(chain, e => ReferenceEquals(e, shared));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Contains_ReachesASiblingInsideAnAggregate()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("permanent"),
            new IOException("disk gone")
        );

        Assert.True(ExceptionChain.Contains(aggregate, e => e is IOException));
        Assert.False(ExceptionChain.Contains(aggregate, e => e is TimeoutException));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Enumerate_Null_YieldsNothing() => Assert.Empty(ExceptionChain.Enumerate(null));
}

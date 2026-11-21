using System.Collections.Concurrent;
using Melomg.Infrastructure.Services;
using Xunit;

namespace Melomg.UnitTests;

public sealed class MonotonicIdGeneratorTests
{
    [Fact]
    public void GeneratesUniqueMonotonicIdsAcrossTightLoops()
    {
        var generator = new MonotonicTimestampIdGenerator();
        var docIds = new ConcurrentBag<string>();
        var figIds = new ConcurrentBag<string>();

        Parallel.For(0, 500, _ =>
        {
            docIds.Add(generator.NewDocumentId());
            figIds.Add(generator.NewFigureId());
        });

        Assert.Equal(500, docIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(500, figIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(docIds, id => Assert.StartsWith("Doc_", id, StringComparison.Ordinal));
        Assert.All(figIds, id => Assert.StartsWith("Fig_", id, StringComparison.Ordinal));
    }
}

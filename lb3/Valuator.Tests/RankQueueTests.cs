using System.Text;
using Valuator.Shared;

namespace Valuator.Tests;

public class RankQueueTests
{
    [Fact]
    public void JobContainsOnlyTextId()
    {
        string id = Guid.NewGuid().ToString();
        Assert.Equal(id, RankQueue.ParseId(Encoding.UTF8.GetBytes(id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("TEXT-00000000-0000-0000-0000-000000000000")]
    [InlineData("zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz")]
    public void RejectsMalformedJob(string message)
        => Assert.Null(RankQueue.ParseId(Encoding.UTF8.GetBytes(message)));
}

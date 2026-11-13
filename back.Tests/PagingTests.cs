using ERental.DTOs;

namespace ERental.Tests;

/// <summary>分页归一化：page 从 1 开始，pageSize 上限 200，pageSize ≤ 0 表示不分页</summary>
public class PagingTests
{
    [Theory]
    [InlineData(0, 10, 1, 10, 0, 10)]
    [InlineData(-3, 10, 1, 10, 0, 10)]
    [InlineData(3, 20, 3, 20, 40, 20)]
    [InlineData(2, 500, 2, 200, 200, 200)]
    [InlineData(5, 0, 1, 0, 0, 0)]
    [InlineData(5, -1, 1, 0, 0, 0)]
    public void Normalize_ClampsPageAndPageSize(int page, int pageSize, int expectedPage, int expectedSize, int expectedSkip, int expectedTake)
    {
        var (pageResult, size, skip, take) = Paging.Normalize(page, pageSize);

        Assert.Equal(expectedPage, pageResult);
        Assert.Equal(expectedSize, size);
        Assert.Equal(expectedSkip, skip);
        Assert.Equal(expectedTake, take);
    }

    [Fact]
    public void PagedResult_PageCount_IsAtLeastOne()
    {
        Assert.Equal(1, new PagedResult<int>([], 0, 1, 10).PageCount);
        Assert.Equal(3, new PagedResult<int>([], 21, 1, 10).PageCount);
        Assert.Equal(1, new PagedResult<int>([], 21, 1, 0).PageCount);
    }
}

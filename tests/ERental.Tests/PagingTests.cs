using ERental.DTOs;

namespace ERental.Tests;

/// <summary>
/// 分页归一化测试。分页参数来自 URL，必须假设调用方会传 0、负数、超大值，
/// 否则一个 <c>?pageSize=999999</c> 就能把整张表拉进内存。
/// </summary>
public class PagingTests
{
    [Fact]
    public void NoPageSize_MeansNoPaging()
    {
        // pageSize 缺省（0）→ Take=0，调用方返回全量，兼容旧前端
        var (_, size, skip, take) = Paging.Normalize(1, 0);
        Assert.Equal(0, size);
        Assert.Equal(0, skip);
        Assert.Equal(0, take);
    }

    [Theory]
    [InlineData(0, 1)]      // page=0 → 第 1 页
    [InlineData(-5, 1)]     // 负数 → 第 1 页
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public void Page_IsClampedToAtLeastOne(int input, int expected)
    {
        var (page, _, _, _) = Paging.Normalize(input, 10);
        Assert.Equal(expected, page);
    }

    [Fact]
    public void Skip_IsZeroBased()
    {
        var (_, size, skip, take) = Paging.Normalize(3, 20);
        Assert.Equal(20, size);
        Assert.Equal(40, skip);    // 第 3 页从第 41 条开始
        Assert.Equal(20, take);
    }

    [Fact]
    public void PageSize_IsCappedAt200()
    {
        var (_, size, _, take) = Paging.Normalize(1, 100_000);
        Assert.Equal(Paging.MaxPageSize, size);
        Assert.Equal(Paging.MaxPageSize, take);
    }

    [Fact]
    public void PageCount_IsAtLeastOneEvenWhenEmpty()
    {
        var empty = new PagedResult<int>([], 0, 1, 10);
        Assert.Equal(1, empty.PageCount);                           // 空结果也显示 1 页，避免前端算出 0
        Assert.Equal(3, new PagedResult<int>([], 25, 1, 10).PageCount);
        Assert.Equal(2, new PagedResult<int>([], 20, 1, 10).PageCount);
    }

    [Fact]
    public void PageCount_GuardsAgainstZeroPageSize()
    {
        // pageSize=0 时不能除零
        Assert.Equal(1, new PagedResult<int>([], 50, 1, 0).PageCount);
    }
}

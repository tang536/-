using ERental.Models;
using ERental.Services;

namespace ERental.Tests;

/// <summary>单号生成与实体校验：唯一、可读、不可枚举</summary>
public class BusinessNumberAndEntityTests
{
    [Fact]
    public void New_UsesPrefixStampAndRandomSegment()
    {
        var number = BusinessNumber.New("ZL", 4, "yyyyMMdd", new DateTime(2026, 1, 2));

        Assert.StartsWith("ZL20260102", number, StringComparison.Ordinal);
        Assert.Equal(10 + 4, number.Length);
        Assert.Matches("^ZL20260102[0-9A-F]{4}$", number);
    }

    [Fact]
    public void New_RejectsInvalidArguments()
    {
        Assert.Throws<BusinessException>(() => BusinessNumber.New(""));
        Assert.Throws<BusinessException>(() => BusinessNumber.New("ZL", 3));
        Assert.Throws<BusinessException>(() => BusinessNumber.New("ZL", 33));
    }

    [Fact]
    public void New_DoesNotCollideInABatch()
    {
        var numbers = Enumerable.Range(0, 200)
            .Select(_ => BusinessNumber.New("T", 8, "yyyyMMddHHmmss"))
            .ToArray();

        Assert.Equal(numbers.Length, numbers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NewComposite_KeepsBusinessKeyBetweenSeparators()
    {
        var number = BusinessNumber.NewComposite("SP", "ZL20261113001", "-", 4, "yyyyMMdd");

        var parts = number.Split('-');
        Assert.Equal(4, parts.Length);
        Assert.Equal("SP", parts[0]);
        Assert.Equal("ZL20261113001", parts[1]);
        Assert.Equal(4, parts[3].Length);
    }

    [Fact]
    public void VehicleStatuses_OnlyAcceptFourKnownValues()
    {
        Assert.True(VehicleStatuses.IsValid(VehicleStatuses.Idle));
        Assert.True(VehicleStatuses.IsValid(VehicleStatuses.Rented));
        Assert.True(VehicleStatuses.IsValid(VehicleStatuses.Maintenance));
        Assert.True(VehicleStatuses.IsValid(VehicleStatuses.Scrapped));
        Assert.False(VehicleStatuses.IsValid("租出"));
        Assert.False(VehicleStatuses.IsValid(null));
        Assert.Contains("空闲", VehicleStatuses.InvalidMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Vehicle_Create_TrimsAndRejectsNegativePricing()
    {
        var vehicle = Vehicle.Create(
            (" 桂A100 ", null, " 雅迪 "),
            (null, 3500m),
            (60m, 500m),
            (VehicleStatuses.Idle, null));

        Assert.Equal("桂A100", vehicle.VehicleNo);
        Assert.Equal("雅迪", vehicle.Brand);
        Assert.Equal(60m, vehicle.DailyRent);

        Assert.Throws<BusinessException>(() => Vehicle.Create(
            ("桂A101", null, null), (null, null), (-1m, 500m), (VehicleStatuses.Idle, null)));
    }

    [Fact]
    public void RentalOrder_Create_ValidatesLeaseAndStatus()
    {
        var order = RentalOrder.Create(
            ("ZL20261113001", 1, 1, null),
            (new DateTime(2026, 11, 13, 9, 0, 0), new DateTime(2026, 11, 14, 9, 0, 0)),
            (60m, 500m));

        Assert.Equal("待支付", order.OrderStatus);
        Assert.Equal(60m, order.TotalRent);

        Assert.Throws<BusinessException>(() => RentalOrder.Create(
            ("ZL1", 1, 1, null),
            (new DateTime(2026, 11, 14), new DateTime(2026, 11, 13)),
            (60m, 500m)));
        Assert.Throws<BusinessException>(() => RentalOrder.Create(
            ("ZL1", 1, 1, null),
            (new DateTime(2026, 11, 13), new DateTime(2026, 11, 14)),
            (60m, 500m),
            "租出"));
    }

    [Fact]
    public void CarCheckList_Create_JudgesMatchByStatusEquality()
    {
        var match = CarCheckList.Create("PD1", new DateTime(2026, 11, 13), 1,
            VehicleStatuses.Idle, VehicleStatuses.Idle, null, null);
        var diff = CarCheckList.Create("PD2", new DateTime(2026, 11, 13), 1,
            VehicleStatuses.Idle, VehicleStatuses.Maintenance, "现场发现车辆在维保", null);

        Assert.True(match.IsMatch);
        Assert.False(diff.IsMatch);
        Assert.Equal("现场发现车辆在维保", diff.DifferenceDesc);
    }
}

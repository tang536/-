namespace ERental.Models;

/// <summary>前台，继承SystemUser，负责租赁业务</summary>
public class ReceptionUser : SystemUser
{
    public RentalOrder CreateOrder() => new();
    public SettleRecord ReturnSettle() => new();
    public void PrintReceipt() { }
}

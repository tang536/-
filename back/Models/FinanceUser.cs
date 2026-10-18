namespace ERental.Models;

/// <summary>财务，继承SystemUser，负责财务核对</summary>
public class FinanceUser : SystemUser
{
    public void FinanceCheck() { }
    public VisualReport ViewFinanceReport() => new();
    public void HandleRefund() { }
}

namespace ERental.Models;

/// <summary>管理员，继承SystemUser，拥有全部权限</summary>
public class AdminUser : SystemUser
{
    public void ManageVehicle() { }
    public VisualReport ViewAllReport() => new();
    public void ConfigSystem() { }
    public void ManageStaffAccount() { }
}

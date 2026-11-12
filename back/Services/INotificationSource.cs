namespace ERental.Services;

/// <summary>通知来源：把随时间变化的业务状态（逾期订单、保养到期、资料待完善等）幂等同步成 message 行</summary>
public interface INotificationSource
{
    /// <summary>把「当前应当存在的通知」同步进 message 表（幂等，可重复调用）</summary>
    Task SyncAsync(string receiverType, int receiverId);
}

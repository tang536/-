using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace ERental.Data;

/// <summary>
/// 建立「不经过 EF 连接池语义」的独立物理连接。
/// <para>
/// 需要独立连接的场景只有两类：<br/>
/// ① MySQL 命名锁（GET_LOCK 的作用域是连接生命周期，必须由同一条物理连接持锁与释放）；<br/>
/// ② 数据库对象安装（DDL 会隐式提交，不能挂在 EF 事务或池化连接上）。<br/>
/// 因此这里强制 Pooling=false：连接关闭即物理断开，锁与会话状态不会被连接池
/// 以「看起来已释放、实际仍被复用连接持有」的方式带走。
/// </para>
/// </summary>
internal static class DbConnectionFactory
{
    /// <summary>按当前 DbContext 的连接串创建一条独立连接（默认关闭连接池）</summary>
    public static DbConnection CreateDedicated(AppDbContext db, bool disablePooling = true)
    {
        var connectionString = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = db.Database.GetDbConnection().ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("数据库连接串为空，无法建立独立连接");

        if (disablePooling)
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            builder["Pooling"] = false;
            connectionString = builder.ConnectionString;
        }

        // 驱动可能是 MySql.Data 也可能是 MySqlConnector：用 EF 现有连接的类型做原型，
        // 避免在代码里写死某一个驱动的命名空间。
        var prototype = db.Database.GetDbConnection();
        var type = prototype.GetType();
        var connection = (DbConnection)Activator.CreateInstance(type)!;
        connection.ConnectionString = connectionString;
        return connection;
    }
}

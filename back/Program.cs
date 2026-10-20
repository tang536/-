using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using ERental.Authorization;
using ERental.Data;
using ERental.Filters;
using ERental.Services;
using ERental.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// ⚠️ 必须在 CreateBuilder **之前**建目录。
// WebApplicationBuilder 的构造函数里就会探测 ContentRoot 下的 wwwroot 来决定 WebRootPath；
// 一旦探测时目录不存在，WebRootPath 会被置空、StaticFileMiddleware 退化成 NullFileProvider，
// 之后就算再建目录也不会重新扫描 —— 表现为「照片能上传成功，但访问一律 404」。
foreach (var sub in new[] { "maintenance", "vehicle" })
{
    Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", sub));
}

var builder = WebApplication.CreateBuilder(args);

// 支付宝沙箱网关的响应可能用 GBK 声明编码，而 .NET Core 默认不带非 Unicode 编码表（CodePages）。
// 不注册的话，读取响应时会抛「'GBK' is not a supported encoding name」。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// 数据库上下文
// 驱动变更（.NET 10 升级）：Pomelo.EntityFrameworkCore.MySql 至今没有 EF Core 10 版本，
// 最新稳定版停在 9.0.0，故改用 Oracle 官方 MySql.EntityFrameworkCore（有 lib/net10.0 目标组）。
// 与 Pomelo 的差异：UseMySQL 无需再用 ServerVersion.AutoDetect 探测服务端版本。
var erentalDb = builder.Configuration.GetConnectionString("DefaultConnection");
// 空字符串也算缺失：仓库里的 appsettings.json 刻意不写真实口令，
// 若这里放行空串，等到第一次查库才报"连接失败"，排查成本高得多。
if (string.IsNullOrWhiteSpace(erentalDb))
    throw new InvalidOperationException(
        "缺少数据库连接串 ConnectionStrings:DefaultConnection。" +
        "本地开发请写在 appsettings.Development.json；生产用环境变量 ConnectionStrings__DefaultConnection 注入。");
builder.Services.AddDbContext<AppDbContext>(options => options.UseMySQL(erentalDb));

// JWT 认证
// 密钥来源：显式配置（Jwt__Key / Jwt:Key）→ App_Data/signing.key → 首次启动生成随机密钥并落盘。
// 仓库里不再保留任何可用的默认密钥：写死的默认值等于公开的密钥，谁都能伪造管理员令牌。
var jwtKey = SigningKeyProvider.Resolve(builder.Configuration, builder.Environment, out var jwtKeySource);
// 把解析结果写回配置，AuthService / BackupService 注入的 IConfiguration 便拿到同一把密钥，
// 不需要各自再算一遍来源。
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = jwtKey });

// 兜底：正常走不到这里（Resolve 保证长度），万一被改动也宁可拒绝启动，
// 也不要带着空密钥/短密钥对外服务。
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < SigningKeyProvider.MinLength)
    throw new InvalidOperationException(
        "Jwt:Key 缺失或短于 32 字符。本地开发留空即可（会自动生成 App_Data/signing.key）；" +
        "生产用环境变量 Jwt__Key 注入。");
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
    options.Events = new JwtBearerEvents
    {
        // ⚠️ 续期令牌（7 天）与访问令牌（120 分钟）用的是同一把签名密钥，
        // 若不加这道判断，把 refresh token 当 Bearer 发过来就能一路通过所有策略校验。
        // 这里在验证通过后按 token_type 再拦一道：只有 access 能访问业务接口。
        // 没有 token_type 声明的老令牌一律放行，避免升级瞬间把所有人踢下线。
        OnTokenValidated = ctx =>
        {
            var tokenType = ctx.Principal?.FindFirst(AuthService.TokenTypeClaim)?.Value;
            if (tokenType == AuthService.TokenTypeRefresh)
                ctx.Fail("该令牌仅用于续期，不能访问业务接口");
            return Task.CompletedTask;
        }
    };
});

// 授权：按需求 4.2.1「角色权限管理」把 12 档角色组合注册成具名策略，
// 控制器侧统一写 [Authorize(Policy = Policies.X)]，不再散落中文字符串。
builder.Services.AddAuthorization(AuthorizationSetup.Configure);

// 授权失败时返回统一的 JSON 响应体（框架默认只给状态码、body 为空，前端拿不到原因）
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonAuthorizationResultHandler>();
builder.Services.AddControllers(o =>
    {
        // 全局授权兜底：未声明 [Authorize]/[AllowAnonymous] 的接口一律拒绝，杜绝「忘标注=匿名可访问」。
        o.Filters.Add<RequireAuthorizationFilter>();
        // 关键写操作自动审计：POST/PUT/DELETE/PATCH 自动落 system_log（需求 4.2.1「全量记录」）。
        o.Filters.Add<AuditLogFilter>();
    })
    .AddJsonOptions(o =>
    {
        // 防御：EF 实体的双向导航属性一旦形成环（如 工单→车辆→工单），
        // 默认序列化会抛 JsonException 导致 500，这里改为把成环的属性输出为 null。
        // 各接口仍应优先返回 DTO 投影，此配置只是最后一道保险。
        o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// ── 反向代理头还原 ──
// 生产由本机 Nginx 反代（systemd 里 Kestrel 只监听 http://localhost:5105，外部无法直连），
// 它会把真实客户端 IP 写进 X-Forwarded-For / X-Real-IP。
// 不还原这个头会带来两个后果：限流把所有用户当成同一个 127.0.0.1 一起限，
// 审计日志里的 IP 也全是反代地址，出事后无法定位到人。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // ⚠️ 只信任回环地址。信任范围越宽，客户端伪造 X-Forwarded-For 绕过限流的门槛就越低；
    // 清空默认的 127.0.0.0/8 网段、只留回环，配合「Kestrel 只绑 localhost」才是闭环。
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

// ── 限流 ──
// 与 VehicleLeaseGuard 是两道不同的防线：
//   前者保证「同一辆车不会被租两次」（数据一致性），
//   后者防「同一来源在短时间内打太多请求」（脚本刷单、撞库、前端死循环）。
// 两者都需要，缺一个都挡不住真实攻击：限流挡不住并发窗口，原子占车也挡不住洪水请求。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // 全站：每 IP 令牌桶，容量 120、每秒回补 40。
    // 门店场景下同一 Wi-Fi 可能有 5 个终端共用一个出口 IP，单页刷新约 5~10 个接口调用，
    // 容量 120 足够正常使用，只有脚本或死循环才打得满。
    // 分区键用还原后的客户端 IP；本地直连时它就是 127.0.0.1，全机共用一个桶，符合预期。
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetTokenBucketLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 120,
                TokensPerPeriod = 40,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    // 认证类接口：每 IP 每分钟 10 次（登录 / 注册 / 改密共用）。
    // 账号级还有「5 次失败锁 10 分钟」的既有保护，
    // 两层叠加才能挡住「换账号撞库」——单靠账号锁定时，攻击者每个账号试 5 次就换下一个。
    options.AddPolicy(RateLimitPolicies.Auth, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    // 被拒时返回与全站一致的 JSON 结构，前端按 { code, message, success } 统一处理；
    // 同时给 Retry-After，让前端可以倒计时而不是无脑重点。
    options.OnRejected = async (ctx, token) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await ctx.HttpContext.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            code = StatusCodes.Status429TooManyRequests,
            message = "操作过于频繁，请稍后再试",
            success = false
        }), token);
    };
});

// HTTP 客户端工厂：全站出站请求（支付宝网关 / 账户余额查询）
// 统一经 IHttpClientFactory 获取，由框架池化并复用底层 TCP 连接。
//
// 为什么不再各处 new HttpClient()：
//   HttpClient 虽可 Dispose，但**底层连接不会随之复用**——已建立的 TCP 连接会进入
//   TIME_WAIT 并占用本地端口，高频调用下最终抛
//   SocketException「Only one usage of each socket address」。工厂模式把 handler 池化，
//   连接复用、DNS 变更可感知，是 .NET 的官方推荐做法。
builder.Services.AddHttpClient();

// ComfyUI 连通性探测专用客户端：必须绕开系统代理。
// ComfyUI 基本都跑在本机/局域网，代理层不仅多余，还可能对连不上的端口返回误导性响应
// （实测沙箱代理对死端口回 200，导致误报「在线」）。注册成命名客户端比在接口里
// new HttpClient 更好：连接由工厂池化复用，也不会随请求泄漏 handler。
builder.Services.AddHttpClient("comfy-probe", c => c.Timeout = TimeSpan.FromSeconds(6))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });

// 业务服务
builder.Services.AddScoped<ImageStorage>();
// 单点登录登记表：同一账号不允许同时登录。进程内单例（本地单实例部署），
// 不加表不加列——保住与《唐荣强.docx》图4-1~图4-17 的 17 张表 / 134 字段严格一致。
// 会话登记表落在库表（system_config），多实例共享；因此随请求作用域解析，不能做单例
builder.Services.AddScoped<SessionRegistry>();
// 基于 MySQL GET_LOCK 的分布式锁：定时任务（备份、超时单回收）多实例选主
builder.Services.AddScoped<DbLockService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<VehicleService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<SettlementService>();
builder.Services.AddScoped<MaintenanceService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<SystemConfigService>();
builder.Services.AddScoped<LogService>();
// 备份/恢复：17 张表 → AES-256-GCM 加密文件；恢复时先做快照再覆盖（见 BackupService 注释）。
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<RenewalService>();
builder.Services.AddScoped<CheckService>();
builder.Services.AddScoped<PrintService>();
builder.Services.AddScoped<MemberService>();

// 支付配置：凭证由管理员在管理员端填写，存 system_config（pay.* 前缀），支持「生产→沙箱→模拟」三级自动降级。
// 敏感项（私钥/公钥）经 SecretProvider 分层解析：环境变量 → 用户机密 → 数据库。
// 部署时可用环境变量注入覆盖库里的那份；开发期保持"页面填了就生效"。
builder.Services.AddSingleton<SecretProvider>();
builder.Services.AddScoped<PayConfigService>();

// 账户余额查询：支付宝/微信官方余额接口，带超时、重试、缓存与降级兜底。
builder.Services.AddScoped<BalanceService>();

// 支付宝收单：SignType 等静态项读 appsettings 的 Alipay 节点（该节点已不含任何凭证）；
// AppId/密钥/网关一律从 PayConfigService 动态解析。
// 凭证不全时不报错——会自动降级为沙箱或内置模拟收银台，保证链路可演示。
builder.Services.Configure<ERental.Options.AlipayOptions>(builder.Configuration.GetSection("Alipay"));
builder.Services.AddScoped<AlipayService>();
// 微信（APIv3）与银行卡（通用收银网关）正式通道：与支付宝同一套降级约定（凭证齐全走真实，否则模拟收银台）
builder.Services.AddScoped<WechatPayService>();
builder.Services.AddScoped<BankPayService>();

// 局域网接入：手机/另一台电脑连同一 Wi-Fi 即可访问。开关由启动脚本注入
// （Lan__Enabled=true），未开启时行为与改造前完全一致。
builder.Services.Configure<ERental.Options.LanOptions>(builder.Configuration.GetSection("Lan"));

// AI 生图：对接本机 ComfyUI 桌面版（默认 127.0.0.1:8188）。
// 工作流模板放在 back/ComfyWorkflows/（从桌面版 Save (API Format) 导出）。
builder.Services.Configure<ERental.Options.AiImageOptions>(builder.Configuration.GetSection("AiImage"));
// ComfyUiClient 用强类型 HttpClient：超时在构造函数里配好，连接由 IHttpClientFactory 池化复用。
// 地址不写死——ComfyConfigService 从 system_config（ai.comfyui_base_url）动态提供，
// 管理员在系统配置页改完最多 3 秒生效（ComfyUI 换端口/换机器不用改代码重启）。
builder.Services.AddScoped<ComfyConfigService>();
// 生成进度跟踪器：ComfyUiClient 收 WS 消息 → AiImageService 写入 → 前端轮询读取（进程内单例）
builder.Services.AddSingleton<AiProgressTracker>();
builder.Services.AddHttpClient<ComfyUiClient>();
builder.Services.AddScoped<AiImageService>();

// 后台兜底任务：每分钟回收「超时未支付」的订单并释放车辆，防止车辆被永久锁死。
builder.Services.AddHostedService<PendingOrderCleanupService>();
// 定时备份：每天 Backup:DailyAt（默认 02:30）导出加密备份并按保留数清理旧文件。
// 与手动备份共用同一套实现，避免"手动备的能恢复、自动备的不能"这类分裂。
builder.Services.AddHostedService<BackupSchedulerService>();

var app = builder.Build();

// 只记来源、不记密钥：排查「为什么重启后令牌失效 / 备份解不开」时，这一行就够定位了
if (jwtKeySource.StartsWith("新生成", StringComparison.Ordinal))
    app.Logger.LogWarning("JWT 签名密钥{Source}。请勿提交该文件；生产环境建议用环境变量 Jwt__Key 注入。", jwtKeySource);
else
    app.Logger.LogInformation("JWT 签名密钥来源：{Source}", jwtKeySource);

// 自动迁移和种子数据
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    DbInitializer.EnsureSchemaUpgrades(db);   // 老库平滑升级（customer.login_account、id_card 可空）
    DbInitializer.Seed(db, app.Configuration);
}

app.UseMiddleware<ExceptionMiddleware>();

// 反代头还原必须排在任何「读客户端 IP」的组件之前：
// 限流的分区键、审计日志的 ip_address、支付下单的终端 IP 都取自 Connection.RemoteIpAddress。
app.UseForwardedHeaders();

// 静态资源：对外提供维保现场照片（wwwroot/uploads/maintenance/**）。
// 无需鉴权即可读取图片文件本身，但文件路径不可枚举（时间戳 + GUID 命名），
// 工单列表接口仍受角色授权保护。
app.UseStaticFiles();

// 生产部署强制 HTTPS；开发期前端通过 http://localhost:5105 反向代理调用，
// 若一并强制跳转，会把代理请求 307 到 https 端口导致联调失败，因此开发环境跳过。
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowAll");

// 限流位置很关键：
//   放在 UseCors 之后 —— 429 响应才会带上跨域头，浏览器能读到提示文案而不是笼统的 CORS 报错；
//   放在 UseAuthentication 之前 —— 被限流的请求不必再走 JWT 验签，省掉无谓开销。
app.UseRateLimiter();

app.UseAuthentication();
// 单点登录校验：必须夹在 Authentication 与 Authorization 之间。
// 前者把 token 解析成 ClaimsPrincipal（才读得到 sid），后者做角色判定——
// 放在授权之前，16 个控制器无需逐个改造，将来新增的接口也自动受保护。
app.UseMiddleware<SingleSessionMiddleware>();
app.UseAuthorization();
app.MapControllers();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

Console.WriteLine("===== 途电驿站启动成功 =====");

// 监听地址打印：Kestrel 绑了 0.0.0.0 时控制台只会显示 http://0.0.0.0:5105，
// 对使用者没有信息量。这里把可用的局域网地址显式算出来，省去自己 ipconfig 的步骤。
var lanIp = NetworkHelper.ResolveLanIp();
if (!string.IsNullOrWhiteSpace(lanIp))
{
    // 前端只有 5173 一个入口：员工端与顾客端已合并为同一份 React 工程，
    // 登录时按角色决定进入后台工作台还是顾客自助端，不再有独立的 5174。
    Console.WriteLine($"[局域网] 前端     http://{lanIp}:5173");
    Console.WriteLine($"[局域网] 接口文档 http://{lanIp}:5105/swagger");
}

app.Run();

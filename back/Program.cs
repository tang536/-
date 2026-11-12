using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

// 顶层语句只保留「启动流程骨架」：每一步具体做什么，由文件末尾的静态局部函数完成。
// 各步调用点与原文件逐行位置一致——服务注册顺序、中间件管道顺序、初始化调用时机都没有变，
// 只是把成段的语句搬进了函数体（原地调用，不会推迟或提前执行）。

// 在 CreateBuilder 之前建目录。
PrepareUploadDirectories();

var builder = WebApplication.CreateBuilder(args);

// HTTP 上下文访问器：AuthService 要把登录来源 IP 写进 system_log。
builder.Services.AddHttpContextAccessor();

// 支付宝沙箱网关的响应可能用 GBK 声明编码，而 .NET Core 默认不带非 Unicode 编码表（CodePages）。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// 数据库上下文
var erentalDb = ResolveConnectionString(builder.Configuration);
builder.Services.AddDbContext<AppDbContext>(options => options.UseMySQL(erentalDb));

// JWT 认证
var jwtKey = SigningKeyProvider.Resolve(builder.Configuration, builder.Environment, out var jwtKeySource);
// 把解析结果写回配置，AuthService / BackupService 注入的 IConfiguration 便拿到同一把密钥，
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = jwtKey });
// 兜底：正常走不到这里（Resolve 保证长度），万一被改动也宁可拒绝启动，
EnsureJwtKeyUsable(jwtKey);
ConfigureJwtAuthentication(builder.Services, builder.Configuration, jwtKey);

// 授权：按需求 4.2.1「角色权限管理」把 12 档角色组合注册成具名策略，
builder.Services.AddAuthorization(AuthorizationSetup.Configure);

// 授权失败时返回统一的 JSON 响应体（框架默认只给状态码、body 为空，前端拿不到原因）
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonAuthorizationResultHandler>();
ConfigureControllers(builder.Services);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── CORS：来源白名单（不再 AllowAnyOrigin）──
// 策略名由函数回传，后面的 UseCors 必须用同一个名字，写死两处容易改漏。
var corsPolicyName = ConfigureCors(builder.Services, builder.Configuration);

// ── 反向代理头还原 ──
ConfigureForwardedHeaders(builder.Services);

// ── 限流 ──
ConfigureRateLimiter(builder.Services);

ConfigureHttpClients(builder.Services);

// 业务服务
RegisterBusinessServices(builder.Services, builder.Configuration);

// 局域网接入与 AI 生图
RegisterLanAndAiServices(builder.Services, builder.Configuration);

// 后台兜底任务与定时备份
RegisterHostedServices(builder.Services);

var app = builder.Build();

ReportSigningKeySource(app.Logger, jwtKeySource);

// 启动序列：建表 → 老库平滑升级 → 种子数据，三步均幂等。
RunStartupTasks(app);

ConfigurePipeline(app, corsPolicyName);

Console.WriteLine("===== 途电驿站启动成功 =====");

// 监听地址打印：Kestrel 绑了 0.0.0.0 时控制台只会显示 http://0.0.0.0:5105，
PrintLanEndpoints(builder.Configuration);

// 用 RunAsync 而不是同步 Run：同步版本会在管道上阻塞一个线程，异步版本在关闭信号到来时能优雅排空
await app.RunAsync();

// ══════════════════════════ 启动流程的各个步骤 ══════════════════════════
// 全部写成 static 局部函数：不捕获顶层变量、靠参数显式传递，
// 这样每一步都能单独读懂，也不会因闭包延长顶层变量的生命周期。

// 上传目录必须在 CreateBuilder 之前建好：后面的静态文件中间件会直接使用该目录。
static void PrepareUploadDirectories()
{
    foreach (var sub in new[] { "maintenance", "vehicle" })
    {
        Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", sub));
    }
}

// 连接串为空也算缺失：仓库里的 appsettings.json 刻意不写真实口令。
// 与其带着半截配置启动、直到第一次查询才炸，不如启动即失败并给出可操作的提示。
static string ResolveConnectionString(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException(
            "缺少数据库连接串 ConnectionStrings:DefaultConnection。" +
            "本地开发请写在 appsettings.Development.json；生产用环境变量 ConnectionStrings__DefaultConnection 注入。");
    return connectionString;
}

// 正常走不到这里（SigningKeyProvider.Resolve 保证长度），万一被改动也宁可拒绝启动。
static void EnsureJwtKeyUsable(string? jwtKey)
{
    if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < SigningKeyProvider.MinLength)
        throw new InvalidOperationException(
            "Jwt:Key 缺失或短于 32 字符。本地开发留空即可（会自动生成 App_Data/signing.key）；" +
            "生产用环境变量 Jwt__Key 注入。");
}

// JWT 认证：签发与校验共用同一把密钥，这里只负责把密钥、签发方、受众接进认证管道。
static void ConfigureJwtAuthentication(IServiceCollection services, IConfiguration configuration, string jwtKey)
{
    services.AddAuthentication(options =>
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
            ValidIssuer = configuration["Jwt:Issuer"],
            ValidAudience = configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
        options.Events = new JwtBearerEvents
        {
            // 续期令牌（7 天）与访问令牌（120 分钟）用的是同一把签名密钥，
            // 不区分令牌类型的话，续期令牌就能直接访问业务接口（相当于把访问令牌有效期拉到 7 天）。
            OnTokenValidated = ctx =>
            {
                var tokenType = ctx.Principal?.FindFirst(AuthService.TokenTypeClaim)?.Value;
                if (tokenType == AuthService.TokenTypeRefresh)
                    ctx.Fail("该令牌仅用于续期，不能访问业务接口");
                return Task.CompletedTask;
            }
        };
    });
}

// 过滤器顺序即安全语义（清洗 → 授权兜底 → 审计），抽取时保持注册先后不变。
static void ConfigureControllers(IServiceCollection services)
{
    services.AddControllers(o =>
        {
            // 入参 XSS 清洗：必须排在其它过滤器之前——后面任何过滤器/动作方法
            o.Filters.Add<XssGuardFilter>();
            // 全局授权兜底：未声明 [Authorize]/[AllowAnonymous] 的接口一律拒绝，杜绝「忘标注=匿名可访问」。
            o.Filters.Add<RequireAuthorizationFilter>();
            // 关键写操作自动审计：POST/PUT/DELETE/PATCH 自动落 system_log（需求 4.2.1「全量记录」）。
            o.Filters.Add<AuditLogFilter>();
        })
        .AddJsonOptions(o =>
        {
            // 防御：EF 实体的双向导航属性一旦形成环（如 工单→车辆→工单），
            o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        });
}

// CORS 策略名由函数回传：策略名同时被 AddPolicy 与 UseCors 用到，集中在一处才不会改漏。
static string ConfigureCors(IServiceCollection services, IConfiguration configuration)
{
    const string policyName = "SpaPolicy";

    var corsOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    var corsOriginPatterns = configuration.GetSection("Cors:AllowedOriginPatterns").Get<string[]>() ?? [];
    // 注意 Get<string[]>() 对「配置了空数组」返回的是 length=0 而不是 null，
    // 所以这里必须按长度判断；没配时才给本机与内网的默认白名单。
    if (corsOriginPatterns.Length == 0)
    {
        corsOriginPatterns =
        [
            // 本机任意端口：Vite 开发服务器、预览服务、临时调试端口都在这
            @"^https?://(localhost|127\.0\.0\.1|\[::1\])(:\d{1,5})?$",
            // 内网私有网段：10/8、192.168/16、172.16/12（教室 / 门店局域网）
            @"^https?://((10|192\.168)\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(:\d{1,5})?$",
        ];
    }
    var corsRegexes = corsOriginPatterns
        // 显式给匹配超时：正则表达式会对着请求头里的 Origin 跑，没有上限就可能被构造串拖住线程
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)))
        .ToArray();

    services.AddCors(options =>
    {
        options.AddPolicy(policyName, policy => policy
            .SetIsOriginAllowed(origin =>
                corsOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                || corsRegexes.Any(r => r.IsMatch(origin)))
            .AllowAnyMethod()
            .AllowAnyHeader());
    });

    return policyName;
}

// 反向代理头还原：只信任回环地址，信任范围配置在这里集中管理。
static void ConfigureForwardedHeaders(IServiceCollection services)
{
    services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // ⚠️ 只信任回环地址。信任范围越宽，客户端伪造 X-Forwarded-For 绕过限流的门槛就越低；
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    });
}

// 限流：全站令牌桶 + 认证类接口与支付状态查询各自的分区策略。
static void ConfigureRateLimiter(IServiceCollection services)
{
    services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // 全站：每 IP 令牌桶，容量 120、每秒回补 40。
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

        // 匿名支付状态查询：订单号加随机熵之后仍可能被批量试探，单独按 IP 限流。
        options.AddPolicy(RateLimitPolicies.PayStatus, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

        // 被拒时返回与全站一致的 JSON 结构，前端按 { code, message, success } 统一处理；
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
}

// 出站 HTTP 客户端：全站通用工厂 + ComfyUI 探测专用（绕开系统代理）。
static void ConfigureHttpClients(IServiceCollection services)
{
    // HTTP 客户端工厂：全站出站请求（支付宝网关 / 账户余额查询）
    services.AddHttpClient();

    // ComfyUI 连通性探测专用客户端：必须绕开系统代理。
    services.AddHttpClient("comfy-probe", c => c.Timeout = TimeSpan.FromSeconds(6))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
}

// 业务服务注册：顺序与原文件保持一致（同一服务多次注册时后者覆盖前者，顺序本身就是语义）。
static void RegisterBusinessServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddScoped<ImageStorage>();
    // 登录 IP 防护闸：按来源 IP 累计登录失败并封锁。进程内单例——封锁记录只在内存，
    services.AddSingleton<LoginIpGuard>();
    // 单点登录登记表：同一账号不允许同时登录。进程内单例（本地单实例部署），
    services.AddScoped<SessionRegistry>();
    // 基于 MySQL GET_LOCK 的分布式锁：定时任务（备份、超时单回收）多实例选主
    services.AddScoped<DbLockService>();
    services.AddScoped<AuthService>();
    // 启动服务：按请求作用域注册
    services.AddScoped<SystemStartupService>();
    services.AddScoped<UserService>();
    services.AddScoped<CustomerService>();
    services.AddScoped<VehicleService>();
    services.AddScoped<OrderService>();
    services.AddScoped<PaymentService>();
    // 补缴（顾客自助 + 门店收银）统一服务；SettlementService 在结算生成线上补缴单时依赖它。
    services.AddScoped<SupplementService>();
    services.AddScoped<SettlementService>();
    services.AddScoped<MaintenanceService>();
    services.AddScoped<ReportService>();
    // 员工实时预警作为「通知来源」注入 MessageService：MessageService 只认接口，
    services.AddScoped<INotificationSource>(sp => sp.GetRequiredService<ReportService>());
    services.AddScoped<SystemConfigService>();
    services.AddScoped<LogService>();
    // 备份/恢复：17 张表 → AES-256-GCM 加密文件；恢复时先做快照再覆盖（见 BackupService 注释）。
    services.AddScoped<BackupService>();
    services.AddScoped<RenewalService>();
    services.AddScoped<CheckService>();
    services.AddScoped<PrintService>();
    services.AddScoped<MemberService>();

    // 支付配置：凭证由管理员在管理员端填写，存 system_config（pay.* 前缀），支持「生产→沙箱→模拟」三级自动降级。
    services.AddSingleton<SecretProvider>();
    services.AddScoped<PayConfigService>();

    // 账户余额查询：支付宝/微信官方余额接口，带超时、重试、缓存与降级兜底。
    services.AddScoped<BalanceService>();

    // 支付宝收单：SignType 等静态项读 appsettings 的 Alipay 节点（该节点已不含任何凭证）；
    services.Configure<ERental.Options.AlipayOptions>(configuration.GetSection("Alipay"));
    services.AddScoped<AlipayService>();
    // 微信（APIv3）与银行卡（通用收银网关）正式通道：与支付宝同一套降级约定（凭证齐全走真实，否则模拟收银台）
    services.AddScoped<WechatPayService>();
    services.AddScoped<BankPayService>();
    // 顾客消息（退款去向 / 逾期线上补缴单 / 补缴成功回执）：结算与补缴环节写入，顾客端读取
    services.AddScoped<MessageService>();
}

// 局域网与 AI 生图相关的配置/服务。
static void RegisterLanAndAiServices(IServiceCollection services, IConfiguration configuration)
{
    // 局域网接入：手机/另一台电脑连同一 Wi-Fi 即可访问。开关由启动脚本注入
    services.Configure<ERental.Options.LanOptions>(configuration.GetSection("Lan"));

    // AI 生图：对接本机 ComfyUI 桌面版（默认 127.0.0.1:8188）。
    services.Configure<ERental.Options.AiImageOptions>(configuration.GetSection("AiImage"));
    // ComfyUiClient 用强类型 HttpClient：超时在构造函数里配好，连接由 IHttpClientFactory 池化复用。
    services.AddScoped<ComfyConfigService>();
    // 生成进度跟踪器：ComfyUiClient 收 WS 消息 → AiImageService 写入 → 前端轮询读取（进程内单例）
    services.AddSingleton<AiProgressTracker>();
    services.AddHttpClient<ComfyUiClient>();
    services.AddScoped<AiImageService>();
}

// 后台任务：超时订单回收 + 定时备份。
static void RegisterHostedServices(IServiceCollection services)
{
    // 后台兜底任务：每分钟回收「超时未支付」的订单并释放车辆，防止车辆被永久锁死。
    services.AddHostedService<PendingOrderCleanupService>();
    // 定时备份：每天 Backup:DailyAt（默认 02:30）导出加密备份并按保留数清理旧文件。
    services.AddHostedService<BackupSchedulerService>();
}

// 只记来源、不记密钥：排查「为什么重启后令牌失效 / 备份解不开」时，这一行就够定位了
static void ReportSigningKeySource(ILogger logger, string source)
{
    if (source.StartsWith("新生成", StringComparison.Ordinal))
        logger.LogWarning("JWT 签名密钥{Source}。请勿提交该文件；生产环境建议用环境变量 Jwt__Key 注入。", source);
    else
        logger.LogInformation("JWT 签名密钥来源：{Source}", source);
}

// 启动序列：建表 → 老库平滑升级 → 种子数据，三步均幂等。
// 必须放在 Build 之后、中间件注册之前——建表失败就别把管道挂起来了。
static void RunStartupTasks(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var startup = scope.ServiceProvider.GetRequiredService<SystemStartupService>();
    startup.Startup();
}

// 中间件管道：顺序即行为（异常 → 反代头 → 静态文件 → HTTPS → CORS → 限流 → 认证 → 单点 → 授权 → 路由 → Swagger），
// 任何调整都要重读各中间件处的注释，这里只是照搬原有先后。
static void ConfigurePipeline(WebApplication app, string corsPolicyName)
{
    app.UseMiddleware<ExceptionMiddleware>();

    // 反代头还原必须排在任何「读客户端 IP」的组件之前：
    app.UseForwardedHeaders();

    // 静态资源：对外提供维保现场照片（wwwroot/uploads/maintenance/**）。
    app.UseStaticFiles();

    // 生产部署强制 HTTPS；开发期前端通过 http://localhost:5105 反向代理调用，
    if (!app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    app.UseCors(corsPolicyName);

    // 限流位置很关键：必须排在认证之前，否则未认证请求可以无限次打到认证逻辑上。
    app.UseRateLimiter();

    app.UseAuthentication();
    // 单点登录校验：必须夹在 Authentication 与 Authorization 之间。
    app.UseMiddleware<SingleSessionMiddleware>();
    app.UseAuthorization();
    app.MapControllers();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }
}

// 启动日志：把局域网访问地址打出来，避免同事拿着 0.0.0.0 的监听地址去连。
// 配置只在确有内网地址时才读取，与原来的惰性读取时机一致。
static void PrintLanEndpoints(IConfiguration configuration)
{
    var lanIp = NetworkHelper.ResolveLanIp();
    if (string.IsNullOrWhiteSpace(lanIp)) return;

    // 员工端与顾客端为同一份 React 工程，
    var lanCfg = configuration.GetSection("Lan").Get<ERental.Options.LanOptions>() ?? new();
    var frontScheme = string.Equals(lanCfg.FrontScheme.Trim(), "http", StringComparison.OrdinalIgnoreCase)
        ? "http" : "https";
    Console.WriteLine($"[局域网] 前端     {frontScheme}://{lanIp}:{lanCfg.CustomerPort}");
    Console.WriteLine($"[局域网] 接口文档 http://{lanIp}:5105/swagger");
}

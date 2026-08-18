using drive_api.Models;
using drive_api.Services.AdminManagement;
using drive_api.Services.AI;
using drive_api.Services.Cache;
using drive_api.Services.Config;
using drive_api.Services.Db;
using drive_api.Services.Email;
using drive_api.Services.FileManagement;
using drive_api.Services.MQ;
using drive_api.Services.Tool;
using drive_api.Services.TrafficStatistics;
using drive_api.Services.UserManagement;
using drive_api.Services.WebDav;
using driveApi.Services.JWT;
using FreeRedis;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.IdentityModel.Tokens;
using Microsoft.SemanticKernel;
using MimeDetective;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using SqlSugar;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.RateLimiting;
var builder = WebApplication.CreateBuilder(args);
var appConfig = new AppConfigInfo(builder.Configuration);
if (!SystemdInitilization()) return;
ServerInitialization();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        //设置为 null，表示不使用任何命名策略（保持原样）
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
    });
builder.Services.AddMemoryCache();
builder.Services.AddOpenApi();

var MyAllowSpecificOrigins = "_myAllowSpecificOrigins";
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: MyAllowSpecificOrigins,
                      policy =>
                      {
                          // 在开发环境中，允许任何来源、任何方法、任何头部
                          policy.AllowAnyOrigin()
                                .AllowAnyMethod()
                                .AllowAnyHeader();
                      });
});
InitLogToDB();
RateLimiterServerInitialization();
builder.Services.AddScoped<IWebDavProvider, MemoryWebDavProvider>();
builder.Services.AddScoped<AdminHandler>();
builder.Services.AddScoped<FileHandler>();
builder.Services.AddScoped<UserHandler>();
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton<Helper>();
//注册后台托管服务 定时清理分片文件
builder.Services.AddHostedService<CleanupChunkFilesWorker>();
//文件类型检测服务初始化
builder.Services.AddSingleton<IContentInspector>(sp =>
{
    return new ContentInspectorBuilder()
    {
        Definitions = MimeDetective.Definitions.DefaultDefinitions.All()
    }.Build();
});

MQInitialization();
AIInitilization();
KestrelServerInitialization();
JWTServiceInitialization();
DbServiceInitialization();
CacheServiceInitialization();
EmailServiceInitialization();
TrafficStatisticsInitialization();
var app = builder.Build();

DbInitialization();
await FileManagementInitialization();
CachetInitialization();
await EmailInitialization();
AssetsInitialization();
await AIModelInitialization();
app.MapOpenApi();

if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();

}


app.UseCors(MyAllowSpecificOrigins);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
var provider = new FileExtensionContentTypeProvider();
provider.Mappings[".ts"] = "application/javascript";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider
});
app.Use(async (context, next) =>
{
    if (context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers.Append("Allow", "OPTIONS, PROPFIND, GET");
        context.Response.Headers.Append("DAV", "1, 2");
        context.Response.Headers.Append("MS-Author-Via", "DAV");
        context.Response.StatusCode = 200;
        return;
    }
    await next();
});


app.MapGet("/", () => Results.Redirect("/home"));
app.MapFallbackToFile("index.html");
//webdav服务
app.Map("/webdav", webdavApp =>
{
    webdavApp.UseMiddleware<WebDavMiddleware>();
});
//添加流量统计中间件
app.UseMiddleware<ApiTrafficMiddleware>();
app.Run();

//注册启动服务
bool SystemdInitilization()
{
    // 如果命令行参数包含 "init"，则执行安装服务的逻辑
    if (args.Contains("install"))
    {
        InstallService("driveApi");
        return false; // 安装完成后直接退出程序
    }
    else if (args.Contains("uninstall"))
    {
        UninstallService("driveApi");
        return false;
    }
    //在win上注册为服务
    builder.Host.UseWindowsService(options =>
    {

        options.ServiceName = "driveApi";
    });
    //在linux上注册为systemd服务
    builder.Host.UseSystemd();
    return true;


    //注册时执行安装服务的逻辑
    void InstallService(string serviceName)
    {
        // 获取当前可执行文件的绝对路径
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            Console.WriteLine("无法获取程序路径，安装失败。");
            return;
        }

        try
        {
            int rq = 0;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine($"正在 Windows 上安装服务: {serviceName}...");

                // 执行 sc create (注意 binPath= 后面的空格，以及 start= auto 设置开机自启)
                rq += Helper.ExecuteCommand("sc", $"create {serviceName} binPath= \"{processPath}\" start= auto");
                // 执行 sc start
                rq += Helper.ExecuteCommand("sc", $"start {serviceName}");
                if (rq != 0)
                {
                    Console.WriteLine("Windows 服务安装失败，请保证以管理员权限执行命令");
                }
                else
                {
                    Console.WriteLine("Windows 服务安装并启动成功！");
                }

            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.WriteLine($"正在 Linux 上安装 Systemd 服务: {serviceName}...");

                var serviceFilePath = $"/etc/systemd/system/{serviceName}.service";
                var workingDirectory = Path.GetDirectoryName(processPath);

                // 生成 service 文件内容
                var serviceContent = $@"
[Unit]
Description={serviceName} Application
After=network.target

[Service]
Type=notify
ExecStart={processPath}
WorkingDirectory={workingDirectory}
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier={serviceName}
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
";
                // 写入配置文件
                File.WriteAllText(serviceFilePath, serviceContent.Trim());

                // 执行 systemctl 命令
                rq += Helper.ExecuteCommand("systemctl", "daemon-reload");
                rq += Helper.ExecuteCommand("systemctl", $"enable {serviceName}.service");
                rq += Helper.ExecuteCommand("systemctl", $"start {serviceName}.service");
                if (rq != 0)
                {
                    Console.WriteLine("Linux Systemd 服务安装失败,请保证以root权限执行命令");
                }
                else
                {
                    Console.WriteLine("Linux Systemd 服务安装并启动成功！");
                }

            }
            else
            {
                Console.WriteLine("当前操作系统不支持自动安装。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"安装过程中发生错误: {ex.Message}");
            Console.WriteLine("请确保你使用了 管理员权限 (Windows) 或 root 权限 (Linux) 运行此命令！");
        }
    }
    //卸载服务的逻辑
    void UninstallService(string serviceName)
    {
        try
        {
            int rq = 0;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine($"正在 Windows 上卸载服务: {serviceName}...");

                // 1. 先尝试停止服务
                rq += Helper.ExecuteCommand("sc", $"stop {serviceName}");
                // 给系统一点时间停止服务
                System.Threading.Thread.Sleep(1000);
                // 2. 删除服务
                rq += Helper.ExecuteCommand("sc", $"delete {serviceName}");
                if (rq != 0)
                {
                    Console.WriteLine("Windows 服务卸载失败，请保证以管理员权限执行命令");
                }
                else
                {
                    Console.WriteLine("Windows 服务卸载成功！");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Console.WriteLine($"正在 Linux 上卸载 Systemd 服务: {serviceName}...");

                var serviceFilePath = $"/etc/systemd/system/{serviceName}.service";

                // 1. 停止服务
                rq += Helper.ExecuteCommand("systemctl", $"stop {serviceName}.service");
                // 2. 取消开机自启
                rq += Helper.ExecuteCommand("systemctl", $"disable {serviceName}.service");

                // 3. 删除服务配置文件
                if (File.Exists(serviceFilePath))
                {
                    File.Delete(serviceFilePath);
                    Console.WriteLine($"已删除配置文件: {serviceFilePath}");
                }

                // 4. 重新加载 systemd 配置
                rq += Helper.ExecuteCommand("systemctl", "daemon-reload");

                if (rq != 0)
                {
                    Console.WriteLine("Linux Systemd 服务卸载失败,请保证以root权限执行命令");
                }
                else
                {
                    Console.WriteLine("Linux Systemd 服务卸载成功！");
                }
            }
            else
            {
                Console.WriteLine("当前操作系统不支持自动卸载。");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"卸载过程中发生错误: {ex.Message}");
            Console.WriteLine("请确保你使用了 管理员权限 (Windows) 或 root 权限 (Linux) 运行此命令！");
        }
    }

}
void ServerInitialization()
{
    var serverSettings = appConfig.serverSettings;
    builder.WebHost.ConfigureKestrel(options =>
    {

        if (serverSettings == null) return;

        // ==========================================
        // 配置 IPv4 终结点
        // ==========================================
        if (serverSettings.IPv4 != null)
        {
            // 尝试解析 IP，如果是 0.0.0.0 则使用 IPAddress.Any
            var ipV4Address = serverSettings.IPv4.Ip == "0.0.0.0"
                ? IPAddress.Any
                : IPAddress.Parse(serverSettings.IPv4.Ip);

            options.Listen(ipV4Address, serverSettings.IPv4.Port, listenOptions =>
            {
                if (serverSettings.IPv4.EnableSsl && !string.IsNullOrEmpty(serverSettings.IPv4.CertPath))
                {
                    // 获取证书的绝对路径
                    var certPath = Path.Combine(builder.Environment.ContentRootPath, serverSettings.IPv4.CertPath);

                    if (File.Exists(certPath))
                    {
                        // 启用 HTTPS 并挂载证书
                        listenOptions.UseHttps(certPath, serverSettings.IPv4.CertPassword);
                    }
                    else
                    {
                        // 容错：开了开关但找不到证书文件，降级为普通 HTTP 监听，防止程序崩溃
                        Console.WriteLine($"[警告] IPv4 证书文件不存在: {certPath}，已降级为普通 HTTP 监听");
                    }
                }
                // 如果 EnableSsl 为 false，不调用 UseHttps()，Kestrel 默认就是 HTTP 监听
            });
        }

        // ==========================================
        // 配置 IPv6 终结点
        // ==========================================
        if (serverSettings.IPv6 != null)
        {
            // 去除 JSON 中可能带的方括号 [::] 以便于 IPAddress 解析
            string cleanIp6 = serverSettings.IPv6.Ip.Replace("[", "").Replace("]", "");
            var ipV6Address = cleanIp6 == "::"
                ? IPAddress.IPv6Any
                : IPAddress.Parse(cleanIp6);

            options.Listen(ipV6Address, serverSettings.IPv6.Port, listenOptions =>
            {
                if (serverSettings.IPv6.EnableSsl && !string.IsNullOrEmpty(serverSettings.IPv6.CertPath))
                {
                    var certPath = Path.Combine(builder.Environment.ContentRootPath, serverSettings.IPv6.CertPath);

                    if (File.Exists(certPath))
                    {
                        listenOptions.UseHttps(certPath, serverSettings.IPv6.CertPassword);
                    }
                    else
                    {
                        Console.WriteLine($"[警告] IPv6 证书文件不存在: {certPath}，已降级为普通 HTTP 监听");
                    }
                }
            });
        }
    });
}
void AIInitilization()
{

    builder.Services.AddSingleton<AiVectorService>();


    var customHttpClient = new HttpClient
    {
        BaseAddress = new Uri(appConfig.aiSetting.BaseURL)
    };
    // 配置 Semantic Kernel
    builder.Services.AddScoped<AIagent>();
    builder.Services.AddScoped(sp =>
    {
        var kernel = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(
            modelId: appConfig.aiSetting.Model,
            apiKey: appConfig.aiSetting.ApiKey,
            httpClient: customHttpClient
            )
            .Build();

        // 注入插件
        kernel.Plugins.AddFromObject(sp.GetRequiredService<AIagent>(), "CloudDrive");
        return kernel;
    });
}

//消息队列初始化  用来计算文件向量 缩略图等耗时任务
void MQInitialization()
{

    if (appConfig.mqSetting.MqType == "RabbitMQ")
    {
        var connStr = builder.Configuration.GetConnectionString("RabbitMQ");
        builder.Services.AddSingleton<IMessageBus>(new RabbitMQMessageBus(connStr));
    }
    else
    {
        builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
    }

    // 注册缩略图制作消费者
    builder.Services.AddHostedService<ImgCompConsumer>();
    builder.Services.AddHostedService<ImgVectorConsumer>();
}

//频率限制服务初始化
void RateLimiterServerInitialization()
{
    builder.Services.AddRateLimiter(options =>
    {
        // 1. 自定义被拦截时的返回（JSON格式）
        options.OnRejected = async (context, token) =>
        {
            context.HttpContext.Response.StatusCode = 429;
            context.HttpContext.Response.ContentType = "application/json; charset=utf-8"; // 确保中文不乱码

            var errorResponse = new
            {
                code = 429,
                data = "访问过于频繁，请喝杯茶再来！", // 自定义提示
                ip = context.HttpContext.Connection.RemoteIpAddress?.ToString()
            };

            await context.HttpContext.Response.WriteAsJsonAsync(errorResponse, token);
        };

        // 2. 配置针对 IP 的策略  主要是针对邮箱验证码接口做限制
        options.AddPolicy("EmailIpLimiter", httpContext =>
        {
            // 获取客户端 IP 作为分区键
            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown_ip";

            // 为每个 IP 创建一个分区的限制器
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: remoteIp,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 1,          // 每个 IP 允许 1 次请求
                    Window = TimeSpan.FromSeconds(60), // 每 60 秒为一个窗口
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0            // 不排队，超过直接拒绝
                });
        });
    });
}
//日志到数据库服务初始化
void InitLogToDB()
{
    Serilog.Debugging.SelfLog.Enable(Console.Error);
    LoggerConfiguration conf = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .MinimumLevel.Debug() //日志级别
    .Enrich.FromLogContext()
    .WriteTo.Console()//控制台输出
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "log.txt"));//文件输出

    if (appConfig.dbSetting.LogWriteToDB)
    {
        conf.WriteTo.Async(a => a.PostgreSQL( // 异步批量输出到数据库
        connectionString: appConfig.dbSetting.ConnectionString,
        tableName: "sys_logs", // 数据库中自动建表的表名
        needAutoCreateTable: true,
        restrictedToMinimumLevel: LogEventLevel.Information));//记录的日志级别
    }
    Log.Logger = conf.CreateLogger();
    builder.Host.UseSerilog();

}
void KestrelServerInitialization()
{
    builder.Services.Configure<KestrelServerOptions>(options =>
    {
        // 设置 Kestrel 的最大请求体大小
        // 这是最底层的、针对所有请求的硬限制
        options.Limits.MaxRequestBodySize = (long)appConfig.fileSetting.MaxFileSize;
    });
    builder.Services.Configure<FormOptions>(options =>
    {
        // 单个 multipart/form-data 请求的总长度限制
        options.MultipartBodyLengthLimit = (long)appConfig.fileSetting.MaxFileSize;

        // 其他可选配置:
        // options.ValueLengthLimit = int.MaxValue; // 单个字段值的长度限制
        // options.KeyLengthLimit = int.MaxValue; // 字段名的长度限制
        // options.MultipartHeadersLengthLimit = int.MaxValue; // multipart 头部的长度限制
    });
}
//  JWT服务初始化
void JWTServiceInitialization()
{
    builder.Services.AddScoped<JwtEventHandler>();
    // 从配置中读取 JWT 设置对象（比如密钥等信息）
    var jwtOpt = appConfig.jwtSetting;

    if (jwtOpt == null || string.IsNullOrWhiteSpace(jwtOpt.SigningKey) || jwtOpt.ExpireSeconds <= 0)
    {

        throw new InvalidOperationException("JWT 配置缺失或无效，请检查 appsettings.json 文件中的 'JwtConfig' 部分。");

    }
    // 注册 JWT Bearer 身份认证服务
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(x =>
        {


            // 把密钥字符串转为字节数组
            byte[] keyBytes = Encoding.UTF8.GetBytes(jwtOpt.SigningKey);

            // 用密钥生成对称安全密钥对象
            var secKey = new SymmetricSecurityKey(keyBytes);

            // 配置 Token 验证参数
            x.TokenValidationParameters = new TokenValidationParameters()
            {
                // 是否验证 Token 的签发者（Issuer）
                ValidateIssuer = false,
                ClockSkew = TimeSpan.Zero,
                // 是否验证 Token 的接收方（Audience）
                ValidateAudience = false,

                // 是否验证 Token 的过期时间，生产环境一般要打开
                ValidateLifetime = true,

                // 是否验证 Token 的签名，生产环境一定要开
                ValidateIssuerSigningKey = true,

                // 用来验证签名的密钥
                IssuerSigningKey = secKey
            };
            var jwtEventHandler = builder.Services.BuildServiceProvider().GetRequiredService<JwtEventHandler>();
            x.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = jwtEventHandler.OnAuthenticationFailed,
                OnChallenge = jwtEventHandler.OnChallenge
            };
        });
}
//数据库服务初始化
void DbServiceInitialization()
{
    builder.Services.AddHttpContextAccessor();

    // 注册 DbEventHandler
    builder.Services.AddScoped<DbEventHandler>();
    var dbc = appConfig.dbSetting;
    if (dbc == null || string.IsNullOrWhiteSpace(dbc.ConnectionString))
    {
        throw new InvalidOperationException("dbc 配置缺失或无效，请检查 appsettings.json 文件中的 'DbConfig' 部分。");
    }

    // 注册 SqlSugar 用 AddScoped
    builder.Services.AddScoped<ISqlSugarClient>(s =>
    {
        var dbEventHandler = s.GetRequiredService<DbEventHandler>();

        // Scoped 用 SqlSugarClient 
        SqlSugarClient sqlSugar = new SqlSugarClient(new ConnectionConfig()
        {
            DbType = (DbType)dbc.DbType,
            ConnectionString = dbc.ConnectionString,
            IsAutoCloseConnection = true,

        },
        db =>
        {
            // 每次上下文都会执行
            // 将解析出来的 handler 绑定到 AOP 事件上
            db.Aop.OnLogExecuted = dbEventHandler.OnLogExecued;
            db.Aop.OnError = dbEventHandler.OnError;

        });


        return sqlSugar;
    });
}
//流量统计服务初始化
void TrafficStatisticsInitialization()
{
    //内存存储流量服务
    builder.Services.AddSingleton<TrafficStoreService>();
    //注册后台托管服务 定时保存到数据库
    builder.Services.AddHostedService<TrafficFlushWorker>();
}
//缓存服务初始化
void CacheServiceInitialization()
{
    //注册缓存服务
    var cacheConfig = appConfig.cacheSetting;
    if (cacheConfig == null || cacheConfig.CacheType.IsWhiteSpace() == true)
    {
        throw new InvalidOperationException("Cache 配置缺失或无效，请检查 appsettings.json 文件中的 'CacheConfig' 部分。");
    }
    if (cacheConfig.CacheType == "MemoryCache")
    {
        builder.Services.AddSingleton<drive_api.Services.Cache.ICache, drive_api.Services.Cache.MemoryCache>();
        return;
    }
    if (cacheConfig.CacheType == "Redis")
    {
        RedisClient? redisClient = null;
        try
        {
            Console.WriteLine("正在连接Redis...");
            redisClient = new RedisClient(cacheConfig.ConnectionString);
            // 2. 立即执行一个操作来验证连接
            redisClient.Ping();
            builder.Services.AddSingleton<ICache, Redis>(); // 注册你的服务
        }
        catch (Exception ex)
        {
            // 4. 如果连接失败，记录警告并注册备用方案
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"警告：Redis 连接失败 ({ex.Message})，已自动切换到内置内存缓存。");
            Console.ResetColor();

            // 确保释放失败的连接
            redisClient?.Dispose();

            // 注册 MemoryCache作为备用
            builder.Services.AddSingleton<ICache, MemoryCache>();
        }
    }
    else
    {
        throw new InvalidOperationException("CacheConfig无效,目前只支持MemoryCache和Redis，请检查 appsettings.json 文件中的 'CacheConfig.CacheType' 部分。");
    }
}
//邮箱服务初始化
void EmailServiceInitialization()
{
    builder.Services.AddSingleton<EmailHandler>();
}
//数据库初始化
async Task DbInitialization()
{

    // 创建一个临时的依赖注入作用域
    using (var scope = app.Services.CreateScope())
    {
        var dbClient = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        app.Logger.LogInformation("正在检查并创建数据库（如果不存在）...");
        try
        {
            dbClient.DbMaintenance.CreateDatabase("drive");

        }
        catch (Exception e)
        {

            app.Logger.LogError($"数据库初始化失败：{e.Message}");

        }
        //初始化表
        if (dbClient.CurrentConnectionConfig.DbType != DbType.PostgreSQL)
        {
            app.Logger.LogWarning("当前非PostgreSQL数据库 已关闭AI功能，目前不想引用外部向量数据库，暂时用的是PG向量插件，若要使用，请确保安装了插件");
            appConfig.aiSetting.Enable = false;
        }
        if (!dbClient.DbMaintenance.IsAnyTable("users"))
        {
            app.Logger.LogWarning("users表不存在，将重新创建表");
            dbClient.CodeFirst.InitTables(typeof(drive_api.Models.User));
        }
        if (!dbClient.DbMaintenance.IsAnyTable("file_info"))
        {
            app.Logger.LogWarning("file_info表不存在，将重新创建表");
            dbClient.CodeFirst.InitTables(typeof(drive_api.Models.FileInfo));
        }
        if (!dbClient.DbMaintenance.IsAnyTable("folder_info"))
        {
            app.Logger.LogWarning("folder_info表不存在，将重新创建表");
            dbClient.CodeFirst.InitTables(typeof(drive_api.Models.UserFolderInfo));
        }
        if (!dbClient.DbMaintenance.IsAnyTable("file_share"))
        {
            app.Logger.LogWarning("file_share表不存在，将重新创建表");
            dbClient.CodeFirst.InitTables(typeof(drive_api.Models.FileShareInfo));
        }
        if (!dbClient.DbMaintenance.IsAnyTable("traffic_statistics"))
        {
            app.Logger.LogWarning("traffic_statistics表不存在，将重新创建表");
            dbClient.CodeFirst.InitTables(typeof(drive_api.Models.TrafficStatisticsModel));
        }
        if (!dbClient.DbMaintenance.IsAnyTable("img_vectorcs") && appConfig.aiSetting.Enable == true && dbClient.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
        {

            app.Logger.LogWarning("img_vectorcs表不存在，将重新创建表，注意：需要安装对应数据库的向量插件,并且进入drive数据库输入 CREATE EXTENSION vector 来启用,否则无法使用语义搜索");
            dbClient.CodeFirst.InitTables(typeof(drive_api.Models.ImgVectorcs));
            dbClient.SqlQueryable<ImgVectorcs>("CREATE EXTENSION vector;CREATE INDEX idx_img_vector_hnsw ON img_vectorcs USING hnsw (vector vector_cosine_ops);");
        }



    }



}
//文件管理初始化
async Task FileManagementInitialization()
{

    // 创建一个临时的依赖注入作用域
    using (var scope = app.Services.CreateScope())
    {
        var fileHandler = scope.ServiceProvider.GetRequiredService<FileHandler>();
        fileHandler.VerifyDirectory();
        //Console.WriteLine(await fileHandler.GetUserDirectoryFileInfo("1001"));
    }



}
//缓存初始化
void CachetInitialization()
{

    using (var scope = app.Services.CreateScope())
    {

        var cache = scope.ServiceProvider.GetRequiredService<ICache>();

        app.Logger.LogInformation($"缓存初始化完成，当前使用缓存:{cache.GetType().Name}");

    }

}
//邮箱初始化
async Task EmailInitialization()
{
    using (var scope = app.Services.CreateScope())
    {

        var eh = scope.ServiceProvider.GetRequiredService<EmailHandler>();
        await eh.Init();

    }
}

//资源目录初始化
void AssetsInitialization()
{
    using (var scope = app.Services.CreateScope())
    {

        //获取静态目录
        IWebHostEnvironment rootpath = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        string wwwroot = Path.Combine(rootpath.ContentRootPath, "wwwroot");
        rootpath.WebRootPath = wwwroot;
        if (!Directory.Exists(wwwroot))
        {
            Directory.CreateDirectory(wwwroot);
        }


        string[] paths = new[] { Path.Combine(rootpath.WebRootPath, "driveassets/avatar"), Path.Combine(rootpath.WebRootPath, "driveassets/imgcomp") };
        foreach (var item in paths)
        {
            if (!Directory.Exists(item))
            {
                Directory.CreateDirectory(item);
            }
        }
    }

    app.Logger.LogInformation("当前使用的MQ类型：" + appConfig.mqSetting.MqType);
}
//初始化模型
async Task AIModelInitialization()
{
    using (var scope = app.Services.CreateScope())
    {
        if (appConfig.aiSetting.Enable == true)
        {
            var aiService = scope.ServiceProvider.GetRequiredService<AiVectorService>();
            app.Logger.LogInformation("正在初始化AI模型...");
            IWebHostEnvironment rootpath = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            await aiService.InitModelsAsync(rootpath.ContentRootPath);
            app.Logger.LogInformation("模型加载完成");
        }
    }
}
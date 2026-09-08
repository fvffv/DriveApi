using drive_api.Models;
using drive_api.Services.AI;
using drive_api.Services.Cache;
using drive_api.Services.Db;
using drive_api.Services.Email;
using drive_api.Services.FileManagement;
using drive_api.Services.MQ;
using driveApi.Services.JWT;
using Serilog;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace drive_api.Services.Config
{

    public class SystemConfigUpdateDto
    {
        public JwtSetting jwtSetting { get; set; }
        public FileSetting fileSetting { get; set; }
        public DbSetting dbSetting { get; set; }
        public CacheSetting cacheSetting { get; set; }
        public UserSetting userSetting { get; set; }
        public EmailSetting emailSetting { get; set; }
        public BasicInformation basicInformation { get; set; }
        public MQSetting mqSetting { get; set; }
        public AISetting aiSetting { get; set; }
        public ServerSettings serverSettings { get; set; }
    }
    public class SerilogSetting
    {
        [JsonPropertyName("MinimumLevel")]
        public MinimumLevelSetting MinimumLevel { get; set; } = new();
    }

    public class MinimumLevelSetting
    {
        [JsonPropertyName("Default")]
        public string Default { get; set; } = "Information";

        [JsonPropertyName("Override")]
        public Dictionary<string, string> Override { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft"] = "Warning",
            ["System"] = "Warning",
            ["Microsoft.Hosting.Lifetime"] = "Information",
            ["Microsoft.AspNetCore.Watch.BrowserRefresh"] = "Warning"
        };
    }
    public class AppConfigInfo
    {
        // 增加一个静态锁，防止多线程并发修改文件导致文件损坏
        private static readonly object _saveLock = new object();
       
        private IConfiguration _configuration { get; set; }
        [JsonPropertyName("Serilog")]
        public SerilogSetting Serilog { get; set; } = new();

        [JsonPropertyName("JwtConfig")]
        public JwtSetting jwtSetting { get; set; } = new JwtSetting();

        [JsonPropertyName("FileConfig")]
        public FileSetting fileSetting { get; set; } = new FileSetting();

        [JsonPropertyName("DbConfig")]
        public DbSetting dbSetting { get; set; } = new DbSetting();

        [JsonPropertyName("CacheConfig")]
        public CacheSetting cacheSetting { get; set; } = new CacheSetting();

        [JsonPropertyName("UserConfig")]
        public UserSetting userSetting { get; set; } = new UserSetting();

        [JsonPropertyName("EmailConfig")]
        public EmailSetting emailSetting { get; set; } = new EmailSetting();

        [JsonPropertyName("BasicInformation")]
        public BasicInformation basicInformation { get; set; } = new BasicInformation();

        [JsonPropertyName("MQConfig")]
        public MQSetting mqSetting { get; set; } = new MQSetting();

        [JsonPropertyName("AIConfig")]
        public AISetting aiSetting { get; set; } = new AISetting();

        [JsonPropertyName("ServerSettings")]
        public ServerSettings serverSettings { get; set; } = new ServerSettings();
        public AppConfigInfo(IConfiguration configuration)
        {
            _configuration = configuration;
            jwtSetting = _configuration.GetSection("JwtConfig").Get<JwtSetting>();
            fileSetting = _configuration.GetSection("FileConfig").Get<FileSetting>();
            dbSetting = _configuration.GetSection("DbConfig").Get<DbSetting>();
            cacheSetting = _configuration.GetSection("CacheConfig").Get<CacheSetting>();
            userSetting = _configuration.GetSection("UserConfig").Get<UserSetting>();
            emailSetting = _configuration.GetSection("EmailConfig").Get<EmailSetting>();
            basicInformation = _configuration.GetSection("BasicInformation").Get<BasicInformation>();
            mqSetting = _configuration.GetSection("MQConfig").Get<MQSetting>();
            aiSetting = _configuration.GetSection("AIConfig").Get<AISetting>();
            serverSettings = _configuration.GetSection("ServerSettings").Get<ServerSettings>();

        }
        // 补上一个无参构造函数
        public AppConfigInfo() { }
        /// <summary>
        /// 线程安全地更新内存配置并持久化到文件
        /// </summary>
        public bool UpdateAndSaveConfig(SystemConfigUpdateDto req)
        {

            lock (_saveLock)
            {

                //利用反射修改值
                ApplyChanges(this.jwtSetting, req.jwtSetting);
                ApplyChanges(this.fileSetting, req.fileSetting);
                ApplyChanges(this.dbSetting, req.dbSetting);
                ApplyChanges(this.cacheSetting, req.cacheSetting);
                ApplyChanges(this.userSetting, req.userSetting);
                ApplyChanges(this.emailSetting, req.emailSetting);
                ApplyChanges(this.basicInformation, req.basicInformation);
                ApplyChanges(this.mqSetting, req.mqSetting);
                ApplyChanges(this.aiSetting, req.aiSetting);
                ApplyChanges(this.serverSettings, req.serverSettings);

                //更新 JSON 文件
                string filePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (!File.Exists(filePath)) return false;

                string jsonString = File.ReadAllText(filePath);
                var documentOptions = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                var jsonNode = JsonNode.Parse(jsonString, documentOptions: documentOptions) as JsonObject;
                if (jsonNode == null) return false;

                //覆盖节点
                jsonNode["JwtConfig"] = JsonSerializer.SerializeToNode(this.jwtSetting);
                jsonNode["FileConfig"] = JsonSerializer.SerializeToNode(this.fileSetting);
                jsonNode["DbConfig"] = JsonSerializer.SerializeToNode(this.dbSetting);
                jsonNode["CacheConfig"] = JsonSerializer.SerializeToNode(this.cacheSetting);
                jsonNode["UserConfig"] = JsonSerializer.SerializeToNode(this.userSetting);
                jsonNode["EmailConfig"] = JsonSerializer.SerializeToNode(this.emailSetting);
                jsonNode["BasicInformation"] = JsonSerializer.SerializeToNode(this.basicInformation);
                jsonNode["MQConfig"] = JsonSerializer.SerializeToNode(this.mqSetting);
                jsonNode["AIConfig"] = JsonSerializer.SerializeToNode(this.aiSetting);
                jsonNode["ServerSettings"] = JsonSerializer.SerializeToNode(this.serverSettings);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string outputJson = jsonNode.ToJsonString(options);

                string tempFilePath = filePath + ".tmp";
                File.WriteAllText(tempFilePath, outputJson);
                File.Move(tempFilePath, filePath, overwrite: true);

                return true;

            }
        }
      
        /// <summary>
        /// 将 source 中的有效值（非空、非 "******"）更新到 target 中
        /// </summary>
        private void ApplyChanges<T>(T target, T source) where T : class
        {
            if (source == null || target == null) return;

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (!prop.CanRead || !prop.CanWrite) continue;

                var sourceValue = prop.GetValue(source);
                if (sourceValue == null) continue;

                // 处理字符串：忽略空值和密码掩码
                if (sourceValue is string strValue)
                {
                    if (!string.IsNullOrEmpty(strValue) && strValue != "******")
                    {
                        prop.SetValue(target, strValue);
                    }
                }
                // 处理嵌套类（例如 ServerSettings.IPv4）
                else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string))
                {
                    var targetValue = prop.GetValue(target);
                    if (targetValue != null)
                    {
                        ApplyChanges(targetValue, sourceValue); // 递归更新嵌套对象
                    }
                }
                // 处理值类型（int, bool 等）直接覆盖
                else
                {
                    prop.SetValue(target, sourceValue);
                }
            }
        }
    }
}

using drive_api.Services.Config;
using MailKit.Net.Smtp;
using MimeKit;

namespace drive_api.Services.Email
{
    public class EmailService
    {
        private readonly ILogger<EmailService> _logger;
        private readonly AppConfigInfo _appConfigInfo;
        private readonly IWebHostEnvironment _env;

        // 模板内容缓存，加载一次即可
        private static string EmailCodeTemplate;


        public EmailService(ILogger<EmailService> logger, AppConfigInfo appConfigInfo, IWebHostEnvironment env)
        {
            _env = env;
            _logger = logger;
            _appConfigInfo = appConfigInfo;

        }

        /// <summary>
        /// 初始化
        /// </summary>
        public async Task Init()
        {



            try
            {
                using var cl = new SmtpClient();
                _logger.LogInformation("正在连接邮箱服务...");
                await cl.ConnectAsync(_appConfigInfo.emailSetting.SmtpServer, _appConfigInfo.emailSetting.SmtpPort, true);
                await cl.AuthenticateAsync(_appConfigInfo.emailSetting.Email, _appConfigInfo.emailSetting.PassWord);
                _logger.LogInformation("邮箱服务连接成功");
                EmailCodeTemplate = await GetTemplateContent();
                _logger.LogInformation("邮件模板加载成功");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "邮件服务初始化失败");
             
            }
        }

        /// <summary>
        /// 发送验证码邮件
        /// </summary>
        public async Task<bool> SeedCodeEmail(string toEmail, string code, string userName)
        {
            string SystemName = _appConfigInfo.basicInformation.ProjectName;

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(SystemName, _appConfigInfo.emailSetting.Email));
            message.To.Add(new MailboxAddress(userName, toEmail));
            message.Subject = $"[{SystemName}] 请查收您的验证码";

            //获取模板
            string htmlTemplate = EmailCodeTemplate;
            if (string.IsNullOrEmpty(htmlTemplate))
            {
                // 尝试临时加载或者报错
                htmlTemplate = await GetTemplateContent();
            }

            //替换占位符
            string emailBody = htmlTemplate
                .Replace("{SystemName}", SystemName)
                .Replace("{UserName}", userName)
                .Replace("{Code}", code)
                .Replace("{Year}", DateTime.Now.Year.ToString());

            //构建Body
            var bodyBuilder = new BodyBuilder();
            bodyBuilder.HtmlBody = emailBody;
            bodyBuilder.TextBody = $"您好 {userName}，您的验证码是：{code}，请在10分钟内完成验证。"; // 纯文本

            message.Body = bodyBuilder.ToMessageBody();

            return await SeedEmail(message, toEmail);
        }

        /// <summary>
        /// 核心发送逻辑：每次创建新连接
        /// </summary>
        public async Task<bool> SeedEmail(MimeMessage mimeMessage, string toEmail)
        {
            // 使用 using 确保资源释放（Disconnect 和 Dispose）
            using (var client = new SmtpClient())
            {
                try
                {
                    // 1. 连接
                    await client.ConnectAsync(
                        _appConfigInfo.emailSetting.SmtpServer,
                        _appConfigInfo.emailSetting.SmtpPort,
                        useSsl: true
                    );

                    // 2. 认证
                    await client.AuthenticateAsync(
                        _appConfigInfo.emailSetting.Email,
                        _appConfigInfo.emailSetting.PassWord
                    );

                    // 3. 发送
                    await client.SendAsync(mimeMessage);

                    // 4. 断开连接
                    client.DisconnectAsync(true);

                    _logger.LogInformation($"发送邮件成功: {toEmail}");
                    return true;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"发送邮件失败: {toEmail}");
                    return false;
                }
            }
        }

        private async Task<string> GetTemplateContent()
        {
            var filePath = Path.Combine(_env.ContentRootPath, "EmailCodeTemplate.html");
            if (!File.Exists(filePath))
            {
                // 也可以创建一个默认的简单HTML字符串返回，防止报错
                throw new FileNotFoundException($"模板文件未找到: {filePath}");
            }
            return await File.ReadAllTextAsync(filePath);
        }
    }
}

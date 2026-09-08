using drive_api.Models;
using drive_api.Services.AI;
using drive_api.Services.Config;
using drive_api.Services.TrafficStatistics;
using drive_api.Services.UserManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Security.Claims;

namespace drive_api.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class UserController(Kernel kernel, ILogger<UserController> logger, AppConfigInfo appConfigInfo, UserService userHandler) : ControllerBase
    {
        private readonly ILogger<UserController> _logger = logger;
        private readonly UserService _userHandler = userHandler;
        private readonly AppConfigInfo _appConfigInfo = appConfigInfo;
        private readonly Kernel _kernel = kernel;

        [HttpGet]
        [EnableRateLimiting("EmailIpLimiter")]
        public async Task<IActionResult> SeedEmailCode(string Email)
        {
            var msg = await _userHandler.SeedEmailCode(Email);
            return Ok(msg);

        }
        [HttpPost]
        public async Task<IActionResult> RegisterUser([FromBody] Models.UserRegInfo userRegInfo)
        {
            var msg = await _userHandler.RegisterUserAsync(userRegInfo);
            return Ok(msg);
        }

        [HttpPost]
        public async Task<IActionResult> LoginUser([FromBody] LoginInfo loginInfo)
        {
            var msg = await _userHandler.LoginUserAsync(loginInfo.usernameOrEmail, loginInfo.password);
            return Ok(msg);

        }


        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetUserInfo()
        {
            var msg = await _userHandler.GetUserInfo(GetUserId());
            return Ok(msg);

        }

        /// <summary>
        /// 上传头像
        /// </summary>
        /// <param name="file"></param>
        /// <returns></returns>
        [HttpPost]
        [Authorize]

        [RequestFormLimits(MultipartBodyLengthLimit = 2000000)]
        [RequestSizeLimit(2000000)]
        [TrackTraffic]
        public async Task<IActionResult> UploadAvatar([FromForm] IFormFile file)
        {
            // 1. 基本验证
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { Message = "没有提供文件或文件为空。" });
            }
            await using (var requestStream = file.OpenReadStream())
            {
                DefaultMsg msg = await _userHandler.UploadAvatar(GetUserId(), requestStream);
                return Ok(msg);

            }

        }
        /// <summary>
        /// 更新用户信息
        /// </summary>
        /// <param name="userInfoEdit"></param>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdateUserInfo([FromBody] UserInfoEdit userInfoEdit)
        {
            DefaultMsg msg = await _userHandler.UpdateUserInfo(GetUserId(), userInfoEdit);
            return Ok(msg);
        }
        /// <summary>
        ///  更新密码
        /// </summary>
        /// <param name="userInfoEdit"></param>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdatePassword([FromBody] UserPasswordEdit userPasswordEdit)
        {
            DefaultMsg msg = await _userHandler.UpdatePassword(GetUserId(), userPasswordEdit);
            return Ok(msg);
        }
        /// <summary>
        /// 更新偏好
        /// </summary>
        /// <param name="preferences"></param>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdateUserPreferences([FromBody] UserPreferences preferences)
        {
            DefaultMsg msg = await _userHandler.UpdateUserPreferences(GetUserId(), preferences);
            return Ok(msg);
        }
        private string GetUserId()
        {
            // 获取用户拥有的所有角色
            IEnumerable<Claim> roleClaims = this.User.FindAll(ClaimTypes.Role);
            return this.User.FindFirst(ClaimTypes.NameIdentifier)!.Value;
        }

        /// <summary>
        /// 更新视图
        /// </summary>
        /// <param name="customView"></param>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdateUserView([FromBody] CustomView[] customView)
        {
            DefaultMsg msg = await _userHandler.UpdateUserView(GetUserId(), customView);
            return Ok(msg);
        }
        [HttpPost]
        [Authorize]
        public async Task ProcessCommandStream([FromBody] AiChatRequest request)
        {
            // 基础校验
            if (string.IsNullOrWhiteSpace(request.UserInput)) return;
            // 提示词
            string systemPrompt = $@"你是一个智能、高效的私人网盘助手。你的任务是帮助用户管理、检索和总结网盘内的文件。请遵循以下核心原则：
1. 【人设设定】你的名字叫“星云”，语气要友好、活泼且专业，经常使用适当的 Emoji。
2. 【由简到繁】（核心沟通策略）当用户提出宽泛问题或询问概况（如“这里面有什么”、“总结一下文件”）时，你必须先提供**简短、精炼的大致概览**，不要一上来就长篇大论。可以在结尾温柔地提示：“需要星云为您详细展开讲讲吗？”只有当用户明确要求“详细解释”或“再具体点”时，再提供深入的细节。
3. 【排版与图片展示】输出 Markdown 文本时请务必保持**格式紧凑**。段落之间最多保留1个空行，无序/有序列表项之间**绝对不要使用空行**。🚨【重要图片渲染规则】：当文件后缀是图片类型（如 jpg, png, gif 等）时，你**必须**直接使用 Markdown 图片语法将其在对话中展示出来！图片的完整网络链接拼接公式严格为：`{request.Context?.CurrentDomain ?? "未知后端地址"}/driveassets/imgcomp/该文件的FileHash字段.jpg`。输出格式必须为：`![文件名](拼接出的完整链接)`。
4. 【能力范围】你可以根据用户的要求调用相关的网盘工具（如搜索文件、重命名、获取存储信息等）。🚨特别注意：当用户要求你**管理、查看或修改“自定义视图/快捷菜单”**时，你必须先调用 `GetUserProfile` 工具获取用户当前的视图列表（在 Preferences 数据中），了解现状后再调用 `ManageCustomView` 执行增删改操作，或者直接回答用户目前有哪些视图。。
5. 【严谨求实】如果没搜到文件，请直接告诉用户没找到，绝对不能自己编造虚假的文件名或内容！
6. 【边界限制】如果用户问你与网盘、文件管理、文档内容无关的问题（例如问天气、写诗、聊政治），请委婉地拒绝，并引导他们回到网盘操作上来。

【当前上下文状态】
你能够感知用户当前在网盘界面的位置。
当前用户所处的文件夹路径：{request.Context?.CurrentPath ?? "/"}
当前文件夹的唯一 ID：{request.Context?.CurrentFolderId ?? "根目录"}
当前系统的前端域名：{request.Context?.CurrentDomain ?? "未知域名"}
用户当前使用的后端地址：{request.Context?.BackEnd ?? "未知后端地址"}
用户当前使用的客户端类型：{request.Context?.Client ?? "未知客户端"}
当前系统时间：{DateTime.Now}

【上下文操作规则】
如果用户的指令中包含“这里”、“当前目录”、“这个文件夹”等代词（例如：“帮我在这里建个名为工作的文件夹”、“列出当前目录的文件”），请你自动使用上述的【当前文件夹的唯一 ID】作为参数去调用对应的工具。注意：除非像删除等极其敏感的操作需要再次与用户确认，其余常规操作请直接静默执行，千万不要再反问用户“您想在哪个目录操作”。";
            // 构建聊天历史对象
            var chatHistory = new ChatHistory(systemPrompt);

            // 将前端传来的历史记录塞进去
            if (request.History != null)
            {
                foreach (var msg in request.History)
                {
                    if (msg.Role == "user")
                        chatHistory.AddUserMessage(msg.Content);
                    else if (msg.Role == "ai")
                        chatHistory.AddAssistantMessage(msg.Content);
                }
            }

            // 加上用户当前的新问题
            chatHistory.AddUserMessage(request.UserInput);

            // 获取聊天服务并执行流式调用
            var chatCompletionService = _kernel.GetRequiredService<IChatCompletionService>();
            var settings = new OpenAIPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
            _kernel.Data["userId"] = GetUserId();
            // 把 chatHistory 传给模型
            var responseStream = chatCompletionService.GetStreamingChatMessageContentsAsync(chatHistory, settings, _kernel);
            // 把用户 ID 传给SK，不传给LLM,以免泄露隐私，但SK的插件系统可以拿到这个参数，进行权限校验等操作

            Response.ContentType = "text/plain; charset=utf-8";
            // 不缓冲直接返回前端 防止nginx反代无法实现流输出
            Response.Headers.Append("X-Accel-Buffering", "no");

            await foreach (var chunk in responseStream)
            {
                // 写入当前的数据块
                await Response.WriteAsync(chunk.ToString());
                // 关键步骤：强制立即将缓冲区的数据推送到前端
                await Response.Body.FlushAsync();
            }
        }

    }
}

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace driveApi.Services.JWT
{
    public class JwtEventHandler(ILogger<JwtEventHandler> logger)
    {
        static JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            // 将 PropertyNamingPolicy 设为 null，表示不转换，保持属性原名
            PropertyNamingPolicy = null
        };
        private readonly ILogger<JwtEventHandler> _logger = logger;

        // 实例方法（签名匹配委托）
        public async Task OnAuthenticationFailed(AuthenticationFailedContext context)
        {

            await Task.CompletedTask;
        }

        public async Task OnChallenge(JwtBearerChallengeContext context)
        {


            // 禁止默认响应
            context.HandleResponse();

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json;charset=utf-8";

                // 这里可以判断之前的失败原因
                string msg = context.AuthenticateFailure?.Message ?? "未登录或 Token 无效";
                // 如果是过期，可以根据 context.AuthenticateFailure 类型判断

                var response = new { Status = 401, Data = msg };
                await context.Response.WriteAsJsonAsync(response, jsonOptions);
            }

        }
        public string BuildToken(IEnumerable<Claim> claims, JwtSetting _jwtSetting)
        {
            // 设置 Token 过期时间
            DateTime expires = DateTime.UtcNow.AddSeconds(_jwtSetting.ExpireSeconds);
            // 调试：打印 UTC 过期时间和本地时区过期时间（方便验证）
            //Console.WriteLine($"UTC 过期时间：{expires:yyyy-MM-dd HH:mm:ss}");
            //Console.WriteLine($"本地时区过期时间：{expires.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            // 根据配置的密钥生成安全密钥对象
            byte[] keyBytes = Encoding.UTF8.GetBytes(_jwtSetting.SigningKey);
            var secKey = new SymmetricSecurityKey(keyBytes);

            // 指定签名算法，这里用 HMAC-SHA256
            var credentials = new SigningCredentials(secKey, SecurityAlgorithms.HmacSha256);

            // 创建 Token 对象，包括过期时间、签名凭据、Claims
            var tokenDescriptor = new JwtSecurityToken(
                expires: expires,
                signingCredentials: credentials,
                claims: claims
            );

            // 序列化成最终 Token 字符串
            return new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);
        }


    }
}

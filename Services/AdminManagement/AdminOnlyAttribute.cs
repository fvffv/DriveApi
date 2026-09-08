using drive_api.Models;
using drive_api.Services.UserManagement;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Claims;

namespace drive_api.Services.AdminManagement
{
    // 这个特性类可以用来标记那些只有管理员才能访问AdminAPI
    public class AdminOnlyAttribute : ActionFilterAttribute
    {
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var user = context.HttpContext.User;
            var _userHandler = context.HttpContext.RequestServices.GetRequiredService<UserService>();
            // 这里调用 IsAdmin 方法来检查当前用户是否是管理员
            if (!await IsAdmin(user, _userHandler))
            {
                // 如果不是管理员，直接给 context.Result 赋值，这会阻断请求，不再往下执行目标路由
                context.Result = new JsonResult(new DefaultMsg(1, "无权限", null));

                return;
            }

            // 如果是管理员，放行，继续执行指定的路由
            await base.OnActionExecutionAsync(context, next);
        }

        private async Task<bool> IsAdmin(ClaimsPrincipal user, UserService _userHandler)
        {
            string uid = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var userInfo = await _userHandler.GetUserInfo(uid);
            if (userInfo.Status != 0)
            {
                return false;
            }
            if ((userInfo.Data as ShowUserInfo).Status != 2)
            {

                return false;
            }
            return true;
        }
    }
}

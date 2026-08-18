using drive_api.Models;
using drive_api.Services.AdminManagement;
using drive_api.Services.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using static drive_api.Services.AdminManagement.AdminHandler;

namespace drive_api.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    [AdminOnly]
    public class AdminController(ILogger<AdminController> logger, AdminHandler ah) : ControllerBase
    {
        private readonly ILogger<AdminController> _logger = logger;
        private readonly AdminHandler _ah = ah;
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetDataStatistics()
        {
            return Ok(await _ah.GetDataStatistics());
        }
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetSystemInfo()
        {
            return Ok(await _ah.GetSystemInfo());
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetUsers()
        {
            return Ok(await _ah.GetUsers());
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdateUserInfo([FromBody] UpdateUserModel userInfo)
        {
            return Ok(await _ah.UpdateUserInfo(GetUserId(), userInfo));
        }
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> RegisterUser([FromBody] RegUserModel userInfo)
        {
            return Ok(await _ah.RegisterUser(GetUserId(), userInfo));
        }
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> GetGlobalFiles([FromBody] FilePageQueryReq req)
        {
            return Ok(await _ah.GetGlobalFilesAsync(req));
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> RemoveFiles([FromBody] FileRemoveInfoReq[] req)
        {
            return Ok(await _ah.RemoveFiles(GetUserId(), req));
        }
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetTempDownLoadKey(string userId, string fileId)
        {

            return Ok(await _ah.GetTempDownLoadKey(GetUserId(), userId, fileId));
        }
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetConfigs()
        {

            return Ok(await _ah.GetConfigs());
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdateSystemConfig([FromBody] SystemConfigUpdateDto req)
        {
            return Ok(await _ah.UpdateSystemConfig(GetUserId(), req));
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Restart()
        {

            return Ok(await _ah.Restart(GetUserId()));
        }
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetLogs(int page)
        {

            return Ok(await _ah.GetLogs(page));
        }
        private string GetUserId()
        {
            // 获取用户拥有的所有角色
            IEnumerable<Claim> roleClaims = this.User.FindAll(ClaimTypes.Role);
            return this.User.FindFirst(ClaimTypes.NameIdentifier)!.Value;
        }
    }




}

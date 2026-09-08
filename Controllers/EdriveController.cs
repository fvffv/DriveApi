using drive_api.Models;
using drive_api.Services.ExternalDrive;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace drive_api.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class EdriveController(ExternalDriveService externalDriveService) : ControllerBase
    {
        private readonly ExternalDriveService _externalDriveService = externalDriveService;

        private Guid GetUserId()
        {
            IEnumerable<Claim> roleClaims = User.FindAll(ClaimTypes.Role);
            return Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        }

        /// <summary>
        /// 获取用户目录下的文件和文件夹信息。
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetUserDirectoryFileInfo(CloudDriveType driveType, string folderIdOrPath, string accessToken, int start = 0, int limit = 1000)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveDirectoryResult> msg = await provider.GetUserDirectoryFileInfo(folderIdOrPath, accessToken, start, limit);
            return Ok(msg);
        }

        /// <summary>
        /// 获取指定文件的详细信息。
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetFileInfo(CloudDriveType driveType, string fileIdOrPath, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveFileInfoResult> msg = await provider.GetFileInfo(fileIdOrPath, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 获取外部网盘存储容量和已使用空间。
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetUserStorageCapacityInfo(CloudDriveType driveType, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<StorageCapacityInfo> msg = await provider.GetUserStorageCapacityInfo(accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 上传普通文件。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdataFileAsync([FromForm] CloudDriveType driveType, [FromForm] IFormFile file, [FromForm] string folderIdOrPath, [FromForm] string accessToken)
        {
            if (file == null || file.Length == 0)
            {
                return Ok(new DefaultMsg<ExternalDriveUploadResult>(1, "没有提供文件或文件为空。", default!));
            }

            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);

            await using Stream fileStream = file.OpenReadStream();
            DefaultMsg<ExternalDriveUploadResult> msg = await provider.UpdataFileAsync(fileStream, file.FileName, folderIdOrPath, accessToken);

            return Ok(msg);
        }

        /// <summary>
        /// 创建分片上传任务。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateMultipartUpload(CloudDriveType driveType, string fileName, long fileSize, string? fileHash, string folderIdOrPath, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveUploadSessionResult> msg = await provider.CreateMultipartUpload(fileName, fileSize, fileHash, folderIdOrPath, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 获取分片上传任务信息。
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetFileChunkInfo(CloudDriveType driveType, string uploadId, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveUploadSessionResult> msg = await provider.GetFileChunkInfo(uploadId, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 上传文件分片。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UploadChunkAsync([FromForm] CloudDriveType driveType, [FromForm] string uploadId, [FromForm] int chunkIndex, [FromForm] IFormFile file, [FromForm] string accessToken)
        {
            if (file == null || file.Length == 0)
            {
                return Ok(new DefaultMsg<ExternalDriveChunkResult>(1, "没有提供分片文件或分片文件为空。", default!));
            }

            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);

            await using Stream chunkStream = file.OpenReadStream();
            DefaultMsg<ExternalDriveChunkResult> msg = await provider.UploadChunkAsync(uploadId, chunkIndex, chunkStream, accessToken);

            return Ok(msg);
        }

        /// <summary>
        /// 合并已上传的文件分片。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> MergeFilesAsync(CloudDriveType driveType, string uploadId, string folderIdOrPath, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveUploadResult> msg = await provider.MergeFilesAsync(uploadId, folderIdOrPath, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 删除指定文件。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> DeleteFileAsync(CloudDriveType driveType, string fileIdOrPath, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveOperationResult> msg = await provider.DeleteFileAsync(fileIdOrPath, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 下载指定文件。
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> DownLoadFile(CloudDriveType driveType, string fileIdOrPath, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveDownloadResult> msg = await provider.DownLoadFile(fileIdOrPath, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 批量移动文件或文件夹。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> MoveFileOrDir([FromBody] ExternalDriveMoveRequest request)
        {
            if (request.FileIdOrPaths.Length == 0)
            {
                return Ok(new DefaultMsg<ExternalDriveOperationResult>(1, "至少需要选择一个待移动项目。", default!));
            }

            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(request.DriveType);
            DefaultMsg<ExternalDriveOperationResult> msg = await provider.MoveFileOrDir(request.FileIdOrPaths, request.TargetFolderIdOrPath, request.AccessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 重命名文件或文件夹。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> RenameFileOrDir(CloudDriveType driveType, string fileIdOrPath, string newName, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveOperationResult> msg = await provider.RenameFileOrDir(fileIdOrPath, newName, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 创建文件夹。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateFolder(CloudDriveType driveType, string parentIdOrPath, string folderName, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveOperationResult> msg = await provider.CreateFolder(parentIdOrPath, folderName, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 删除指定文件夹。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> DeleteUserFolderAsync(CloudDriveType driveType, string folderIdOrPath, string accessToken)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveOperationResult> msg = await provider.DeleteUserFolderAsync(folderIdOrPath, accessToken);
            return Ok(msg);
        }

        /// <summary>
        /// 创建文件分享链接。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateShareKey(CloudDriveType driveType, string fileIdOrPath, string accessToken, string? appId, string? password = null, DateTimeOffset? expireTime = null)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveShareResult> msg = await provider.CreateShareKey(fileIdOrPath, accessToken, appId, password, expireTime);
            return Ok(msg);
        }

        /// <summary>
        /// 搜索文件。
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> SearchFiles(CloudDriveType driveType, string keyword, string accessToken, string? folderIdOrPath = null, int start = 0, int limit = 1000)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveSearchResult> msg = await provider.SearchFiles(keyword, accessToken, folderIdOrPath, start, limit);
            return Ok(msg);
        }

        /// <summary>
        /// 获取添加指定类型外部网盘时需要填写的字段定义。
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetAddExternalDriveParams(CloudDriveType driveType)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<DriveParams[]> msg = await provider.GetAddExternalDriveParams();
            return Ok(msg);
        }

        /// <summary>
        /// 添加外部网盘账户。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> AddExternalDrive(CloudDriveType driveType, string? displayName, string credentialData)
        {
            DefaultMsg<ExternalDriveAccountResult> msg = await _externalDriveService.AddExternalDrive(GetUserId(), driveType, displayName, credentialData);
            return Ok(msg);
        }

        /// <summary>
        /// 删除外部网盘账户。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> DeleteExternalDrive(Guid externalId)
        {
            DefaultMsg<ExternalDriveOperationResult> msg = await _externalDriveService.DeleteExternalDrive(GetUserId(), externalId);
            return Ok(msg);
        }

        /// <summary>
        /// 获取当前用户已添加的外部网盘列表。
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetExternalDriveList()
        {
            DefaultMsg<ExternalDriveAccountResult[]> msg = await _externalDriveService.GetExternalDriveList(GetUserId());
            return Ok(msg);
        }

        /// <summary>
        /// 获取网盘登录二维码/网址，或检查用户是否已完成登录。
        /// 应用凭据只用于本次设备码授权；成功后的 accessToken 返回给前端保管。
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> GetTokenDrive(CloudDriveType driveType, string credentialData, DriveTokenAction action, string? authorizationSessionId = null)
        {
            ICloudDriveProvider provider = await _externalDriveService.GetDriveProvider(driveType);
            DefaultMsg<ExternalDriveAuthorizationResult> msg = await provider.GetTokenDrive(credentialData, action, authorizationSessionId);
            return Ok(msg);
        }
    }
}

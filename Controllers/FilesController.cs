using drive_api.Models;
using drive_api.Services.Config;
using drive_api.Services.FileManagement;
using drive_api.Services.TrafficStatistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using System.Security.Claims;

namespace drive_api.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class FilesController(ILogger<FilesController> logger, FileService fh, AppConfigInfo appConfigInfo) : ControllerBase
    {
        private readonly ILogger<FilesController> _logger = logger;
        private readonly FileService _fh = fh;
        private readonly AppConfigInfo _appConfigInfo = appConfigInfo;
        private readonly ulong MaxFileSize = appConfigInfo.fileSetting.MaxFileSize;
        [HttpPost]
        [Authorize]
        //[RequestFormLimits(MultipartBodyLengthLimit = nameof(MaxFileSize))]
        //[RequestSizeLimit(nameof(MaxFileSize))]
        [TrackTraffic]
        public async Task<IActionResult> UploadFile(
        [FromForm] IFormFile file,
        [FromForm] string folderId = FileService.defaultGuid)
        {
            string userId = GetUserId();
            // 1. 基本验证
            if (file == null || file.Length == 0)
            {
                return Ok(new DefaultMsg(1, "没有提供文件或文件为空。", null));
            }

            try
            {
                _logger.LogInformation($"收到处理来自用户 '{userId}' 的文件上传，原始文件名: '{file.FileName}'");

                // 从 IFormFile 打开流
                //    await using 确保流会被正确关闭
                await using (var requestStream = file.OpenReadStream())
                {
                    DefaultMsg msg = await _fh.UpdataFileAsync(userId, requestStream, file.FileName, folderId);
                    return Ok(msg);

                }
            }
            catch (IOException ex) when (ex.Message.Contains("Request body too large"))
            {
                // 这个 catch 仍然是为了处理 IFormFile 内部可能因为超限而抛出的异常
                _logger.LogWarning($"文件上传失败，因为大小超过服务器限制。用户: {userId}, 文件名: {file.FileName}");
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { Message = "文件大小超过限制。" });
            }
            catch (Exception e)
            {
                // UpdataFileAsync 中向上抛出的任何异常都会在这里被捕获
                _logger.LogError(e, $"处理用户 '{userId}' 的文件上传时发生未知错误。");
                return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "处理文件时发生内部错误。" });
            }
        }

        /// <summary>
        /// 创建分片上传任务
        /// </summary>
        /// <param name="req"></param>
        /// <returns>返回分片任务id</returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateMultipartUpload([FromBody]MultipartUploadInitRequest req)
        {
         
            DefaultMsg msg = await _fh.CreateMultipartUpload(GetUserId(), req);
            return Ok(msg);
        }
        /// <summary>
        /// 获取文件分片信息
        /// </summary>
        /// <param name="req"></param>
        /// <returns>返回分片任务id</returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetFileChunkInfo(string uploadId)
        {

            DefaultMsg msg = await _fh.GetFileChunkInfo(GetUserId(), uploadId);
            return Ok(msg);
        }
        /// <summary>
        /// 上传分片文件
        /// </summary>
        [HttpPost]
        [Authorize]
        [TrackTraffic]
        public async Task<IActionResult> UploadChunk([FromForm] string uploadId, [FromForm] int chunkIndex, [FromForm] IFormFile file)
        {
            string userId = GetUserId();
            // 1. 基本验证
            if (file == null || file.Length == 0)
            {
                return Ok(new DefaultMsg(1, "没有提供文件或文件为空。", null));
            }

            try
            {
                _logger.LogInformation($"收到处理来自用户 '{userId}' 的文件上传，原始文件名: '{file.FileName}'");

                // 从 IFormFile 打开流
                //    await using 确保流会被正确关闭
                await using (var requestStream = file.OpenReadStream())
                {
                    DefaultMsg msg = await _fh.UploadChunkAsync(GetUserId(), uploadId, chunkIndex, requestStream);
                    return Ok(msg);

                }
            }
            catch (IOException ex) when (ex.Message.Contains("Request body too large"))
            {
                // 这个 catch 仍然是为了处理 IFormFile 内部可能因为超限而抛出的异常
                _logger.LogWarning($"文件上传失败，因为大小超过服务器限制。用户: {userId}, 文件名: {file.FileName}");
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { Message = "文件大小超过限制。" });
            }
            catch (Exception e)
            {
                // UpdataFileAsync 中向上抛出的任何异常都会在这里被捕获
                _logger.LogError(e, $"处理用户 '{userId}' 的文件上传时发生未知错误。");
                return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "处理文件时发生内部错误。" });
            }

            
        
        }

        /// <summary>
        /// 请求合并分片文件
        /// </summary>
        /// <param name="uploadId"></param>
        /// <param name="folderId"></param>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> MergeFiles( string uploadId, string folderId)
        {
            DefaultMsg msg = await _fh.MergeFilesAsync(GetUserId(), uploadId, folderId);
            return Ok(msg);
        }
        /// <summary>
        /// 转存文件或通过hash快传
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> SaveToFile(string folderId, string? fileId = "", string? hash256 = "")
        {
            DefaultMsg msg = await _fh.SaveToAsync(GetUserId(), folderId, fileId, hash256);
            return Ok(msg);


        }
        /// <summary>
        /// 获取用户存储容量信息
        /// </summary>
        /// <param name="uid"></param>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetUserStorageCapacityInfo()
        {

            DefaultMsg msg = await _fh.GetUserStorageCapacityInfo(GetUserId());
            return Ok(msg);

        }
        /// <summary>
        /// 查看单个文件信息
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="fileId"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetUserFileInfo(string fileId)
        {
            DefaultMsg msg = await _fh.GetFileInfo(GetUserId(), fileId);
            return Ok(msg);

        }

        /// <summary>
        /// 删除用户文件
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> DeleteUserFile([FromBody] string[] fileIds)
        {

            DefaultMsg msg = await _fh.DeleteFileAsync(GetUserId(), fileIds);
            return Ok(msg);

        }
        /// <summary>
        /// 删除用户文件夹
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> DeleteUserFolder([FromBody] string[] folderIds)
        {

            DefaultMsg msg = await _fh.DeleteUserFolderAsync(GetUserId(), folderIds);
            return Ok(msg);

        }


        /// <summary>
        /// 获取用户文件列表
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="fileId"></param>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetUserDirectoryFileInfo(string folderId, int pageIndex, int pageSize)
        {
            DefaultMsg msg = await _fh.GetUserDirectoryFileInfo(GetUserId(), folderId, pageIndex, pageSize);
            return Ok(msg);


        }

        /// <summary>
        ///移动文件或文件夹
        /// </summary>
        /// <param name="fileOrDirMoveInfo"></param>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> MoveFileOrDir([FromBody] FileOrDirMoveInfo fileOrDirMoveInfo)
        {

            DefaultMsg msg = await _fh.MoveFileOrDir(GetUserId(), fileOrDirMoveInfo);
            return Ok(msg);


        }
        /// <summary>
        /// 改名
        /// </summary>
        /// <param name="fileOrDirReNameInfo"></param>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> RenameFileOrDir([FromBody] FileOrDirReNameInfo fileOrDirReNameInfo)
        {

            DefaultMsg msg = await _fh.RenameFileOrDir(GetUserId(), fileOrDirReNameInfo);
            return Ok(msg);


        }
        [HttpGet("{_fileId}")]
        [TrackTraffic]
        public async Task<IActionResult> DirectLink(string _fileId)
        {
            var provider = new FileExtensionContentTypeProvider();
            FileStreamInfo fileStreamInfo = await _fh.DirectLinkDownLoadFile(_fileId);

            if (fileStreamInfo == null)
            {
                return Ok(new DefaultMsg(1, "用户未开放直连下载或文件不存在", null));
            }
            string contentType;

            // 尝试获取 MIME 类型
            if (!provider.TryGetContentType(fileStreamInfo.Name, out contentType))
            {
                // 如果找不到，给一个默认值
                contentType = "application/octet-stream";
            }
            return File(fileStreamInfo.FileStream, contentType, fileStreamInfo.Name, enableRangeProcessing: true);
        }
        /// <summary>
        /// 得到云盘信息
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> GetCloudInfo()
        {
            DefaultMsg msg = await _fh.GetCloudInfo();
            return Ok(msg);
        }

        /// <summary>
        /// 新建文件夹
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> CreateFolder(string folderId, string name)
        {
            DefaultMsg msg = await _fh.CreateFolder(GetUserId(), folderId, name);
            return Ok(msg);
        }

        /// <summary>
        /// 获取用户根目录ID
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetFolderRoot()
        {
            DefaultMsg msg = await _fh.GetFolderRoot(GetUserId());
            return Ok(msg);
        }

        /// <summary>
        /// 根据路径获取文件夹id
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetFolderByPathStrict(string rootPathId, string fullPath)
        {
            DefaultMsg msg = await _fh.GetFolderByPathStrict(GetUserId(), rootPathId, fullPath);
            return Ok(msg);
        }
        /// <summary>
        /// 下载自己的文件获取临时下载密钥
        /// </summary>
        /// <param name="fileId"></param>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetFileDownLoadTempKey(string fileId)
        {
            DefaultMsg msg = await _fh.GetTempDownLoadKey(GetUserId(), fileId);
            return Ok(msg);
        }
        /// <summary>
        /// 获取临时下载密钥 文件分享
        /// </summary>
        /// <param name="fileId"></param>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> GetShareTempDownLoadKey(string shareKey, string? pwd)
        {
            DefaultMsg msg = await _fh.GetShareTempDownLoadKey(shareKey, pwd);
            return Ok(msg);
        }
        /// <summary>
        /// 创建分享链接
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateShareKey([FromBody] FileShareData fileShareData)
        {
            DefaultMsg msg = await _fh.CreateShareKey(GetUserId(), fileShareData);
            return Ok(msg);
        }
        /// <summary>
        /// 更新分享链接
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> UpdateShareFileInfo([FromQuery] string shareId, [FromBody] FileShareData? fileShareData, [FromQuery] bool isDel)
        {
            DefaultMsg msg = await _fh.UpdateShareFileInfo(GetUserId(), shareId, fileShareData, isDel);
            return Ok(msg);
        }
        /// <summary>
        /// 获取分享链接信息
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> GetShareInfo(string shareKey)
        {
            DefaultMsg msg = await _fh.GetShareInfo(shareKey);
            return Ok(msg);
        }
        //下载文件使用临时密钥，过期后需要重新获取
        [HttpGet("{_key}")]
        [TrackTraffic]
        public async Task<IActionResult> DownLoadKey(string _key)
        {
            var provider = new FileExtensionContentTypeProvider();
            FileStreamInfo fileStreamInfo = await _fh.DownloadFileWithKey(_key);

            if (fileStreamInfo == null)
            {
                return Ok(new DefaultMsg(1, "密钥过期或文件不存在", null));
            }
            string contentType;

            // 尝试获取 MIME 类型
            if (!provider.TryGetContentType(fileStreamInfo.Name, out contentType))
            {
                // 如果找不到，给一个默认值
                contentType = "application/octet-stream";
            }
            return File(fileStreamInfo.FileStream, contentType, fileStreamInfo.Name, enableRangeProcessing: true);
        }
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> SearchFiles([FromBody] SearchInfo searchInfo, bool isAI = true)
        {
            DefaultMsg msg = await _fh.SearchFiles(GetUserId(), searchInfo, isAI);
            return Ok(msg);
        }
        private string GetUserId()
        {
            // 获取用户拥有的所有角色
            IEnumerable<Claim> roleClaims = this.User.FindAll(ClaimTypes.Role);
            return this.User.FindFirst(ClaimTypes.NameIdentifier)!.Value;
        }
        /// <summary>
        /// 得到自己的分享文件列表
        /// </summary>
        /// <param name="customView"></param>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetShareFilesInfoPrivate()
        {
            DefaultMsg msg = await _fh.GetShareFilesInfoPrivate(GetUserId());
            return Ok(msg);
        }
        /// <summary>
        /// 获取自己的统计信息
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetDataStatistics()
        {
            DefaultMsg msg = await _fh.GetDataStatistics(GetUserId());
            return Ok(msg);
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetFullFolderPath(string folderId)
        {
            DefaultMsg msg = await _fh.GetFullFolderPathAsync(GetUserId(), folderId);
            return Ok(msg);
        }
    }
}

using drive_api.Models;

namespace drive_api.Services.ExternalDrive
{
    /// <summary>
    /// 网盘操作提供者接口。
    /// 本地网盘、百度网盘、阿里云盘等具体网盘实现此接口。
    /// </summary>
    public interface ICloudDriveProvider
    {
        /// <summary>
        /// 当前实现对应的网盘类型。
        /// </summary>
        CloudDriveType DriveType { get; }

        /// <summary>
        /// 获取添加当前类型网盘账户时需要提交的字段定义。
        /// 用于前端根据字段名称、输入类型、是否必填和是否保密动态生成表单。
        /// </summary>
        Task<DefaultMsg<DriveParams[]>> GetAddExternalDriveParams();

        /// <summary>
        /// 获取用户目录下的文件和文件夹信息。
        /// </summary>
        Task<DefaultMsg<ExternalDriveDirectoryResult>> GetUserDirectoryFileInfo(string folderIdOrPath, string accessToken, int start = 0, int limit = 1000);

        /// <summary>
        /// 获取指定文件的详细信息。
        /// </summary>
        Task<DefaultMsg<ExternalDriveFileInfoResult>> GetFileInfo(string fileIdOrPath, string accessToken);

        /// <summary>
        /// 获取用户的存储容量和已使用空间。
        /// </summary>
        Task<DefaultMsg<StorageCapacityInfo>> GetUserStorageCapacityInfo(string accessToken);

        /// <summary>
        /// 上传普通文件。
        /// </summary>
        Task<DefaultMsg<ExternalDriveUploadResult>> UpdataFileAsync(Stream fileStream, string fileName, string folderIdOrPath, string accessToken);

        /// <summary>
        /// 创建分片上传任务。
        /// </summary>
        Task<DefaultMsg<ExternalDriveUploadSessionResult>> CreateMultipartUpload(string fileName, long fileSize, string? fileHash, string folderIdOrPath, string accessToken);

        /// <summary>
        /// 获取分片上传任务的信息。
        /// </summary>
        Task<DefaultMsg<ExternalDriveUploadSessionResult>> GetFileChunkInfo(string uploadId, string accessToken);

        /// <summary>
        /// 上传文件分片。
        /// </summary>
        Task<DefaultMsg<ExternalDriveChunkResult>> UploadChunkAsync(string uploadId, int chunkIndex, Stream chunkStream, string accessToken);

        /// <summary>
        /// 合并已上传的文件分片。
        /// </summary>
        Task<DefaultMsg<ExternalDriveUploadResult>> MergeFilesAsync(string uploadId, string folderIdOrPath, string accessToken);

        /// <summary>
        /// 删除指定文件。
        /// </summary>
        Task<DefaultMsg<ExternalDriveOperationResult>> DeleteFileAsync(string fileIdOrPath, string accessToken);

        /// <summary>
        /// 下载指定文件。
        /// </summary>
        Task<DefaultMsg<ExternalDriveDownloadResult>> DownLoadFile(string fileIdOrPath, string accessToken);

        /// <summary>
        /// 批量移动文件或文件夹。
        /// </summary>
        Task<DefaultMsg<ExternalDriveOperationResult>> MoveFileOrDir(IReadOnlyCollection<string> fileIdOrPaths, string targetFolderIdOrPath, string accessToken);

        /// <summary>
        /// 重命名文件或文件夹。
        /// </summary>
        Task<DefaultMsg<ExternalDriveOperationResult>> RenameFileOrDir(string fileIdOrPath, string newName, string accessToken);

        /// <summary>
        /// 创建文件夹。
        /// </summary>
        Task<DefaultMsg<ExternalDriveOperationResult>> CreateFolder(string parentIdOrPath, string folderName, string accessToken);

        /// <summary>
        /// 删除指定文件夹。
        /// </summary>
        Task<DefaultMsg<ExternalDriveOperationResult>> DeleteUserFolderAsync(string folderIdOrPath, string accessToken);

        /// <summary>
        /// 创建文件分享链接。
        /// </summary>
        Task<DefaultMsg<ExternalDriveShareResult>> CreateShareKey(string fileIdOrPath, string accessToken, string? appId, string? password = null, DateTimeOffset? expireTime = null);

        /// <summary>
        /// 搜索文件。
        /// </summary>
        Task<DefaultMsg<ExternalDriveSearchResult>> SearchFiles(string keyword, string accessToken, string? folderIdOrPath = null, int start = 0, int limit = 1000);


        /// <summary>
        /// 获取网盘授权信息或检查授权结果。应用凭据由调用方传入，提供者不访问账户数据库。
        /// </summary>
        Task<DefaultMsg<ExternalDriveAuthorizationResult>> GetTokenDrive(string credentialData, DriveTokenAction action, string? authorizationSessionId = null);


    }
}

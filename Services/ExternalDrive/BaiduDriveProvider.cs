using drive_api.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace drive_api.Services.ExternalDrive;

/// <summary>
/// 百度网盘开放平台提供者。
/// 仅负责将前端传入的 accessToken 转发给百度开放接口，不访问业务数据库。
/// </summary>
public sealed class BaiduDriveProvider(ILogger<BaiduDriveProvider> logger) : ICloudDriveProvider
{
    private readonly ILogger<BaiduDriveProvider> _logger = logger;
    private const string OpenApiBase = "https://openapi.baidu.com";
    private const string PanApiBase = "https://pan.baidu.com";
    private const int DefaultPageSize = 1000;
    private const int SearchPageSizeLimit = 500;
    private const int UploadChunkSize = 4 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, UploadSession> UploadSessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DeviceAuthorizationSession> DeviceAuthorizationSessions = new(StringComparer.Ordinal);

    public CloudDriveType DriveType => CloudDriveType.Baidu;

    /// <summary>
    /// 获取添加百度网盘账户所需的表单字段定义。
    /// credentialData 应由前端将 credential_fields 中的值组装为 JSON 后提交。
    /// </summary>
    public Task<DefaultMsg<DriveParams[]>> GetAddExternalDriveParams()
    {
        DriveParams[] driveParams =
        [
            new DriveParams { Target = "app_id", Label = "AppId", Description = "必填，百度网盘开放平台应用的 AppId；与以下认证字段一起组装到 credentialData JSON。" },
            new DriveParams { Target = "app_key", Label = "AppKey", Description = "必填，百度网盘开放平台应用的 AppKey；与以下认证字段一起组装到 credentialData JSON。" },
            new DriveParams { Target = "secret_key", Label = "SecretKey", Description = "必填，百度网盘开放平台应用的 SecretKey；与以下认证字段一起组装到 credentialData JSON。" },
        ];

        return Task.FromResult(new DefaultMsg<DriveParams[]>(0, "获取添加百度网盘参数成功", driveParams));
    }

    /// <summary>
    /// 获取设备码登录信息，或检查用户是否已完成设备码登录。
    /// device_code 仅暂存于内存会话；授权成功时 access_token 返回给前端，Token 不写入数据库。
    /// </summary>
    public Task<DefaultMsg<ExternalDriveAuthorizationResult>> GetTokenDrive(string credentialData, DriveTokenAction action, string? authorizationSessionId = null)
        => action switch
        {
            DriveTokenAction.GetAuthorizationInfo => GetDeviceAuthorizationInfoAsync(credentialData),
            DriveTokenAction.CheckAuthorization => CheckDeviceAuthorizationAsync(credentialData, authorizationSessionId),
            _ => Task.FromResult(Fail<ExternalDriveAuthorizationResult>("不支持的网盘授权动作"))
        };

    private async Task<DefaultMsg<ExternalDriveAuthorizationResult>> GetDeviceAuthorizationInfoAsync(string credentialData)
    {
        try
        {
            BaiduDriveCredential credential = BaiduDriveCredential.Parse(credentialData);
            PurgeExpiredDeviceAuthorizationSessions();
            JsonElement data = await RequestJsonAsync(HttpMethod.Get, OpenApiBase + "/oauth/2.0/device/code", new Dictionary<string, string?>
            {
                ["response_type"] = "device_code",
                ["client_id"] = credential.AppKey,
                ["scope"] = "basic,netdisk"
            }, null);

            string? deviceCode = FindString(data, "device_code");
            string? userCode = FindString(data, "user_code");
            string? verificationUrl = FindString(data, "verification_url");
            string? qrcodeUrl = FindString(data, "qrcode_url");
            int expiresIn = FindInt(data, "expires_in") ?? 300;
            int interval = Math.Max(5, FindInt(data, "interval") ?? 5);
            if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(verificationUrl))
                return Fail<ExternalDriveAuthorizationResult>("百度未返回完整的设备码授权信息", data);

            string sessionId = Guid.NewGuid().ToString("N");
            DeviceAuthorizationSessions[sessionId] = new DeviceAuthorizationSession(deviceCode, DateTimeOffset.UtcNow.AddSeconds(expiresIn), interval);

            return Success("请使用百度网盘 App、百度 App 或微信扫码并确认授权", new ExternalDriveAuthorizationResult
            {
                Action = DriveTokenAction.CheckAuthorization, AuthorizationSessionId = sessionId, UserCode = userCode, VerificationUrl = verificationUrl,
                VerificationUrlComplete = AppendQuery(verificationUrl, "code", userCode), QrCodeUrl = qrcodeUrl,
                ExpiresIn = expiresIn, Interval = interval, Authorized = false
            });
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveAuthorizationResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveAuthorizationResult>("创建百度设备码授权失败：" + ex.Message); }
    }

    /// <summary>
    /// 检查设备码授权状态。成功时只将 access_token 返回给前端。
    /// </summary>
    private async Task<DefaultMsg<ExternalDriveAuthorizationResult>> CheckDeviceAuthorizationAsync(string credentialData, string? authorizationSessionId)
    {
        if (string.IsNullOrWhiteSpace(authorizationSessionId)) return Fail<ExternalDriveAuthorizationResult>("设备码授权会话ID不能为空");
        if (!DeviceAuthorizationSessions.TryGetValue(authorizationSessionId, out DeviceAuthorizationSession? session))
            return Fail<ExternalDriveAuthorizationResult>("设备码授权会话不存在或无权访问");
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            DeviceAuthorizationSessions.TryRemove(authorizationSessionId, out _);
            return Fail<ExternalDriveAuthorizationResult>("设备码已过期，请重新发起授权");
        }

        try
        {
            BaiduDriveCredential credential = BaiduDriveCredential.Parse(credentialData);
            JsonElement data = await RequestJsonAsync(HttpMethod.Get, OpenApiBase + "/oauth/2.0/token", new Dictionary<string, string?>
            {
                ["grant_type"] = "device_token",
                ["code"] = session.DeviceCode,
                ["client_id"] = credential.AppKey,
                ["client_secret"] = credential.SecretKey
            }, null);
            string? accessToken = FindString(data, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken)) return Fail<ExternalDriveAuthorizationResult>("百度未返回 access_token", data);

            DeviceAuthorizationSessions.TryRemove(authorizationSessionId, out _);
            return Success("百度设备码授权成功", new ExternalDriveAuthorizationResult { Action = DriveTokenAction.CheckAuthorization, AuthorizationSessionId = authorizationSessionId, AccessToken = accessToken, ExpiresIn = FindInt(data, "expires_in"), Authorized = true });
        }
        catch (BaiduApiException ex) when (IsAuthorizationPending(ex.Payload))
        {
            return new DefaultMsg<ExternalDriveAuthorizationResult>(2, "等待用户在百度确认授权", new ExternalDriveAuthorizationResult { Action = DriveTokenAction.CheckAuthorization, AuthorizationSessionId = authorizationSessionId, Interval = session.IntervalSeconds, Authorized = false });
        }
        catch (BaiduApiException ex)
        {
            DeviceAuthorizationSessions.TryRemove(authorizationSessionId, out _);
            return Fail<ExternalDriveAuthorizationResult>(ex.Message, ex.Payload);
        }
        catch (Exception ex) { return Fail<ExternalDriveAuthorizationResult>("查询百度设备码授权失败：" + ex.Message); }
    }

    public async Task<DefaultMsg<ExternalDriveDirectoryResult>> GetUserDirectoryFileInfo(string folderIdOrPath, string accessToken, int start = 0, int limit = DefaultPageSize)
    {
        try
        {
            string token = RequireAccessToken(accessToken);
            JsonElement data = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?>
            {
                ["method"] = "list", ["access_token"] = token, ["dir"] = NormalizePath(folderIdOrPath),
                ["order"] = "name", ["desc"] = "0", ["start"] = Math.Max(0, start).ToString(),
                ["limit"] = Math.Clamp(limit <= 0 ? DefaultPageSize : limit, 1, DefaultPageSize).ToString(), ["web"] = "1", ["folder"] = "0", ["showempty"] = "1"
            }, null);
            return Success("获取百度网盘目录成功", MapDirectory(data, NormalizePath(folderIdOrPath), Math.Max(0, start), Math.Clamp(limit <= 0 ? DefaultPageSize : limit, 1, DefaultPageSize)));
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveDirectoryResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveDirectoryResult>("获取百度网盘目录失败：" + ex.Message); }
    }

    public async Task<DefaultMsg<ExternalDriveFileInfoResult>> GetFileInfo(string fileIdOrPath, string accessToken)
    {
        try
        {
            string token = RequireAccessToken(accessToken);
            string fsId = await ResolveFsIdAsync(fileIdOrPath, token);
            JsonElement data = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/multimedia", new Dictionary<string, string?>
            { ["method"] = "filemetas", ["access_token"] = token, ["fsids"] = "[" + fsId + "]", ["dlink"] = "0" }, null);
            return Success("获取百度网盘文件信息成功", MapFile(data));
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveFileInfoResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveFileInfoResult>("获取百度网盘文件信息失败：" + ex.Message); }
    }

    public async Task<DefaultMsg<ExternalDriveDownloadResult>> DownLoadFile(string fileIdOrPath, string accessToken)
    {
        try
        {
            string token = RequireAccessToken(accessToken);
            string fsId = await ResolveFsIdAsync(fileIdOrPath, token);
            JsonElement data = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/multimedia", new Dictionary<string, string?>
            { ["method"] = "filemetas", ["access_token"] = token, ["fsids"] = "[" + fsId + "]", ["dlink"] = "1" }, null);
            string? dlink = FindListItemString(data, "dlink");
            if (string.IsNullOrWhiteSpace(dlink)) return Fail<ExternalDriveDownloadResult>("百度未返回文件下载地址", data);
            ExternalDriveFileInfoResult file = MapFile(data);
            return Success("获取百度网盘下载地址成功", MapDownload(data, fsId, file.Name, AppendQuery(dlink, "access_token", token)));
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveDownloadResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveDownloadResult>("获取百度网盘下载地址失败：" + ex.Message); }
    }

    public async Task<DefaultMsg<StorageCapacityInfo>> GetUserStorageCapacityInfo(string accessToken)
    {
        try
        {
            string token = RequireAccessToken(accessToken);
            JsonElement raw = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/api/quota", new Dictionary<string, string?>
            { ["access_token"] = token, ["checkfree"] = "1", ["checkexpire"] = "1" }, null);
            return Success("获取百度网盘容量成功", new StorageCapacityInfo { TotalSpaceInBytes = FindUInt64(raw, "quota", "total", "total_space"), UsedSpaceInBytes = FindUInt64(raw, "used", "used_space") });
        }
        catch (BaiduApiException ex) { return Fail<StorageCapacityInfo>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<StorageCapacityInfo>("获取百度网盘容量失败：" + ex.Message); }
    }

    public async Task<DefaultMsg<ExternalDriveSearchResult>> SearchFiles(string keyword, string accessToken, string? folderIdOrPath = null, int start = 0, int limit = DefaultPageSize)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyword)) return Fail<ExternalDriveSearchResult>("搜索关键字不能为空");
            string token = RequireAccessToken(accessToken); int pageSize = Math.Clamp(limit <= 0 ? DefaultPageSize : limit, 1, SearchPageSizeLimit);
            JsonElement data = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?>
            {
                ["method"] = "search", ["access_token"] = token, ["key"] = keyword.Trim().Length > 30 ? keyword.Trim()[..30] : keyword.Trim(),
                ["dir"] = string.IsNullOrWhiteSpace(folderIdOrPath) ? "/" : NormalizePath(folderIdOrPath), ["page"] = (Math.Max(0, start) / pageSize + 1).ToString(), ["num"] = pageSize.ToString(), ["recursion"] = "1", ["web"] = "1"
            }, null);
            return Success("搜索百度网盘文件成功", MapSearch(data, Math.Max(0, start) / pageSize + 1, pageSize));
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveSearchResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveSearchResult>("搜索百度网盘文件失败：" + ex.Message); }
    }

    public async Task<DefaultMsg<ExternalDriveOperationResult>> CreateFolder(string parentIdOrPath, string folderName, string accessToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderName)) return Fail<ExternalDriveOperationResult>("文件夹名称不能为空");
            string token = RequireAccessToken(accessToken);
            JsonElement data = await RequestJsonAsync(HttpMethod.Post, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?>
            { ["method"] = "create", ["access_token"] = token }, new FormUrlEncodedContent(new Dictionary<string, string>
            { ["path"] = CombinePath(parentIdOrPath, folderName), ["size"] = "0", ["isdir"] = "1", ["rtype"] = "1", ["block_list"] = "[]" }));
            return Success("创建百度网盘文件夹成功", MapOperation(data, "create", CombinePath(parentIdOrPath, folderName)));
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveOperationResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveOperationResult>("创建百度网盘文件夹失败：" + ex.Message); }
    }

    public Task<DefaultMsg<ExternalDriveOperationResult>> DeleteFileAsync(string fileIdOrPath, string accessToken) => FileManagerAsync(accessToken, "delete", fileIdOrPath, null, null, "删除百度网盘文件成功");
    public Task<DefaultMsg<ExternalDriveOperationResult>> DeleteUserFolderAsync(string folderIdOrPath, string accessToken) => FileManagerAsync(accessToken, "delete", folderIdOrPath, null, null, "删除百度网盘文件夹成功");
    public async Task<DefaultMsg<ExternalDriveOperationResult>> MoveFileOrDir(IReadOnlyCollection<string> fileIdOrPaths, string targetFolderIdOrPath, string accessToken)
    {
        string token = string.Empty;
        string destinationPath = string.Empty;
        var sourcePaths = new List<string>();
        bool moveRequestSent = false;
        try
        {
            token = RequireAccessToken(accessToken);
            string[] sourceValues = fileIdOrPaths.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
            if (sourceValues.Length == 0) return Fail<ExternalDriveOperationResult>("至少需要选择一个待移动项目");

            destinationPath = await ResolvePathAsync(targetFolderIdOrPath, token);
            var fileList = new List<object>(sourceValues.Length);
            _logger.LogDebug("百度网盘批量移动开始：项目数量 {ItemCount}", sourceValues.Length);
            foreach (string sourceValue in sourceValues)
            {
                string sourcePath = await ResolvePathAsync(sourceValue, token);
                string newName = sourcePath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(newName)) return Fail<ExternalDriveOperationResult>("待移动项目路径无效");

                sourcePaths.Add(sourcePath);
                fileList.Add(new { path = sourcePath, dest = destinationPath, newname = newName });
            }

            // /apps 是百度网盘的应用容器目录，不是用户可移动的普通目录。
            // filemanager 的批量请求只要包含该项，百度就会拒绝整个请求。
            if (IsBaiduSystemDirectory(destinationPath) || sourcePaths.Any(IsBaiduSystemDirectory))
            {
                _logger.LogWarning("百度网盘批量移动被拒绝：请求包含系统目录 /apps，项目数量 {ItemCount}", sourcePaths.Count);
                return Fail<ExternalDriveOperationResult>("百度网盘系统目录“/apps”不能移动，也不能作为移动目标。请取消选择该目录后重试。");
            }

            moveRequestSent = true;
            JsonElement data = await RequestJsonAsync(HttpMethod.Post, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?>
            {
                ["method"] = "filemanager", ["opera"] = "move", ["access_token"] = token
            }, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["async"] = "0", ["filelist"] = JsonSerializer.Serialize(fileList), ["ondup"] = "overwrite"
            }));

            _logger.LogDebug("百度网盘批量移动完成：项目数量 {ItemCount}", sourcePaths.Count);
            return Success($"批量移动百度网盘项目成功（{sourcePaths.Count} 项）", MapOperation(data, "move", destinationPath));
        }
        catch (BaiduApiException ex)
        {
            // 百度批量 filemanager 在部分批次会返回 errno=12，但实际已完成全部移动。
            // 仅在源路径均已消失、目标目录均出现对应项目时按成功处理，避免误报前端。
            if (moveRequestSent && ex.Errno == 12 && sourcePaths.Count > 0 && !string.IsNullOrWhiteSpace(destinationPath)
                && await VerifyMoveCompletedAsync(sourcePaths, destinationPath, token))
            {
                _logger.LogDebug("百度网盘批量移动返回 errno=12，但核验确认已完成：项目数量 {ItemCount}", sourcePaths.Count);
                return Success($"批量移动百度网盘项目成功（{sourcePaths.Count} 项）", new ExternalDriveOperationResult
                {
                    Operation = "move",
                    Path = destinationPath
                });
            }

            return Fail<ExternalDriveOperationResult>(ex.Message, ex.Payload);
        }
        catch (Exception ex) { return Fail<ExternalDriveOperationResult>("批量移动百度网盘项目失败：" + ex.Message); }
    }
    public Task<DefaultMsg<ExternalDriveOperationResult>> RenameFileOrDir(string fileIdOrPath, string newName, string accessToken) => FileManagerAsync(accessToken, "rename", fileIdOrPath, null, newName, "重命名百度网盘文件成功");

    public async Task<DefaultMsg<ExternalDriveShareResult>> CreateShareKey(string fileIdOrPath, string accessToken, string? appId, string? password = null, DateTimeOffset? expireTime = null)
    {
        try
        {
            string token = RequireAccessToken(accessToken);
            if (string.IsNullOrWhiteSpace(appId)) return Fail<ExternalDriveShareResult>("百度网盘 AppId 未提供");
            string fsId = await ResolveFsIdAsync(fileIdOrPath, token);
            int period = expireTime.HasValue ? Math.Max(1, (int)Math.Ceiling((expireTime.Value - DateTimeOffset.UtcNow).TotalDays)) : 7;
            var fields = new List<KeyValuePair<string, string>> { new("fsid_list", "[\"" + fsId + "\"]"), new("period", period.ToString()) };
            if (!string.IsNullOrWhiteSpace(password)) fields.Add(new("pwd", password.Trim()));
            using var form = new MultipartFormDataContent();
            foreach (var field in fields) form.Add(new StringContent(field.Value), field.Key);
            JsonElement data = await RequestJsonAsync(HttpMethod.Post, PanApiBase + "/apaas/1.0/share/set", new Dictionary<string, string?>
            { ["product"] = "netdisk", ["appid"] = appId.Trim(), ["access_token"] = token }, form);
            return Success("创建百度网盘分享链接成功", MapShare(data, expireTime));
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveShareResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveShareResult>("创建百度网盘分享链接失败：" + ex.Message); }
    }

    public async Task<DefaultMsg<ExternalDriveUploadResult>> UpdataFileAsync(Stream fileStream, string fileName, string folderIdOrPath, string accessToken)
    {
        if (fileStream is null || !fileStream.CanRead) return Fail<ExternalDriveUploadResult>("上传文件流无效");
        if (string.IsNullOrWhiteSpace(fileName)) return Fail<ExternalDriveUploadResult>("文件名不能为空");
        string? tempPath = null;
        try
        {
            tempPath = Path.Combine(Path.GetTempPath(), "baidu-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true)) await fileStream.CopyToAsync(output);
            await using var input = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
            string token = RequireAccessToken(accessToken);
            return await UploadLocalFileAsync(input, fileName, folderIdOrPath, token);
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveUploadResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveUploadResult>("上传百度网盘文件失败：" + ex.Message); }
        finally { if (tempPath is not null) TryDelete(tempPath); }
    }

    public Task<DefaultMsg<ExternalDriveUploadSessionResult>> CreateMultipartUpload(string fileName, long fileSize, string? fileHash, string folderIdOrPath, string accessToken)
    {
        if (fileSize < 0) return Task.FromResult(Fail<ExternalDriveUploadSessionResult>("文件大小不能为负数"));
        if (string.IsNullOrWhiteSpace(fileName)) return Task.FromResult(Fail<ExternalDriveUploadSessionResult>("文件名不能为空"));
        try
        {
            _ = RequireAccessToken(accessToken); string id = Guid.NewGuid().ToString("N");
            UploadSessions[id] = new UploadSession(id, fileName, NormalizePath(folderIdOrPath), fileSize, fileHash, DateTimeOffset.UtcNow);
            return Task.FromResult(Success("已创建百度网盘分片上传任务", MapUploadSession(id, fileName, fileSize, UploadChunkSize, "UPLOADING")));
        }
        catch (Exception ex) { return Task.FromResult(Fail<ExternalDriveUploadSessionResult>("创建百度网盘分片上传任务失败：" + ex.Message)); }
    }

    public Task<DefaultMsg<ExternalDriveUploadSessionResult>> GetFileChunkInfo(string uploadId, string accessToken)
    {
        try
        {
            _ = RequireAccessToken(accessToken);
            if (!UploadSessions.TryGetValue(uploadId, out UploadSession? session)) return Task.FromResult(Fail<ExternalDriveUploadSessionResult>("分片上传任务不存在或已过期"));
            var chunks = session.Chunks.OrderBy(x => x.Key).Select(x => new { index = x.Key, size = new System.IO.FileInfo(x.Value).Length, md5 = session.Hashes.GetValueOrDefault(x.Key) }).ToArray();
            return Task.FromResult(Success("获取百度网盘分片上传任务成功", MapUploadSession(uploadId, session.FileName, session.FileSize, UploadChunkSize, "UPLOADING", chunks.Select(x => new ExternalDriveChunkResult { UploadId = uploadId, ChunkIndex = x.index, Size = x.size, Md5 = x.md5 }))));
        }
        catch (Exception ex) { return Task.FromResult(Fail<ExternalDriveUploadSessionResult>("获取分片上传任务失败：" + ex.Message)); }
    }

    public async Task<DefaultMsg<ExternalDriveChunkResult>> UploadChunkAsync(string uploadId, int chunkIndex, Stream chunkStream, string accessToken)
    {
        if (chunkIndex < 0) return Fail<ExternalDriveChunkResult>("分片序号不能为负数");
        if (chunkStream is null || !chunkStream.CanRead) return Fail<ExternalDriveChunkResult>("分片文件流无效");
        try
        {
            _ = RequireAccessToken(accessToken);
            if (!UploadSessions.TryGetValue(uploadId, out UploadSession? session)) return Fail<ExternalDriveChunkResult>("分片上传任务不存在或已过期");
            string chunkPath = Path.Combine(Path.GetTempPath(), "baidu-chunk-" + uploadId + "-" + chunkIndex);
            await using (var output = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true)) await chunkStream.CopyToAsync(output);
            long length = new System.IO.FileInfo(chunkPath).Length;
            if (length == 0 || length > UploadChunkSize) { TryDelete(chunkPath); return Fail<ExternalDriveChunkResult>("分片大小无效，普通用户分片不能超过 4 MiB"); }
            string md5 = await ComputeMd5Async(chunkPath);
            lock (session.Sync)
            {
                if (session.Chunks.TryGetValue(chunkIndex, out string? old)) TryDelete(old);
                session.Chunks[chunkIndex] = chunkPath; session.Hashes[chunkIndex] = md5;
            }
            return Success("上传百度网盘文件分片成功", MapChunk(uploadId, chunkIndex, length, md5));
        }
        catch (Exception ex) { return Fail<ExternalDriveChunkResult>("上传百度网盘文件分片失败：" + ex.Message); }
    }

    public async Task<DefaultMsg<ExternalDriveUploadResult>> MergeFilesAsync(string uploadId, string folderIdOrPath, string accessToken)
    {
        if (!UploadSessions.TryGetValue(uploadId, out UploadSession? session)) return Fail<ExternalDriveUploadResult>("分片上传任务不存在或已过期");
        try
        {
            string token = RequireAccessToken(accessToken); string[] paths; string[] hashes;
            lock (session.Sync)
            {
                if (session.Chunks.Count == 0) return Fail<ExternalDriveUploadResult>("尚未上传任何文件分片");
                int[] indexes = session.Chunks.Keys.OrderBy(x => x).ToArray();
                if (indexes[0] != 0 || indexes.Select((value, index) => value == index).Any(ok => !ok)) return Fail<ExternalDriveUploadResult>("分片序号不连续");
                long total = session.Chunks.Values.Sum(x => new System.IO.FileInfo(x).Length); if (total != session.FileSize) return Fail<ExternalDriveUploadResult>($"分片总大小 {total} 与文件大小 {session.FileSize} 不一致");
                paths = indexes.Select(x => session.Chunks[x]).ToArray(); hashes = indexes.Select(x => session.Hashes[x]).ToArray();
            }
            return await UploadPreparedChunksAsync(paths, hashes, session.FileName, session.FileSize, session.FolderPath, token);
        }
        catch (BaiduApiException ex) { return Fail<ExternalDriveUploadResult>(ex.Message, ex.Payload); }
        catch (Exception ex) { return Fail<ExternalDriveUploadResult>("合并百度网盘文件分片失败：" + ex.Message); }
        finally { if (UploadSessions.TryRemove(uploadId, out UploadSession? removed)) foreach (string path in removed.Chunks.Values) TryDelete(path); }
    }

    private async Task<DefaultMsg<ExternalDriveUploadResult>> UploadLocalFileAsync(Stream input, string fileName, string folderPath, string accessToken)
    {
        var paths = new List<string>(); var hashes = new List<string>(); long size = 0;
        string tempDir = Path.Combine(Path.GetTempPath(), "baidu-upload-parts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tempDir);
        try
        {
            for (int index = 0;; index++)
            {
                string part = Path.Combine(tempDir, index.ToString("D8"));
                await using var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
                byte[] buffer = new byte[UploadChunkSize]; int read = 0;
                while (read < buffer.Length) { int n = await input.ReadAsync(buffer.AsMemory(read, buffer.Length - read)); if (n == 0) break; read += n; }
                if (read == 0) { TryDelete(part); break; }
                await output.WriteAsync(buffer.AsMemory(0, read)); await output.FlushAsync(); size += read; paths.Add(part); hashes.Add(await ComputeMd5Async(part));
            }
            return await UploadPreparedChunksAsync(paths, hashes, fileName, size, folderPath, accessToken);
        }
        finally { foreach (string path in paths) TryDelete(path); TryDeleteDirectory(tempDir); }
    }

    private async Task<DefaultMsg<ExternalDriveUploadResult>> UploadPreparedChunksAsync(IReadOnlyList<string> paths, IReadOnlyList<string> hashes, string fileName, long size, string folderPath, string accessToken)
    {
        if (paths.Count == 0) return Fail<ExternalDriveUploadResult>("不能上传空文件");
        string path = CombinePath(folderPath, fileName);
        JsonElement precreate = await RequestJsonAsync(HttpMethod.Post, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?> { ["method"] = "precreate", ["access_token"] = accessToken }, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["path"] = path, ["size"] = size.ToString(), ["isdir"] = "0", ["block_list"] = JsonSerializer.Serialize(hashes), ["autoinit"] = "1", ["rtype"] = "1" }));
        string? remoteUploadId = FindString(precreate, "uploadid"); if (string.IsNullOrWhiteSpace(remoteUploadId)) return Success("百度网盘文件已秒传或无需继续上传", MapUpload(precreate));
        JsonElement locate = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/pcs/file", new Dictionary<string, string?>
        { ["method"] = "locateupload", ["appid"] = "250528", ["access_token"] = accessToken, ["path"] = path, ["uploadid"] = remoteUploadId, ["upload_version"] = "2.0" }, null);
        string? server = FindFirstServer(locate); if (string.IsNullOrWhiteSpace(server)) return Fail<ExternalDriveUploadResult>("百度未返回上传域名", locate);
        foreach (int index in ParseBlockIndexes(precreate, paths.Count)) await UploadPartAsync(server, path, remoteUploadId, index, paths[index], accessToken);
        JsonElement created = await RequestJsonAsync(HttpMethod.Post, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?> { ["method"] = "create", ["access_token"] = accessToken }, new FormUrlEncodedContent(new Dictionary<string, string>
        { ["path"] = path, ["size"] = size.ToString(), ["isdir"] = "0", ["rtype"] = "1", ["uploadid"] = remoteUploadId, ["block_list"] = JsonSerializer.Serialize(hashes) }));
        return Success("上传百度网盘文件成功", MapUpload(created, remoteUploadId));
    }

    private async Task UploadPartAsync(string server, string path, string uploadId, int partIndex, string partPath, string accessToken)
    {
        string endpoint = server.TrimEnd('/') + "/rest/2.0/pcs/superfile2";
        await using var stream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        using var multipart = new MultipartFormDataContent(); var content = new StreamContent(stream); content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); multipart.Add(content, "file", Path.GetFileName(partPath));
        await RequestJsonAsync(HttpMethod.Post, endpoint, new Dictionary<string, string?> { ["method"] = "upload", ["type"] = "tmpfile", ["path"] = path, ["uploadid"] = uploadId, ["partseq"] = partIndex.ToString(), ["access_token"] = accessToken }, multipart);
    }

    private async Task<DefaultMsg<ExternalDriveOperationResult>> FileManagerAsync(string accessToken, string opera, string fileIdOrPath, string? dest, string? newName, string successMessage)
    {
        string token = string.Empty;
        string path = string.Empty;
        string renamePath = string.Empty;
        bool fileManagerRequestSent = false;
        try
        {
            token = RequireAccessToken(accessToken); path = await ResolvePathAsync(fileIdOrPath, token);
            string item;
            // 百度 filemanager 的 filelist 必须是对象数组；删除操作也需要每项携带 path 字段。
            if (opera == "delete") item = JsonSerializer.Serialize(new[] { new { path } });
            else if (opera == "move" || opera == "copy") item = JsonSerializer.Serialize(new[] { new { path, dest = await ResolvePathAsync(dest ?? "/", token) } });
            else if (opera == "rename")
            {
                if (string.IsNullOrWhiteSpace(newName)) return Fail<ExternalDriveOperationResult>("新名称不能为空");
                // 百度 filemanager/rename 要求 newname 是重命名后的完整路径，而不是单独的文件名。
                renamePath = CombinePath(GetParentPath(path), newName.Trim());
                item = JsonSerializer.Serialize(new[] { new { path, newname = renamePath } });
            }
            else item = JsonSerializer.Serialize(new[] { new { path, newname = newName } });
            fileManagerRequestSent = true;
            JsonElement data = await RequestJsonAsync(HttpMethod.Post, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?> { ["method"] = "filemanager", ["opera"] = opera, ["access_token"] = token }, new FormUrlEncodedContent(new Dictionary<string, string> { ["async"] = "0", ["filelist"] = item, ["ondup"] = "overwrite" }));
            return Success(successMessage, MapOperation(data, opera, path));
        }
        catch (BaiduApiException ex)
        {
            // 与批量移动一致：百度偶尔在重命名实际完成后仍返回 errno=12。
            if (fileManagerRequestSent && string.Equals(opera, "rename", StringComparison.OrdinalIgnoreCase)
                && ex.Errno == 12 && !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(renamePath)
                && await VerifyRenameCompletedAsync(path, renamePath, token))
            {
                _logger.LogDebug("百度网盘重命名返回 errno=12，但核验确认已完成：旧路径 {OldPath}，新路径 {NewPath}", path, renamePath);
                return Success(successMessage, new ExternalDriveOperationResult
                {
                    Operation = "rename",
                    Path = renamePath
                });
            }

            return Fail<ExternalDriveOperationResult>(ex.Message, ex.Payload);
        }
        catch (Exception ex) { return Fail<ExternalDriveOperationResult>(successMessage.Replace("成功", "失败") + "：" + ex.Message); }
    }

    private async Task<string> ResolveFsIdAsync(string fileIdOrPath, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(fileIdOrPath)) throw new ArgumentException("文件 ID 或路径不能为空"); string value = fileIdOrPath.Trim(); if (ulong.TryParse(value, out _)) return value;
        string path = NormalizePath(value); int slash = path.LastIndexOf('/'); string dir = slash <= 0 ? "/" : path[..slash];
        JsonElement list = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?> { ["method"] = "list", ["access_token"] = accessToken, ["dir"] = dir, ["start"] = "0", ["limit"] = DefaultPageSize.ToString(), ["web"] = "1" }, null);
        if (list.TryGetProperty("list", out JsonElement items) && items.ValueKind == JsonValueKind.Array) foreach (JsonElement item in items.EnumerateArray()) if (string.Equals(FindString(item, "path"), path, StringComparison.Ordinal)) { string? id = FindString(item, "fs_id"); if (!string.IsNullOrWhiteSpace(id)) return id; }
        throw new BaiduApiException("未找到指定百度网盘文件", list);
    }

    private async Task<string> ResolvePathAsync(string fileIdOrPath, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(fileIdOrPath)) throw new ArgumentException("文件 ID 或路径不能为空");
        string value = fileIdOrPath.Trim();
        if (!ulong.TryParse(value, out _)) return NormalizePath(value);
        JsonElement data = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/multimedia", new Dictionary<string, string?>
        { ["method"] = "filemetas", ["access_token"] = accessToken, ["fsids"] = "[" + value + "]", ["dlink"] = "0" }, null);
        string? path = FindListItemString(data, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new BaiduApiException("未找到指定百度网盘文件路径", data);
        return path;
    }

    private async Task<bool> VerifyMoveCompletedAsync(IReadOnlyCollection<string> sourcePaths, string destinationPath, string accessToken)
    {
        try
        {
            string destination = NormalizePath(destinationPath).TrimEnd('/');
            JsonElement destinationData = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?>
            {
                ["method"] = "list", ["access_token"] = accessToken, ["dir"] = destination.Length == 0 ? "/" : destination,
                ["start"] = "0", ["limit"] = DefaultPageSize.ToString(), ["web"] = "1", ["folder"] = "0", ["showempty"] = "1"
            }, null);

            HashSet<string> destinationItems = GetArray(destinationData, "list").EnumerateArray()
                .Select(item => FindString(item, "path"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => NormalizePath(path!))
                .ToHashSet(StringComparer.Ordinal);

            string[] expectedPaths = sourcePaths
                .Select(path => CombinePath(destination, path.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty))
                .ToArray();
            if (expectedPaths.Any(path => !destinationItems.Contains(path)))
            {
                _logger.LogDebug("百度网盘移动结果核验未找到全部目标项目：期望 {ExpectedCount}，目标目录中匹配 {MatchedCount}", expectedPaths.Length, expectedPaths.Count(destinationItems.Contains));
                return false;
            }

            // 按源父目录分组，减少检查请求；确认源位置不再保留原项目。
            foreach (IGrouping<string, string> group in sourcePaths.GroupBy(GetParentPath, StringComparer.Ordinal))
            {
                JsonElement sourceData = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?>
                {
                    ["method"] = "list", ["access_token"] = accessToken, ["dir"] = group.Key,
                    ["start"] = "0", ["limit"] = DefaultPageSize.ToString(), ["web"] = "1", ["folder"] = "0", ["showempty"] = "1"
                }, null);
                HashSet<string> sourceItems = GetArray(sourceData, "list").EnumerateArray()
                    .Select(item => FindString(item, "path"))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => NormalizePath(path!))
                    .ToHashSet(StringComparer.Ordinal);
                if (group.Any(sourceItems.Contains)) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "百度网盘移动结果核验失败");
            return false;
        }
    }

    private async Task<bool> VerifyRenameCompletedAsync(string oldPath, string newPath, string accessToken)
    {
        try
        {
            string parent = GetParentPath(newPath);
            string normalizedOldPath = NormalizePath(oldPath);
            string normalizedNewPath = NormalizePath(newPath);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                JsonElement data = await RequestJsonAsync(HttpMethod.Get, PanApiBase + "/rest/2.0/xpan/file", new Dictionary<string, string?>
                {
                    ["method"] = "list", ["access_token"] = accessToken, ["dir"] = parent,
                    ["start"] = "0", ["limit"] = DefaultPageSize.ToString(), ["web"] = "1", ["folder"] = "0", ["showempty"] = "1"
                }, null);
                HashSet<string> items = GetArray(data, "list").EnumerateArray()
                    .Select(item => FindString(item, "path"))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => NormalizePath(path!))
                    .ToHashSet(StringComparer.Ordinal);
                bool completed = !items.Contains(normalizedOldPath) && items.Contains(normalizedNewPath);
                if (completed) return true;
                if (attempt < 2) await Task.Delay(250);
                else _logger.LogDebug("百度网盘重命名结果核验失败：旧路径仍存在 {OldExists}，新路径存在 {NewExists}", items.Contains(normalizedOldPath), items.Contains(normalizedNewPath));
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "百度网盘重命名结果核验失败");
            return false;
        }
    }

    private static string GetParentPath(string path)
    {
        string normalized = NormalizePath(path).TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    private async Task<JsonElement> RequestJsonAsync(HttpMethod method, string endpoint, IReadOnlyDictionary<string, string?> query, HttpContent? content)
    {
        string baiduMethod = query.TryGetValue("method", out string? methodValue) ? methodValue ?? string.Empty : string.Empty;
        string opera = query.TryGetValue("opera", out string? operaValue) ? operaValue ?? string.Empty : string.Empty;
        _logger.LogDebug("百度接口请求开始：HTTP {HttpMethod}，Method {BaiduMethod}，Opera {Opera}，Endpoint {Endpoint}", method.Method, baiduMethod, opera, endpoint);

        try
        {
            using var request = new HttpRequestMessage(method, AddQuery(endpoint, query));
            request.Headers.UserAgent.ParseAdd("pan.baidu.com");
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = content;

            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            string body = await response.Content.ReadAsStringAsync();
            JsonElement data;
            try
            {
                data = JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "百度接口返回非 JSON：HTTP {StatusCode}，Method {BaiduMethod}，Opera {Opera}，响应长度 {ResponseLength}", (int)response.StatusCode, baiduMethod, opera, body.Length);
                throw new BaiduApiException($"百度接口返回非 JSON（HTTP {(int)response.StatusCode}）", default);
            }

            bool error = !response.IsSuccessStatusCode || (FindInt(data, "errno") is int errno && errno != 0) || (FindInt(data, "error_code") is int errorCode && errorCode != 0) || data.TryGetProperty("error", out _);
            if (error)
            {
                if (IsPotentiallyCompletedFileManagerOperation(data, baiduMethod, opera))
                    _logger.LogDebug("百度接口返回可核验的操作结果：HTTP {StatusCode}，Method {BaiduMethod}，Opera {Opera}，错误 {ErrorSummary}", (int)response.StatusCode, baiduMethod, opera, BuildBaiduErrorSummary(data));
                else
                    _logger.LogWarning("百度接口请求失败：HTTP {StatusCode}，Method {BaiduMethod}，Opera {Opera}，错误 {ErrorSummary}", (int)response.StatusCode, baiduMethod, opera, BuildBaiduErrorSummary(data));
                throw new BaiduApiException(BuildBaiduErrorMessage(data, response.StatusCode), data);
            }

            _logger.LogDebug("百度接口请求成功：HTTP {StatusCode}，Method {BaiduMethod}，Opera {Opera}", (int)response.StatusCode, baiduMethod, opera);
            return data;
        }
        catch (BaiduApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "百度接口请求异常：HTTP {HttpMethod}，Method {BaiduMethod}，Opera {Opera}，异常类型 {ExceptionType}", method.Method, baiduMethod, opera, ex.GetType().Name);
            throw;
        }
    }

    private static string BuildBaiduErrorMessage(JsonElement data, HttpStatusCode status) => FindString(data, "show_msg", "errmsg", "error_description", "error_msg") ?? $"百度接口错误（HTTP {(int)status}）";
    private static bool IsPotentiallyCompletedFileManagerOperation(JsonElement data, string baiduMethod, string opera)
        => string.Equals(baiduMethod, "filemanager", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(opera, "move", StringComparison.OrdinalIgnoreCase) || string.Equals(opera, "rename", StringComparison.OrdinalIgnoreCase))
            && (FindInt(data, "errno") == 12 || FindInt(data, "error_code") == 12);
    private static bool IsAuthorizationPending(JsonElement data)
    {
        string? error = FindString(data, "error");
        return string.Equals(error, "authorization_pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(error, "slow_down", StringComparison.OrdinalIgnoreCase);
    }

    private static void PurgeExpiredDeviceAuthorizationSessions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var session in DeviceAuthorizationSessions)
            if (session.Value.ExpiresAt <= now) DeviceAuthorizationSessions.TryRemove(session.Key, out _);
    }

    private static string RequireAccessToken(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("accessToken 不能为空");
        return accessToken.Trim();
    }
    private static string? FindFirstServer(JsonElement value) { if (value.TryGetProperty("servers", out JsonElement servers) && servers.ValueKind == JsonValueKind.Array) foreach (JsonElement item in servers.EnumerateArray()) { string? server = FindString(item, "server"); if (server?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true) return server; } return null; }
    private static IEnumerable<int> ParseBlockIndexes(JsonElement precreate, int count)
    {
        if (precreate.TryGetProperty("block_list", out JsonElement blocks)) { if (blocks.ValueKind == JsonValueKind.Array) return blocks.EnumerateArray().Select(x => x.TryGetInt32(out int i) ? i : -1).Where(i => i >= 0 && i < count).Distinct().OrderBy(i => i).ToArray(); if (blocks.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(blocks.GetString())) try { using JsonDocument d = JsonDocument.Parse(blocks.GetString()!); return d.RootElement.EnumerateArray().Select(x => x.GetInt32()).Where(i => i >= 0 && i < count).Distinct().OrderBy(i => i).ToArray(); } catch { } } return Enumerable.Range(0, count);
    }
    private static string NormalizePath(string? path) { string value = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim().Replace('\\', '/'); return value.StartsWith('/') ? value : "/" + value; }
    private static bool IsBaiduSystemDirectory(string path) => string.Equals(NormalizePath(path).TrimEnd('/'), "/apps", StringComparison.OrdinalIgnoreCase);
    private static string CombinePath(string? folder, string name) { string parent = NormalizePath(folder).TrimEnd('/'); string safeName = name.Trim().Trim('/'); return (parent.Length == 0 ? "/" : parent + "/") + safeName; }
    private static string AddQuery(string endpoint, IReadOnlyDictionary<string, string?> query) { var builder = new UriBuilder(endpoint); var values = new List<string>(); if (!string.IsNullOrWhiteSpace(builder.Query)) values.Add(builder.Query.TrimStart('?')); values.AddRange(query.Where(x => x.Value is not null).Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value!))); builder.Query = string.Join("&", values); return builder.Uri.ToString(); }
    private static string AppendQuery(string endpoint, string key, string value) => AddQuery(endpoint, new Dictionary<string, string?> { [key] = value });
    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
    private static string BuildBaiduErrorSummary(JsonElement data)
    {
        string[] fields = ["errno", "error_code", "error", "error_msg", "errmsg", "show_msg", "request_id"];
        return string.Join("，", fields
            .Select(field => (Field: field, Value: FindString(data, field)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{item.Field}={item.Value}"));
    }

    private static DefaultMsg<T> Success<T>(string message, T data) => new(0, message, data);
    private DefaultMsg<T> Fail<T>(string message, object? data = null)
    {
        string details = data is JsonElement element ? BuildBaiduErrorSummary(element) : string.Empty;
        _logger.LogWarning("百度网盘提供者操作失败：{Message}{ErrorDetails}", message, string.IsNullOrWhiteSpace(details) ? string.Empty : "，" + details);
        return new DefaultMsg<T>(1, message, default!);
    }
    private static string? FindString(JsonElement element, params string[] names) { if (element.ValueKind != JsonValueKind.Object) return null; foreach (string name in names) if (element.TryGetProperty(name, out JsonElement value)) return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(); foreach (JsonProperty property in element.EnumerateObject()) if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase))) return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString(); return null; }
    private static int? FindInt(JsonElement element, params string[] names) => int.TryParse(FindString(element, names), out int result) ? result : null;
    private static ulong FindUInt64(JsonElement element, params string[] names) => ulong.TryParse(FindString(element, names), out ulong result) ? result : 0;
    private static async Task<string> ComputeMd5Async(string path) { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true); return Convert.ToHexString(await MD5.HashDataAsync(stream)).ToLowerInvariant(); }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private static ExternalDriveDirectoryResult MapDirectory(JsonElement data, string path, int start, int limit)
    {
        var items = GetArray(data, "list").EnumerateArray().Select(item => MapItem(item, FindInt(item, "isdir") == 1)).ToArray();
        return new ExternalDriveDirectoryResult { Path = path, Items = items, Start = start, Limit = limit, HasMore = FindInt(data, "has_more") == 1 || items.Length >= limit };
    }

    private static ExternalDriveFileInfoResult MapFile(JsonElement data)
    {
        JsonElement item = GetArray(data, "list").EnumerateArray().FirstOrDefault();
        ExternalDriveItem mapped = MapItem(item, FindInt(item, "isdir") == 1);
        return new ExternalDriveFileInfoResult { Id = mapped.Id, Name = mapped.Name, Path = mapped.Path, IsDirectory = mapped.IsDirectory, Size = mapped.Size, CreatedAt = mapped.CreatedAt, ModifiedAt = mapped.ModifiedAt, Hash = mapped.Hash, DownloadUrl = FindString(item, "dlink"), MimeType = null };
    }

    private static ExternalDriveDownloadResult MapDownload(JsonElement data, string fileId, string fileName, string downloadUrl)
        => new() { FileId = fileId, FileName = fileName, DownloadUrl = downloadUrl, ExpiresIn = 8 * 60 * 60, SupportsRange = true };

    private static ExternalDriveSearchResult MapSearch(JsonElement data, int page, int limit)
    {
        var items = GetArray(data, "list").EnumerateArray().Select(item => MapItem(item, FindInt(item, "isdir") == 1)).ToArray();
        return new ExternalDriveSearchResult { Items = items, HasMore = FindInt(data, "has_more") == 1, Page = page, Limit = limit };
    }

    private static ExternalDriveOperationResult MapOperation(JsonElement data, string operation, string? path = null)
        => new() { Operation = operation, FileId = FindListItemString(data, "fs_id"), Path = path ?? FindListItemString(data, "path"), TaskId = FindLong(data, "taskid") };

    private static ExternalDriveShareResult MapShare(JsonElement data, DateTimeOffset? expiresAt)
    {
        JsonElement source = data.TryGetProperty("data", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object ? nested : data;
        return new ExternalDriveShareResult { ShareId = FindString(source, "share_id"), Url = FindString(source, "link", "url"), ShortUrl = FindString(source, "short_url"), Password = FindString(source, "pwd", "password"), ExpiresAt = expiresAt };
    }

    private static ExternalDriveUploadSessionResult MapUploadSession(string uploadId, string fileName, long fileSize, int chunkSize, string status, IEnumerable<ExternalDriveChunkResult>? chunks = null)
        => new() { UploadId = uploadId, FileName = fileName, FileSize = fileSize, ChunkSize = chunkSize, Status = status, UploadedChunks = chunks?.ToArray() ?? [] };

    private static ExternalDriveUploadResult MapUpload(JsonElement data, string? uploadId = null)
        => new() { File = data.ValueKind == JsonValueKind.Object && (data.TryGetProperty("fs_id", out _) || data.TryGetProperty("path", out _)) ? MapFileFromItem(data) : null, UploadId = uploadId, Status = "completed" };

    private static ExternalDriveChunkResult MapChunk(string uploadId, int index, long size, string md5)
        => new() { UploadId = uploadId, ChunkIndex = index, Size = size, Md5 = md5 };

    private static ExternalDriveItem MapItem(JsonElement item, bool isDirectory)
        => new() { Id = FindString(item, "fs_id", "id") ?? string.Empty, Name = FindString(item, "server_filename", "name") ?? string.Empty, Path = FindString(item, "path") ?? string.Empty, IsDirectory = isDirectory, Size = FindLong(item, "size") ?? 0, CreatedAt = UnixTime(FindLong(item, "server_ctime", "ctime")), ModifiedAt = UnixTime(FindLong(item, "server_mtime", "mtime")), Hash = FindString(item, "md5", "hash") };

    private static ExternalDriveFileInfoResult MapFileFromItem(JsonElement item)
    {
        ExternalDriveItem mapped = MapItem(item, FindInt(item, "isdir") == 1);
        return new ExternalDriveFileInfoResult { Id = mapped.Id, Name = mapped.Name, Path = mapped.Path, IsDirectory = mapped.IsDirectory, Size = mapped.Size, CreatedAt = mapped.CreatedAt, ModifiedAt = mapped.ModifiedAt, Hash = mapped.Hash };
    }

    private static JsonElement GetArray(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement result) && result.ValueKind == JsonValueKind.Array) return result;
        using JsonDocument empty = JsonDocument.Parse("[]");
        return empty.RootElement.Clone();
    }
    private static DateTimeOffset? UnixTime(long? seconds) => seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
    private static long? FindLong(JsonElement element, params string[] names) => long.TryParse(FindString(element, names), out long value) ? value : null;

    private sealed record DeviceAuthorizationSession(string DeviceCode, DateTimeOffset ExpiresAt, int IntervalSeconds);
    private sealed class UploadSession(string id, string fileName, string folderPath, long fileSize, string? fileHash, DateTimeOffset createdAt)
    {
        public string Id { get; } = id; public string FileName { get; } = fileName; public string FolderPath { get; } = folderPath; public long FileSize { get; } = fileSize; public string? FileHash { get; } = fileHash; public DateTimeOffset CreatedAt { get; } = createdAt; public object Sync { get; } = new(); public Dictionary<int, string> Chunks { get; } = new(); public Dictionary<int, string> Hashes { get; } = new();
    }
    private static string? FindListItemString(JsonElement element, string name)
    {
        string? direct = FindString(element, name); if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("list", out JsonElement list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0) return FindString(list[0], name);
        return null;
    }

    private sealed class BaiduApiException : Exception
    {
        public JsonElement Payload { get; }
        public int? Errno { get; }
        public BaiduApiException(string message, JsonElement data) : base(message)
        {
            Payload = data;
            Errno = FindInt(data, "errno", "error_code");
        }
    }
}

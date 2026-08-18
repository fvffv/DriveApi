using drive_api.Models;
using drive_api.Services.Cache;
using drive_api.Services.Config;
using drive_api.Services.FileManagement;
using drive_api.Services.UserManagement;
using SqlSugar;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;

namespace drive_api.Services.WebDav
{
    public class MemoryWebDavProvider(FileHandler fileHandler, AppConfigInfo appConfigInfo, ILogger<MemoryWebDavProvider> logger, ICache icache, ISqlSugarClient sqlSugarClient, UserHandler userHandler) : IWebDavProvider
    {
        private readonly ILogger<MemoryWebDavProvider> _logger = logger;
        private readonly ICache _icache = icache;
        private readonly ISqlSugarClient _sqlSugarClient = sqlSugarClient;
        private readonly UserHandler _userHandler = userHandler;
        private readonly AppConfigInfo _appConfigInfo = appConfigInfo;
        private readonly FileHandler _fileHandler = fileHandler;
        private string userpassword = "";



        // 1. 登录拦截事件
        public async Task<bool> AuthenticateAsync(string username, string password)
        {
            // 因为 Windows 会自动加上机器名前缀，所以在这里截取掉
            if (username.Contains("\\"))
                username = username.Substring(username.IndexOf('\\') + 1);

            if (await _icache.ExistsAsync("WebDavAuth", $"{username}:{password}"))
            {
                Guid.TryParse(await _icache.GetAsync<string>("WebDavAuth", $"{username}:{password}"), out Guid uid);
                //获取用户偏好设置
                UserPreferences userPreferences = null;
                try
                {
                    //使用缓存
                    userPreferences = JsonSerializer.Deserialize<UserPreferences>(await _icache.GetAsync<string>("UserPreferences", $"{uid}"));

                }
                catch (Exception)
                {

                    userPreferences = await _sqlSugarClient.Queryable<User>()
                    .Where(x => x.UserId == uid)
                    .Select(x => x.Preferences).FirstAsync();

                    _icache.SetAsync("UserPreferences", $"{uid}", JsonSerializer.Serialize(userPreferences), DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
                }

                userpassword = $"{username}:{password}";
                return userPreferences.IsWebDAVEnabled;
            }



            DefaultMsg msg = await _userHandler.LoginUserAsync(username, password);
            if (msg.Status == 0)
            {
                _logger.LogInformation($"用户{username} 登录WebDAV");
                userpassword = $"{username}:{password}";
                var result = msg.Data as LoginResult;
                var (userId, isExpired, remainingDays) = JwtHelper.ParseTokenInfo(result.token);
                Guid.TryParse(userId, out Guid uid);
                await _icache.SetAsync("WebDavAuth", $"{username}:{password}", $"{uid}", DateTimeOffset.Now.AddDays(remainingDays));

                return result.userPreferences.IsWebDAVEnabled;
            }
            else
            {
                _logger.LogInformation($"用户{username} {password}登录WebDAV失败");
                return false;
            }

        }

        // 2. 获取节点事件
        public async Task<WebDavNode?> GetNodeAsync(string path)
        {
            if (string.IsNullOrEmpty(path)) path = "/";

            if (await _icache.ExistsAsync("WebDavAuth", userpassword))
            {
                string uid = await _icache.GetAsync<string>("WebDavAuth", userpassword);

                // 1. 根目录永远存在，直接返回
                if (path == "/")
                {
                    return new WebDavNode { Name = "Root", IsFolder = true, Length = 0, Created = DateTime.Now, Modified = DateTime.Now };
                }

                // 2. 提取父目录路径和当前节点名
                string parentPath = GetParentPath(path);
                string nodeName = GetFileName(path);

                // 3. 获取根目录 ID
                string folderRootId = (await _fileHandler.GetFolderRoot(uid)).Data.ToString();
                string parentFolderId = folderRootId;

                // 4. 如果父目录不是根目录，获取父目录的真实 ID (内部有缓存，极快)
                if (parentPath != "/")
                {
                    DefaultMsg parentResult = await _fileHandler.GetFolderByPathStrict(uid, folderRootId, parentPath);
                    if (parentResult.Status != 0) return null; // 父目录都不存在，节点必不存在
                    parentFolderId = parentResult.Data.ToString();
                }

                // 5. 获取父目录下的所有内容 (这里完美命中 FileHandler.GetUserDirectoryFileInfo 的整包缓存，只需 1 次 Redis 查询)
                DefaultMsg dirResult = await _fileHandler.GetUserDirectoryFileInfoWebDAV(uid, parentFolderId);

                if (dirResult.Status == 0 && dirResult.Data is UserFilesInfo userFilesInfo)
                {
                    // 6. 在内存中极速查找：是文件夹吗？
                    var targetFolder = userFilesInfo.Dirs.FirstOrDefault(x => x.FolderName == nodeName);
                    if (targetFolder != null)
                    {
                        return new WebDavNode { Name = targetFolder.FolderName, IsFolder = true, Length = 0, Created = targetFolder.CreationTime, Modified = targetFolder.CreationTime };
                    }

                    // 7. 在内存中极速查找
                    var targetFile = userFilesInfo.FileInfos.FirstOrDefault(x => x.FileName == nodeName);
                    if (targetFile != null)
                    {
                        return new WebDavNode { Name = targetFile.FileName, IsFolder = false, Length = (long)targetFile.FileSizeInBytes, Created = targetFile.CreationTime, Modified = targetFile.LastModifiedTime };
                    }
                }
            }
            return null;
        }

        // 3. 获取子节点列表事件 (优化版：剔除多余的循环写缓存)
        public async Task<List<WebDavNode>> GetChildrenAsync(string folderPath)
        {
            if (await _icache.ExistsAsync("WebDavAuth", userpassword))
            {
                string uid = await _icache.GetAsync<string>("WebDavAuth", userpassword);

                string folderRootId = (await _fileHandler.GetFolderRoot(uid)).Data.ToString();
                string folderPathId = folderRootId;

                // 解析当前想要浏览的文件夹 ID
                if (folderPath != "/")
                {
                    DefaultMsg result2 = await _fileHandler.GetFolderByPathStrict(uid, folderRootId, folderPath);
                    if (result2.Status != 0) return null;
                    folderPathId = result2.Data.ToString();
                }

                // 获取文件夹内容 (底层自动走 Redis 缓存)
                DefaultMsg result = await _fileHandler.GetUserDirectoryFileInfoWebDAV(uid, folderPathId);

                if (result.Status == 0 && result.Data is UserFilesInfo userFilesInfo)
                {
                    var children = new List<WebDavNode>();

                    // 组装返回，不再做任何冗余的 Redis Set 操作！
                    foreach (var item in userFilesInfo.Dirs.Where(x => x.Id != Guid.Parse(folderRootId)))
                    {
                        children.Add(new WebDavNode { Name = item.FolderName, IsFolder = true, Length = 0, Created = item.CreationTime, Modified = item.CreationTime });
                    }

                    foreach (var item in userFilesInfo.FileInfos)
                    {
                        children.Add(new WebDavNode { Name = item.FileName, IsFolder = false, Length = (long)item.FileSizeInBytes, Created = item.CreationTime, Modified = item.LastModifiedTime });
                    }

                    return children;
                }
            }
            return null;
        }


        // 4. 下载事件
        public async Task<Stream?> GetFileStreamAsync(string filePath)
        {
            if (!await _icache.ExistsAsync("WebDavAuth", userpassword)) return null;

            string uid = await _icache.GetAsync<string>("WebDavAuth", userpassword);
            string folderRootId = (await _fileHandler.GetFolderRoot(uid)).Data.ToString();

            // 1. 提取父目录和文件名
            string parentPath = GetParentPath(filePath);
            string nodeName = GetFileName(filePath);
            string parentFolderId = folderRootId;

            // 2. 找到父目录ID
            if (parentPath != "/")
            {
                var parentResult = await _fileHandler.GetFolderByPathStrict(uid, folderRootId, parentPath);
                if (parentResult.Status != 0) return null;
                parentFolderId = parentResult.Data.ToString();
            }

            // 3. 获取父目录下的文件列表，找到目标文件ID
            DefaultMsg dirResult = await _fileHandler.GetUserDirectoryFileInfoWebDAV(uid, parentFolderId);
            if (dirResult.Status == 0 && dirResult.Data is UserFilesInfo userFilesInfo)
            {
                var targetFile = userFilesInfo.FileInfos.FirstOrDefault(x => x.FileName == nodeName);
                if (targetFile != null)
                {

                    // 步骤一：获取该文件的临时下载密钥 (内部做了缓存和归属权校验)
                    var keyResult = await _fileHandler.GetTempDownLoadKey(uid, targetFile.Id.ToString());

                    if (keyResult != null && keyResult.Status == 0 && keyResult.Data != null)
                    {
                        // 步骤二：通过密钥换取 FileStreamInfo
                        var fileStreamInfo = await _fileHandler.DownloadFileWithKey(keyResult.Data.ToString());

                        if (fileStreamInfo != null)
                        {

                            return fileStreamInfo.FileStream;
                        }
                    }
                }
            }

            return null;
        }

        // 5. 上传事件
        public async Task<WebDavResult> PutFileAsync(string filePath, Stream contentStream)
        {
            if (!await _icache.ExistsAsync("WebDavAuth", userpassword)) return WebDavResult.Fail(401, "未登录");

            string uid = await _icache.GetAsync<string>("WebDavAuth", userpassword);
            string folderRootId = (await _fileHandler.GetFolderRoot(uid)).Data.ToString();

            string parentPath = GetParentPath(filePath);
            string fileName = GetFileName(filePath);
            string parentFolderId = folderRootId;

            // 1. 获取父目录ID
            if (parentPath != "/")
            {
                var parentResult = await _fileHandler.GetFolderByPathStrict(uid, folderRootId, parentPath);
                if (parentResult.Status != 0) return WebDavResult.Fail(409, "父目录不存在"); // 409 Conflict
                parentFolderId = parentResult.Data.ToString();
            }

            // 2. 检查是否已经存在同名文件（实现 WebDAV 要求的覆盖语义）
            bool isNew = true;
            DefaultMsg dirResult = await _fileHandler.GetUserDirectoryFileInfoWebDAV(uid, parentFolderId);
            if (dirResult.Status == 0 && dirResult.Data is UserFilesInfo userFilesInfo)
            {
                var existingFile = userFilesInfo.FileInfos.FirstOrDefault(x => x.FileName == fileName);
                if (existingFile != null)
                {
                    isNew = false;

                    if (existingFile.FileSizeInBytes == 0)
                    {
                        // 如果旧文件是 0 字节，说明它很可能是 WebDAV 刚才传的占位符。
                        // 彻底从数据库抹除这条记录，不留垃圾。
                        await _sqlSugarClient.Deleteable<Models.FileInfo>().Where(x => x.Id == existingFile.Id).ExecuteCommandAsync();

                        // 清理缓存，防止影响后续的同名文件插入
                        await _icache.RemoveAsync("FolderVersion", $"{uid}:{parentFolderId}");
                    }
                    else
                    {
                        // 正常的覆盖旧文件，走原有的软删除逻辑
                        await _fileHandler.DeleteFileAsync(uid, new[] { existingFile.Id.ToString() });
                    }
                }
            }

            // 3. 调用你的上传业务
            DefaultMsg uploadResult = await _fileHandler.UpdataFileAsync(uid, contentStream, fileName, parentFolderId);

            if (uploadResult.Status == 0)
            {
                // 201 Created (新文件), 204 No Content (覆盖旧文件)
                return WebDavResult.Success(isNew ? 201 : 204);
            }
            else
            {
                return WebDavResult.Fail(500, uploadResult.Data?.ToString() ?? "上传失败");
            }
        }

        // 6. 新建文件夹事件
        public async Task<WebDavResult> CreateFolderAsync(string folderPath)
        {
            if (await _icache.ExistsAsync("WebDavAuth", userpassword))
            {
                string uid = await _icache.GetAsync<string>("WebDavAuth", userpassword);
                string folderRootId = (await _fileHandler.GetFolderRoot(uid)).Data.ToString();
                // 提取父目录路径和当前节点名
                string parentPath = GetParentPath(folderPath);
                string nodeName = GetFileName(folderPath);
                DefaultMsg result = null;
                if (parentPath == "/")
                {
                    result = await _fileHandler.CreateFolder(uid, folderRootId, nodeName);
                    if (result.Status == 0)
                    {
                        return WebDavResult.Success(201);
                    }
                    else
                    {
                        return WebDavResult.Fail(401, result.Data.ToString());
                    }
                }

                result = await _fileHandler.GetFolderByPathStrict(uid, folderRootId, parentPath);
                if (result.Status != 0)
                {
                    return WebDavResult.Fail(401, result.Data.ToString());
                }



                DefaultMsg info = await _fileHandler.CreateFolder(uid, result.Data.ToString(), nodeName);
                if (info.Status == 0)
                {
                    return WebDavResult.Success(201);
                }
                else
                {
                    return WebDavResult.Fail(401, info.Data.ToString());
                }
            }
            else
            {
                return WebDavResult.Fail(401, "未登录");
            }
        }

        // 7. 删除事件
        public async Task<WebDavResult> DeleteAsync(string path)
        {
            if (path == "/") return WebDavResult.Fail(403, "不能删除根目录");
            if (!await _icache.ExistsAsync("WebDavAuth", userpassword)) return WebDavResult.Fail(401, "未登录");

            string uid = await _icache.GetAsync<string>("WebDavAuth", userpassword);
            string folderRootId = (await _fileHandler.GetFolderRoot(uid)).Data.ToString();

            string parentPath = GetParentPath(path);
            string nodeName = GetFileName(path);
            string parentFolderId = folderRootId;

            // 1. 获取父目录ID
            if (parentPath != "/")
            {
                var parentResult = await _fileHandler.GetFolderByPathStrict(uid, folderRootId, parentPath);
                if (parentResult.Status != 0) return WebDavResult.Fail(404, "父目录不存在");
                parentFolderId = parentResult.Data.ToString();
            }

            // 2. 在父目录中精确查找是文件还是文件夹
            DefaultMsg dirResult = await _fileHandler.GetUserDirectoryFileInfoWebDAV(uid, parentFolderId);
            if (dirResult.Status == 0 && dirResult.Data is UserFilesInfo userFilesInfo)
            {
                // 判断是否为文件
                var targetFile = userFilesInfo.FileInfos.FirstOrDefault(x => x.FileName == nodeName);
                if (targetFile != null)
                {
                    var res = await _fileHandler.DeleteFileAsync(uid, new[] { targetFile.Id.ToString() });
                    return res.Status == 0 ? WebDavResult.Success(204) : WebDavResult.Fail(500, res.Data?.ToString());
                }

                // 判断是否为文件夹
                var targetFolder = userFilesInfo.Dirs.FirstOrDefault(x => x.FolderName == nodeName);
                if (targetFolder != null)
                {
                    var res = await _fileHandler.DeleteUserFolderAsync(uid, new[] { targetFolder.Id.ToString() });
                    return res.Status == 0 ? WebDavResult.Success(204) : WebDavResult.Fail(500, res.Data?.ToString());
                }
            }

            return WebDavResult.Fail(404, "文件或文件夹不存在");
        }

        // 8. 移动事件
        // 8. 移动或重命名事件 (对接真实数据库与业务模型)
        public async Task<WebDavResult> MoveAsync(string sourcePath, string destinationPath)
        {
            // 1. 登录与缓存校验
            if (!await _icache.ExistsAsync("WebDavAuth", userpassword))
            {
                return WebDavResult.Fail(401, "未登录");
            }

            string uid = await _icache.GetAsync<string>("WebDavAuth", userpassword);
            string folderRootId = (await _fileHandler.GetFolderRoot(uid)).Data.ToString();

            // 2. 解析路径与名称
            string sourceParentPath = GetParentPath(sourcePath);
            string sourceName = GetFileName(sourcePath);

            string destParentPath = GetParentPath(destinationPath);
            string destName = GetFileName(destinationPath);

            bool isSameDirectory = sourceParentPath == destParentPath; // 父目录相同说明是纯重命名
            bool isSameName = sourceName == destName;                  // 名字相同说明是纯移动

            // 3. 获取源父级目录 ID
            string sourceParentFolderId = folderRootId;
            if (sourceParentPath != "/")
            {
                var srcParentResult = await _fileHandler.GetFolderByPathStrict(uid, folderRootId, sourceParentPath);
                if (srcParentResult.Status != 0) return WebDavResult.Fail(404, "源父目录不存在");
                sourceParentFolderId = srcParentResult.Data.ToString();
            }

            // 4. 获取目标父级目录 ID
            string destParentFolderId = folderRootId;
            if (destParentPath != "/")
            {
                var destParentResult = await _fileHandler.GetFolderByPathStrict(uid, folderRootId, destParentPath);
                if (destParentResult.Status != 0) return WebDavResult.Fail(409, "目标父目录不存在"); // WebDAV规范一般报409
                destParentFolderId = destParentResult.Data.ToString();
            }

            // 5. 查找源节点（判定是文件还是文件夹，并获取真实ID）
            DefaultMsg dirResult = await _fileHandler.GetUserDirectoryFileInfoWebDAV(uid, sourceParentFolderId);
            if (dirResult.Status != 0 || !(dirResult.Data is UserFilesInfo userFilesInfo))
            {
                return WebDavResult.Fail(500, "无法读取源目录信息");
            }

            bool isFolder = false;
            string targetId = "";

            var targetFolder = userFilesInfo.Dirs.FirstOrDefault(x => x.FolderName == sourceName);
            if (targetFolder != null)
            {
                isFolder = true;
                targetId = targetFolder.Id.ToString();
            }
            else
            {
                var targetFile = userFilesInfo.FileInfos.FirstOrDefault(x => x.FileName == sourceName);
                if (targetFile != null)
                {
                    isFolder = false;
                    targetId = targetFile.Id.ToString();
                }
            }

            if (string.IsNullOrEmpty(targetId)) return WebDavResult.Fail(404, "要操作的文件或文件夹不存在");

            DefaultMsg actionResult = null;

            // 动作A：如果名字变了，执行重命名
            if (!isSameName)
            {
                var renameInfo = new drive_api.Models.FileOrDirReNameInfo
                {
                    Type = isFolder ? 1 : 0, // 0文件 1文件夹
                    Id = targetId,
                    NewName = destName
                };
                actionResult = await _fileHandler.RenameFileOrDir(uid, renameInfo);
                if (actionResult.Status != 0) return WebDavResult.Fail(403, actionResult.Data?.ToString());
            }

            // 动作B：如果目录变了，执行移动
            if (!isSameDirectory)
            {
                var moveInfo = new drive_api.Models.FileOrDirMoveInfo
                {
                    FolderIds = isFolder ? new string[] { targetId } : null,
                    FileIds = !isFolder ? new string[] { targetId } : null,
                    NewFolderId = destParentFolderId
                };
                actionResult = await _fileHandler.MoveFileOrDir(uid, moveInfo);
                if (actionResult.Status != 0) return WebDavResult.Fail(403, actionResult.Data?.ToString());
            }

            // 7. 返回结果 (移动/重命名成功，WebDAV 规范建议返回 201 Created)
            return WebDavResult.Success(201);
        }

        // --- 辅助方法 ---
        private string GetParentPath(string path) { int lastSlash = path.LastIndexOf('/'); return lastSlash <= 0 ? "/" : path.Substring(0, lastSlash); }
        private string GetFileName(string path) => path == "/" ? "Root" : path.Split('/').Last();

        public class MemoryNode
        {
            public bool IsFolder { get; set; }
            public string Name { get; set; } = "";
            public string? TempFilePath { get; set; }
            public long Length { get; set; }
            public DateTime Created { get; set; } = DateTime.UtcNow;
            public DateTime Modified { get; set; } = DateTime.UtcNow;
        }
    }

    public static class JwtHelper
    {
        /// <summary>
        /// 解析 JWT Token，获取用户 ID、过期状态以及离过期的剩余分钟数
        /// </summary>
        /// <param name="token">JWT 字符串</param>
        /// <returns>返回元组：(用户ID, 是否已过期, 剩余分钟数)</returns>
        public static (string? UserId, bool IsExpired, double remainingDays) ParseTokenInfo(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return (null, true, 0);
            }

            var handler = new JwtSecurityTokenHandler();

            // 1. 检查字符串是否符合 JWT 格式
            if (!handler.CanReadToken(token))
            {
                return (null, true, 0);
            }

            try
            {
                // 2. 解析 Token (仅解析结构，不验证数字签名)
                var jwtToken = handler.ReadJwtToken(token);

                // 3. 提取 ClaimTypes.NameIdentifier
                var userIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier);
                string? userId = userIdClaim?.Value;

                // 4. 计算过期时间和剩余分钟数
                DateTime expireTimeUtc = jwtToken.ValidTo;
                DateTime nowUtc = DateTime.UtcNow;

                bool isExpired = expireTimeUtc < nowUtc;

                double remainingDays = 0;
                if (!isExpired)
                {
                    // 计算时间差并转换为总分钟数（保留两位小数，更加精确）
                    remainingDays = Math.Round((expireTimeUtc - nowUtc).TotalDays, 2);
                }

                return (userId, isExpired, remainingDays);
            }
            catch (Exception)
            {
                // 解析过程中发生异常
                return (null, true, 0);
            }
        }
    }
}

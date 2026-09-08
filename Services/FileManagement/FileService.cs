using Dm.util;
using drive_api.Models;
using drive_api.Services.AI;
using drive_api.Services.Cache;
using drive_api.Services.Config;
using drive_api.Services.MQ;
using drive_api.Services.Tool;
using drive_api.Services.UserManagement;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32.SafeHandles;
using MimeDetective;
using MimeDetective.Engine;
using MimeDetective.Storage;
using Npgsql.Internal.Postgres;
using Serilog.Sinks.File;
using SqlSugar;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static System.Runtime.InteropServices.JavaScript.JSType;
using DbType = SqlSugar.DbType;
using fi = drive_api.Models.FileInfo;

namespace drive_api.Services.FileManagement
{
    public class FileService(
        Helper helper,
        ImgAiVectorService avs,
        IWebHostEnvironment env,
        IMessageBus messageBus,
        IContentInspector contentInspector,
        ILogger<FileService> logger,
        ICache icache,
        ISqlSugarClient sqlSugarClient,
        AppConfigInfo appConfigInfo,
        TextAiVectorService textAiVectorService,
        UserService userHandler)
    {
        private readonly ILogger<FileService> _logger = logger;
        private readonly ICache _icache = icache;
        private readonly ISqlSugarClient _sqlSugarClient = sqlSugarClient;
        private readonly AppConfigInfo _appConfigInfo = appConfigInfo;
        private readonly Helper _helper = helper;
        public static string defaultfileDirectory = AppContext.BaseDirectory;
        private readonly UserService _userHandler = userHandler;
        private readonly IContentInspector _contentInspector = contentInspector;
        private readonly IMessageBus _messageBus = messageBus;
        private readonly IWebHostEnvironment _env = env;
        private readonly ImgAiVectorService _avs = avs;
        private readonly TextAiVectorService _textAiVectorService = textAiVectorService;
        /// <summary>
        /// hash查询并发限制器，限制同时进行的 hash 查询数量，防止数据库压力过大
        /// </summary>
        private static readonly SemaphoreSlim HashQueryLimiter = new(40, 40);
        public const string defaultGuid = "00000000-0000-0000-0000-000000000000";


        // 声明一个基于 UploadId 的并发锁字典
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _taskLocks = new();
        /// <summary>
        /// 分片数据存储路径 在应用程序根目录下的 Temp/ChunkData 文件夹中
        /// </summary>
        public static string ChunksDataPath = "";
        /// <summary>
        /// 用户设置的文件存储路径
        /// </summary>
        public static IEnumerable<string> fileDirectories;

        /// <summary>
        /// 获取合适的文件存储目录，用于当一个 盘/位置 存储空间满自动切换到下一个 盘/位置 存储
        /// </summary>
        /// <returns></returns>
        public string GetFileStorageDirectory(long fileBytes)
        {
            var shuffledDirectories = _helper.ShuffleFisherYates(fileDirectories);

            foreach (var item in shuffledDirectories)
            {
                try
                {
                    //获取绝对路径
                    string fullPath = Path.GetFullPath(item);

                    //提取所属的盘符根路径
                    string driveRoot = Path.GetPathRoot(fullPath)
                        ?? throw new InvalidOperationException($"无法获取路径根目录: {item}");

                    //传入盘符根路径
                    var di = new DriveInfo(driveRoot);

                    if (di.AvailableFreeSpace > fileBytes)
                    {
                        _logger.LogDebug("成功分配存储目录: {StorageDirectory}, 申请空间: {FileBytes} Bytes", item, fileBytes);
                        return item;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "存储目录空间不足被跳过: {StorageDirectory}. 剩余空间: {FreeSpace} Bytes, 请求空间: {FileBytes} Bytes",
                            item, di.AvailableFreeSpace, fileBytes);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "获取存储目录信息失败: {StorageDirectory}", item);
                }
            }

            _logger.LogError("所有配置的存储目录均空间不足或无法正常访问，无法为大小 {FileBytes} Bytes 的文件分配空间", fileBytes);
            throw new Exception("所有配置的存储目录均空间不足或无法正常访问，请检查FileConfig配置");
        }


        /// <summary>
        /// 验证文件目录是否存在 ，不存在则创建
        /// </summary>
        public void VerifyDirectory()
        {
            var paths = appConfigInfo.fileSetting.LocalFilePath?.Split('|');
            if (paths is not { Length: > 0 })
            {
                paths = [defaultfileDirectory];
            }
            fileDirectories = paths.Select(p => Path.Combine(p, "UserFiles"));

            foreach (var path in fileDirectories)
            {
                if (!Directory.Exists(path))
                {
                    _logger.LogWarning("必要目录不存在，正在创建: {DirectoryPath}", path);
                    try
                    {
                        Directory.CreateDirectory(path);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "必要目录创建失败: {DirectoryPath}", path);
                    }
                }
                else
                {
                    _logger.LogInformation("已启用用户文件存储目录: {DirectoryPath}", path);
                }
            }



            ChunksDataPath = Path.Combine(_appConfigInfo.fileSetting.TempFilePath, "Temp", "ChunkData");
            string[] tempPaths = [Path.Combine(_appConfigInfo.fileSetting.TempFilePath, "Temp"), ChunksDataPath];

            foreach (var item in tempPaths)
            {
                if (!Directory.Exists(item))
                {
                    _logger.LogWarning("必要临时目录不存在，正在创建: {TempDirectoryPath}", item);

                    try
                    {
                        Directory.CreateDirectory(item);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "必要临时目录创建失败: {TempDirectoryPath}", item);
                    }
                }
            }


        }

        /// <summary>
        /// 获取用户目录下的文件和文件夹信息
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="folderId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetUserDirectoryFileInfo(string userId, string folderId = defaultGuid,
            int pageIndex = 1, int pageSize = 50,bool isSimplify = false)
        {
            if (!Guid.TryParse(userId, out Guid uid))
            {
                return new DefaultMsg(1, "uid格式错误", null);
            }

            if (!Guid.TryParse(folderId, out Guid foid))
            {
                return new DefaultMsg(1, "folderId格式错误", null);
            }

            string json = "";
            // 获取当前目录的缓存版本号 相当于获取通往当前的目录分页缓存的密钥 任何修改当前目录下的文件或文件夹的操作都应该更新这个版本号 从而使得旧版本号对应的缓存失效 避免了频繁删除缓存的操作 提高性能
            string versionCacheKey = $"{userId}:{foid}";
            string currentVersion = await _icache.GetAsync<string>("FolderVersion", versionCacheKey);

            // 如果没有版本号，生成一个新的并缓存 构建用户与缓存之间的桥梁 用版本号来连接，只要阻断了版本号与缓存的连接 就相当于清除了缓存 这样就避免了频繁删除缓存的操作 提高性能
            if (string.IsNullOrEmpty(currentVersion))
            {
                currentVersion = Guid.NewGuid().ToString("N");
                await _icache.SetAsync("FolderVersion", versionCacheKey, currentVersion,
                    DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            }

            // 拼接带有版本号和分页参数的完整缓存Key
            string pageCacheKey = $"{userId}:{foid}:{currentVersion}:p{pageIndex}:s{pageSize}";

            // 尝试获取分页缓存 
            if (await _icache.ExistsAsync("FileDirectoryPage", pageCacheKey))
            {
                _logger.LogDebug("用户{UserId}浏览目录命中分页缓存{CacheKey}", userId, pageCacheKey);
                try
                {
                    return new DefaultMsg(0, "cache",
                        JsonSerializer.Deserialize<UserFilesInfo>(
                            await _icache.GetAsync<string>("FileDirectoryPage", pageCacheKey)));
                }
                catch (Exception)
                {
                    _logger.LogWarning("缓存数据反序列化失败，准备重新获取:{CacheKey}", pageCacheKey);
                }
            }

            // 缓存未命中
            
            UserFilesInfo userFilesInfo = new UserFilesInfo();
            UserFolderInfo existingDir = null;
            if (folderId != defaultGuid)
            {
                existingDir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .FirstAsync(x => x.UserId == uid && x.Id == foid && x.IsDeleted == false);
            }
            else
            {
                //查询根目录ID兜底
                Guid.TryParse((await GetFolderRoot(userId)).Data as string, out Guid rootfid);
                existingDir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .FirstAsync(x => x.UserId == uid && x.Id == rootfid && x.IsDeleted == false);
            }

            if (existingDir == null) return new DefaultMsg(1, "目录不存在", null);


            try
            {
                RefAsync<int> totalFileCount = 0;
               var folder =  await _sqlSugarClient.Queryable<UserFolderInfo>()
                        .Where(x => x.UserId == uid && existingDir.Id == x.ParentId && x.IsDeleted == false)
                        .Select(x => new UserDirsInfoItem
                        { Id = x.Id, FolderName = x.FolderName, CreationTime = x.CreationTime })
                        .ToArrayAsync();

                var files = _sqlSugarClient.Queryable<fi>()
                        .Where(x => x.UserId == uid && x.FolderId == existingDir.Id && x.IsDeleted == false)
                        .OrderBy(x => x.CreationTime, OrderByType.Desc);
                //是否获取简化信息
                if (isSimplify)
                {
                    UserFilesInfoSimplify userFilesInfoSimplify = new UserFilesInfoSimplify();
                    // 【查询文件夹】：文件夹不分页
                    userFilesInfoSimplify.Dirs = folder;

                    // 【查询文件】：使用 SqlSugar 进行分页查询
                    userFilesInfoSimplify.FileInfos = (await files
                        .Select(x => new UserFilesInfoItemSimplify
                        {
                            Id = x.Id,
                            FileName = x.FileName,
                            CreationTime = x.CreationTime,
                        })
                        .ToPageListAsync(pageIndex, pageSize, totalFileCount)).ToArray(); // 分页核心
                    userFilesInfoSimplify.TotalFileCount = totalFileCount;    // 文件总数 不包括文件夹
                    return new DefaultMsg(0, "success", userFilesInfoSimplify);
                }
                else
                {
                    // 【查询文件夹】：文件夹不分页
                    userFilesInfo.Dirs = folder;

                    // 【查询文件】：使用 SqlSugar 进行分页查询
                    userFilesInfo.FileInfos = (await files
                        .Select(x => new UserFilesInfoItem
                        {
                            Id = x.Id,
                            FileName = x.FileName,
                            FileSizeInBytes = x.FileSizeInBytes,
                            FileHash = x.FileHash,
                            FolderId = x.FolderId,
                            CreationTime = x.CreationTime,
                            LastModifiedTime = x.LastModifiedTime,
                        })
                        .ToPageListAsync(pageIndex, pageSize, totalFileCount)).ToArray(); // 分页核心
                                                                                          // 文件总数 不包括文件夹
                    userFilesInfo.TotalFileCount = totalFileCount;
                }
               

          

                // 缓存关联关系
                var time = DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod);
                var dirTasks = userFilesInfo.Dirs.Select(item =>
                    _icache.SetAsync("FolderIdToPFolder", $"{userId}:{item.Id}", folderId.ToString(), time));
                var fileTasks = userFilesInfo.FileInfos.Select(item =>
                    _icache.SetAsync("FileIdToFolderId", $"{userId}:{item.Id}", folderId.ToString(), time));
                await Task.WhenAll(fileTasks.Concat(dirTasks));

                // 写入分页缓存
                json = JsonSerializer.Serialize(userFilesInfo);
                _icache.SetAsync("FileDirectoryPage", pageCacheKey, json,
                    DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户ID:{UserId},获取用户文件目录错误:{ErrorMessage}", userId, ex.Message);
                return new DefaultMsg(1, "获取失败", null);
            }

            return new DefaultMsg(0, "success", userFilesInfo);
        }

        /// <summary>
        /// 获取用户目录下的文件和文件夹信息 WebDAV专用 不分页
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="folderId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetUserDirectoryFileInfoWebDAV(string userId, string folderId = defaultGuid)
        {
            if (!Guid.TryParse(userId, out Guid uid))
            {
                return new DefaultMsg(1, "uid格式错误", null);
            }

            if (!Guid.TryParse(folderId, out Guid foid))
            {
                return new DefaultMsg(1, "folderId格式错误", null);
            }

            string json = "";

            string pageCacheKey = $"{userId}:{foid}";

            // 尝试获取缓存 
            if (await _icache.ExistsAsync("FileDirectoryWebDAV", pageCacheKey))
            {
                _logger.LogDebug("用户{UserId}浏览目录命中缓存{CacheKey}", userId, pageCacheKey);
                try
                {
                    return new DefaultMsg(0, "cache",
                        JsonSerializer.Deserialize<UserFilesInfo>(
                            await _icache.GetAsync<string>("FileDirectoryWebDAV", pageCacheKey)));
                }
                catch (Exception)
                {
                    _logger.LogWarning("缓存数据反序列化失败，准备重新获取:{CacheKey}", pageCacheKey);
                }
            }

            // 缓存未命中
            UserFilesInfo userFilesInfo = new UserFilesInfo();
            UserFolderInfo existingDir = null;
            if (folderId != defaultGuid)
            {
                existingDir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .FirstAsync(x => x.UserId == uid && x.Id == foid && x.IsDeleted == false);
            }
            else
            {
                //查询根目录ID兜底
                Guid.TryParse((await GetFolderRoot(userId)).Data as string, out Guid rootfid);
                existingDir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .FirstAsync(x => x.UserId == uid && x.Id == rootfid && x.IsDeleted == false);
            }

            if (existingDir == null) return new DefaultMsg(1, "目录不存在", null);


            try
            {
                RefAsync<int> totalFileCount = 0;

                // 【查询文件夹】
                userFilesInfo.Dirs = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .Where(x => x.UserId == uid && existingDir.Id == x.ParentId && x.IsDeleted == false)
                    .Select(x => new UserDirsInfoItem
                    { Id = x.Id, FolderName = x.FolderName, CreationTime = x.CreationTime })
                    .ToArrayAsync();

                // 【查询文件】
                userFilesInfo.FileInfos = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => x.UserId == uid && x.FolderId == existingDir.Id && x.IsDeleted == false)
                    .OrderBy(x => x.CreationTime, OrderByType.Desc)
                    .Select(x => new UserFilesInfoItem
                    {
                        Id = x.Id,
                        FileName = x.FileName,
                        FileSizeInBytes = x.FileSizeInBytes,
                        FileHash = x.FileHash,
                        FolderId = x.FolderId,
                        CreationTime = x.CreationTime,
                        LastModifiedTime = x.LastModifiedTime,
                    })
                    .ToArrayAsync();

                // 文件总数 不包括文件夹
                userFilesInfo.TotalFileCount = totalFileCount;


                var time = DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod);
                var dirTasks = userFilesInfo.Dirs.Select(item =>
                    _icache.SetAsync("FolderIdToPFolder", $"{userId}:{item.Id}", folderId.ToString(), time));
                var fileTasks = userFilesInfo.FileInfos.Select(item =>
                    _icache.SetAsync("FileIdToFolderId", $"{userId}:{item.Id}", folderId.ToString(), time));
                await Task.WhenAll(fileTasks.Concat(dirTasks));

                // 写入缓存
                json = JsonSerializer.Serialize(userFilesInfo);
                _icache.SetAsync("FileDirectoryWebDAV", pageCacheKey, json,
                    DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户ID:{UserId},获取用户文件目录错误:{ErrorMessage}", userId, ex.Message);
                return new DefaultMsg(1, "获取失败", null);
            }

            return new DefaultMsg(0, "success", userFilesInfo);
        }

        /// <summary>
        /// 获取用户单个文件信息，带缓存。
        /// </summary>
        public async Task<DefaultMsg> GetFileInfo(string userId, string fileId)
        {
            if (!Guid.TryParse(userId, out Guid uid))
                return new DefaultMsg(1, "uid格式错误", null);

            if (!Guid.TryParse(fileId, out Guid fid))
                return new DefaultMsg(1, "fileId格式错误", null);

            string cacheKey = $"{uid}:{fid}";
            var expiration = DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod);

            if (await _icache.ExistsAsync("UserFileInfo", cacheKey))
            {
                try
                {
                    string json = await _icache.GetAsync<string>("UserFileInfo", cacheKey);
                    var cacheInfo = JsonSerializer.Deserialize<UserFilesInfoItem>(json);

                    if (cacheInfo != null)
                    {
                        _logger.LogDebug("用户{UserId}获取文件{FileId}命中文件详情缓存", uid, fid);
                        return new DefaultMsg(0, "cache", cacheInfo);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "文件详情缓存反序列化失败，准备查询数据库. CacheKey: {CacheKey}", cacheKey);
                    await _icache.RemoveAsync("UserFileInfo", cacheKey);
                }
            }

            var fileInfo = await _sqlSugarClient.Queryable<fi>()
                .Where(x => x.UserId == uid && x.Id == fid && x.IsDeleted == false)
                .Select(x => new UserFilesInfoItem
                {
                    Id = x.Id,
                    FileName = x.FileName,
                    FileSizeInBytes = x.FileSizeInBytes,
                    FileHash = x.FileHash,
                    FolderId = x.FolderId,
                    CreationTime = x.CreationTime,
                    LastModifiedTime = x.LastModifiedTime,
                })
                .FirstAsync();

            if (fileInfo == null)
            {
                await _icache.RemoveAsync("UserFileInfo", cacheKey);
                return new DefaultMsg(1, "文件不存在", null);
            }

            await _icache.SetAsync("FileIdToFolderId", $"{uid}:{fid}", fileInfo.FolderId.ToString(), expiration);

            string jsonData = JsonSerializer.Serialize(fileInfo);
            await _icache.SetAsync("UserFileInfo", cacheKey, jsonData, expiration);

            return new DefaultMsg(0, "success", fileInfo);
        }

        /// <summary>
        /// 获取文件具体页数
        /// </summary>
        /// <param name="uid"></param>
        /// <returns></returns>
        public async Task<int> GetFilePageIndexAsync(
            Guid uid,
            fi fileInfo,
            int pageSize)
        {
            var countBefore = await _sqlSugarClient.Queryable<fi>()
                .Where(x =>
                    x.UserId == uid &&
                    x.FolderId == fileInfo.FolderId &&
                    x.IsDeleted == false &&
                    x.CreationTime > fileInfo.CreationTime)
                .CountAsync();

            return countBefore / pageSize + 1;
        }

        /// <summary>
        /// 检测hash是否存在  存在返回对象
        /// </summary>
        /// <param name="fileHash"></param>
        /// <returns></returns>
        private async Task<fi> CheckForDuplicateAsync(string fileHash)
        {
            await HashQueryLimiter.WaitAsync();

            try
            {
                return await _sqlSugarClient.Queryable<fi>()
                    .FirstAsync(x => x.FileHash == fileHash && !x.IsDeleted);
            }
            finally
            {
                HashQueryLimiter.Release();
            }
        }

        /// <summary>
        /// 获取用户存储容量信息
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        /// <summary>
        /// 获取用户存储容量信息
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetUserStorageCapacityInfo(string userId)
        {
            StorageCapacityInfo sci;
            // 参数校验：如果转换失败，直接返回，避免后续无效操作
            if (!Guid.TryParse(userId, out Guid uid)) return null;

            //触发缓存读取
            if (await _icache.ExistsAsync("UserStorageCapacityInfo", userId))
            {
                //注意 如果修改全局容量需要清除所有缓存或重启，如果是用户单独修改容量 只需要在修改用户单独存量的地方清除对应用户缓存
                string info = await _icache.GetAsync<string>("UserStorageCapacityInfo", userId);
                if (!string.IsNullOrEmpty(info))
                {
                    _logger.LogDebug("获取用户存储容量命中缓存. UserId: {UserId}", userId);
                    return new DefaultMsg(0, "cache", JsonSerializer.Deserialize<StorageCapacityInfo>(info));
                }
            }

            _logger.LogDebug("用户存储容量未命中缓存，开始检索数据库进行重算. UserId: {UserId}", userId);

            // 获取用户信息 和 计算已用空间 是两个独立的数据库操作，可以同时进行，减少总等待时间。
            // (注：为保证复用同一个 SqlSugarClient 的线程安全，这里保持现有的顺序 await 执行)

            // 任务A：查询用户设置
            var userTask = await _sqlSugarClient.Queryable<User>()
                .Where(x => x.UserId == uid)
                .Select(x => new { x.TotalStorageGB, x.UserId })
                .FirstAsync();

            // 任务B：计算已用空间 (物理去重)
            var usedSpaceTask = await _sqlSugarClient.Queryable<fi>()
                .Where(x => x.UserId == uid && !x.IsDeleted)
                // 1. 只选择用于去重的列(如Hash)和用于计算的列(大小)
                .Select(x => new { x.FileHash, x.FileSizeInBytes })
                // 2. 进行去重 (SELECT DISTINCT FileHash, FileSizeInBytes ...)
                .Distinct()
                .MergeTable()
                // 4. 对这张去重后的临时表进行求和
                .SumAsync(x => x.FileSizeInBytes);

            //处理结果
            if (userTask == null)
            {
                _logger.LogWarning("计算存储容量失败：数据库中未找到该用户. UserId: {UserId}", userId);
                return new DefaultMsg(1, "用户不存在", null);
            }

            decimal userTotalGb = userTask.TotalStorageGB;
            decimal finalTotalGb = userTotalGb > 0 ? userTotalGb : _appConfigInfo.userSetting.DefaultTotalStorageGb;

            sci = new StorageCapacityInfo
            {
                UsedSpaceInBytes = (ulong)usedSpaceTask,
                // 总容量也转换成字节传回去，方便前端计算百分比
                TotalSpaceInBytes = (ulong)(finalTotalGb * 1024 * 1024 * 1024)
            };

            //设置缓存
            string sciJson = JsonSerializer.Serialize(sci);
            _icache.SetAsync("UserStorageCapacityInfo", userId, sciJson,
                DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));


            _logger.LogInformation(
                "用户存储容量计算完成. UserId: {UserId}, UsedSpace: {UsedSpace} Bytes, TotalSpace: {TotalSpace} Bytes",
                userId, sci.UsedSpaceInBytes, sci.TotalSpaceInBytes);

            // 5. 组装返回
            return new DefaultMsg(0, "success", sci);
        }

        /// <summary>
        /// 转存文件  当只传入hash表示 从数据库查找相同hash的文件存入当前用户
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> SaveToAsync(string userId, string folderId = defaultGuid, string shareId = "",
            string hash256 = "")
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null); // 确保 UserId 是合法的
            if (!Guid.TryParse(folderId, out Guid foid))
            {
                return new DefaultMsg(1, "folderId格式错误", null);
            }
            string hash = hash256.Trim().ToLowerInvariant();

            fi? sourceItem = null;

            // 通过 shareId (分享ID) 查找  
            if (!string.IsNullOrWhiteSpace(shareId) && Guid.TryParse(shareId, out Guid guidFileId))
            {
               
                _logger.LogInformation("用户尝试转存文件. UserId: {UserId}, SourceFileId: {FileId}", userId, shareId);
            
                //暂时没写
            }
            // 通过 Hash (秒传) 查找
            else if (!string.IsNullOrWhiteSpace(hash))
            {
                // 标准化：消除 $ 插值
                _logger.LogInformation("用户通过hash上传(秒传)文件. UserId: {UserId}, FileHash: {FileHash}", userId, hash);
                sourceItem = await CheckForDuplicateAsync(hash);
            }

            // 如果没找到源文件，直接返回失败
            if (sourceItem == null)
            {
                
                _logger.LogWarning(
                    "用户上传/转存失败，源文件不存在. UserId: {UserId}, RequestFileId: {FileId}, RequestHash: {FileHash}", userId,
                    shareId, hash);
                return new DefaultMsg(1, "转存失败,转存文件不存在", null);
            }

            UserFolderInfo dir;
            if (folderId == defaultGuid)
            {
                //查询根目录ID兜底
                Guid.TryParse((await GetFolderRoot(userId)).Data as string, out Guid rootfid);
                dir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .FirstAsync(x => x.UserId == uid && x.Id == rootfid);
            }
            else
            {
                dir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .FirstAsync(x => x.UserId == uid && x.Id == foid);
            }

            if (dir == null)
            {
                _logger.LogWarning("用户转存失败，目标目录不存在. UserId: {UserId}, TargetFolderId: {FolderId}", userId, foid);
                return new DefaultMsg(1, "转存失败，目录不存在", null);
            }

            // 统一构建新对象
            var newItem = new fi
            {
                Id = Guid.NewGuid(),
                UserId = uid,
                FileName = sourceItem.FileName,
                FileType = sourceItem.FileType,
                FolderId = dir.Id,
                FileSizeInBytes = sourceItem.FileSizeInBytes,
                StoragePath = sourceItem.StoragePath,
                FileHash = sourceItem.FileHash,
                IsDeleted = false,
                CreationTime = DateTime.Now,
                LastModifiedTime = DateTime.Now
            };

            await _sqlSugarClient.Insertable(newItem).ExecuteCommandAsync();
            _icache.RemoveAsync("FolderVersion", $"{userId}:{foid}");
            _icache.RemoveAsync("UserStorageCapacityInfo", userId);
            ClearSearchFilesCache(uid);


            _logger.LogInformation(
                "用户上传/转存成功. UserId: {UserId}, NewFileId: {NewFileId}, SourceFileId: {FileId}, FileHash: {FileHash}, FileName: {FileName}",
                userId, newItem.Id, shareId, hash, newItem.FileName);

            return new DefaultMsg(0, $"{newItem.FileName}转存成功", null);
        }

        /// <summary>
        /// 上传文件 
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> UpdataFileAsync(string userId, Stream requestStream, string fileName,
            string folderId = defaultGuid)
        {
            if (!_helper.NameValidation(fileName)) return new DefaultMsg(1, "文件名格式非法", null);
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!Guid.TryParse(folderId, out Guid foid))
            {
                return new DefaultMsg(1, "folderId格式错误", null);
            }

            Guid tempFileNameObject = Guid.NewGuid();
            string tempFileName = tempFileNameObject.ToString("N");
            string tempFilePath = Path.Combine(_appConfigInfo.fileSetting.TempFilePath, "Temp", tempFileName);
            string hashString;
            long fileSize;
            ImmutableArray<DefinitionMatch> type;

            //查询上传目录是否存在
            UserFolderInfo dir;
            if (folderId == defaultGuid)
            {
                //查询根目录ID兜底
                Guid.TryParse((await GetFolderRoot(userId)).Data as string, out Guid rootfid);
                dir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .FirstAsync(x => x.UserId == uid && x.Id == rootfid);
            }
            else
            {
                dir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .FirstAsync(x => x.UserId == uid && x.Id == foid);
            }

            if (dir == null) return new DefaultMsg(1, "上传失败，目录不存在", null);

            try
            {
                await using (var fileStream =
                             new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (var sha256 = SHA256.Create())
                    await using (var cryptoStream =
                                 new CryptoStream(fileStream, sha256, CryptoStreamMode.Write, leaveOpen: false))
                    {
                        _logger.LogInformation("正在接收到用户{UserId}文件...", userId);


                        await requestStream.CopyToAsync(cryptoStream);
                        await cryptoStream.FlushFinalBlockAsync();

                        hashString = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
                        fileSize = fileStream.Length;
                    }
                }

                // 交给 MimeDetective 进行同步类型检测
                using (var tempReadStream =
                       new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    type = _contentInspector.Inspect(tempReadStream);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "接收用户 {UserId} 的文件时失败。", userId);

                // 尝试清理失败后可能留下的临时文件
                if (System.IO.File.Exists(tempFilePath))
                {
                    System.IO.File.Delete(tempFilePath);
                }

                // 向上抛出异常或返回错误信息
                throw;
            }




            // 得到哈希值及文件，进行业务逻辑
            //检查目录
            string userPath = GetFileStorageDirectory(fileSize);
            if (!Directory.Exists(userPath))
            {
                Directory.CreateDirectory(userPath);
            }

            string fileSavePath = Path.Combine(userPath, tempFileName + $".{userId}");
            fi item = await CheckForDuplicateAsync(hashString);
            fi newItem;


            if (item != null)
            {
                newItem = new fi
                {
                    Id = tempFileNameObject,
                    UserId = uid,
                    FileName = fileName,
                    FileType = _helper.GetCategory(fileName),
                    FolderId = dir.Id,
                    FileSizeInBytes = (ulong)fileSize,
                    StoragePath = item.StoragePath,
                    FileHash = hashString,
                    IsDeleted = false,
                    CreationTime = DateTime.Now,
                    LastModifiedTime = DateTime.Now
                };
                await _sqlSugarClient.Insertable<fi>(newItem).ExecuteCommandAsync();
                if (System.IO.File.Exists(tempFilePath))
                {
                    System.IO.File.Delete(tempFilePath);
                }

                _logger.LogInformation("用户{UserId}文件查找到相同文件，SHA256:{FileHash}，大小:{FileSize}字节，物理地址:{StoragePath}",
                    userId, hashString, fileSize, item.StoragePath);
            }
            else
            {
                newItem = new fi
                {
                    Id = tempFileNameObject,
                    UserId = uid,
                    FileType = _helper.GetCategory(fileName),
                    FileName = fileName,
                    FolderId = dir.Id,
                    FileSizeInBytes = (ulong)fileSize,
                    StoragePath = fileSavePath,
                    FileHash = hashString,
                    IsDeleted = false,
                    CreationTime = DateTime.Now,
                    LastModifiedTime = DateTime.Now
                };
                await _sqlSugarClient.Insertable<fi>(newItem).ExecuteCommandAsync();
                try
                {
                    File.Move(tempFilePath, fileSavePath);
                    _logger.LogInformation("用户{UserId}文件接收完成，SHA256:{FileHash}，大小:{FileSize}字节，物理地址:{StoragePath}",
                        userId, hashString, fileSize, fileSavePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "移动文件失败，从 {TempFilePath} 到 {FileSavePath}", tempFilePath, fileSavePath);
                    // 如果移动失败，也应该清理临时文件
                    if (System.IO.File.Exists(tempFilePath))
                    {
                        System.IO.File.Delete(tempFilePath);
                    }

                    throw;
                }



            }
            if (type.Any())
            {

                var result = type.First();
                string mimeType = result.Definition.File.MimeType; // 获取类型 例如: "image/jpeg"
                _logger.LogInformation("检测到用户 {UserId} 上传的类型类型:{MimeType}", userId, mimeType);
                //图片类消息队列
                if (mimeType.StartsWith("image/"))
                {
                    var imgComp = new ImgCompModel()
                    { FileHash = newItem.FileHash, StoragePath = newItem.StoragePath };
                    var imgVector = new ImgVectorModel()
                    { DbType = _sqlSugarClient.CurrentConnectionConfig.DbType, FileId = newItem.Id, StoragePath = newItem.StoragePath, UserId = newItem.UserId };
                    //缩略图
                    _messageBus.PublishAsync("ImgComp", imgComp);
                    //向量计算
                    if (_appConfigInfo.aiSetting.Enable)
                        _messageBus.PublishAsync("ImgVector", imgVector);
                }
            }

            if (Helper.TextExtensions.Contains(Path.GetExtension(newItem.FileName)) || Helper.DocExtensions.Contains(Path.GetExtension(newItem.FileName)))
            {
                var textVector = new TextVectorModel()
                { DbType = _sqlSugarClient.CurrentConnectionConfig.DbType, FileId = newItem.Id, StoragePath = newItem.StoragePath, UserId = newItem.UserId, FileName = newItem.FileName };
                _messageBus.PublishAsync("TextVector", textVector);
            }
            //清除缓存
            _icache.RemoveAsync("FolderVersion", $"{userId}:{foid}");
            _icache.RemoveAsync("UserStorageCapacityInfo", userId);
            _icache.RemoveAsync("UserInfoCache", userId);
            ClearSearchFilesCache(uid);
            return new DefaultMsg(0, $"上传成功，SHA256:{hashString}", null);
        }

        /// <summary>
        /// 创建分片上传任务
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> CreateMultipartUpload(string userId, MultipartUploadInitRequest req)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!_helper.NameValidation(req.FileName)) return new DefaultMsg(1, "文件名格式非法", null);
            if (req.FileSizeInBytes > _appConfigInfo.fileSetting.MaxFileSize) return new DefaultMsg(1, "文件大小超过限制", null);

            //检测相同hash文件是否存在  存在则拒绝继续执行 此处不处理秒传
            if (await CheckForDuplicateAsync(req.FileHash) != null)
            {
                return new DefaultMsg(1, "文件已存在", null);
            }
            //创建分片上传目录
            Guid UploadId = Guid.NewGuid();
            string workingDirectory = Path.Combine(ChunksDataPath, UploadId.toString());
            try
            {
                Directory.CreateDirectory(workingDirectory);
            }
            catch (Exception)
            {

                _logger.LogError("创建分片上传目录失败. UserId: {UserId}, UploadId: {UploadId}, Path: {Path}", userId, UploadId, workingDirectory);
                return new DefaultMsg(1, "创建分片上传目录失败", null);
            }

            //分片数计算
            int Chunks = 0;
            if (req.FileSizeInBytes <= _appConfigInfo.fileSetting.FileChunkSizeBytes)
            {
                Chunks = 1;
            }
            else
            {
                Chunks = (int)(req.FileSizeInBytes % _appConfigInfo.fileSetting.FileChunkSizeBytes == 0
                        ? req.FileSizeInBytes / _appConfigInfo.fileSetting.FileChunkSizeBytes
                        : req.FileSizeInBytes / _appConfigInfo.fileSetting.FileChunkSizeBytes + 1);
            }
            //构建分片文件信息
            UploadTaskModel uploadTaskModel = new()
            {
                UserId = uid,
                CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
                UploadId = UploadId,
                Meta = new UploadMetaModel()
                {
                    FileName = req.FileName,
                    TotalSize = req.FileSizeInBytes,
                    FileHash = req.FileHash,
                    TotalChunks = Chunks,
                }
            };
            //转json存缓存和文件
            string json = JsonSerializer.Serialize(uploadTaskModel);
            _ = _icache.SetAsync("MultipartUploadTask", $"{uid}-{UploadId}", json, DateTimeOffset.Now.AddDays(3));
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "upload_task.json"), json);
            _logger.LogInformation($"用户{userId}创建分片上传任务成功: {json}");
            return new DefaultMsg(0, "分片上传任务创建成功", UploadId);


        }
        /// <summary>
        /// 得到文件分片信息
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="uploadId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetFileChunkInfo(string userId, string uploadId)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!Guid.TryParse(uploadId, out Guid upid)) return new DefaultMsg(1, "uploadId格式错误", null);
            if (await _icache.ExistsAsync("MultipartUploadTask", $"{uid}-{upid}"))
            {
                try
                {
                    return new DefaultMsg(0, "cache", JsonNode.Parse(await _icache.GetAsync<string>("MultipartUploadTask", $"{uid}-{upid}")));
                }
                catch (Exception)
                {

                    _logger.LogWarning("MultipartUploadTask缓存数据反序列化失败，准备重新获取:{CacheKey}", $"{uid}-{upid}");
                }

            }
            //找不到缓存则从文件读取
            string filePath = Path.Combine(ChunksDataPath, upid.ToString(), "upload_task.json");
            if (!File.Exists(filePath))
            {
                return new DefaultMsg(1, "分片上传任务不存在或已过期", null);
            }

            var info = await File.ReadAllTextAsync(filePath);
            var jsonObj = JsonSerializer.Deserialize<UploadTaskModel>(info);
            if (jsonObj.UserId != uid)
            {
                return new DefaultMsg(1, "所属用户id不匹配", null);
            }
            _ = _icache.SetAsync("MultipartUploadTask", $"{uid}-{upid}", info, DateTimeOffset.Now.AddDays(3));
            return new DefaultMsg(0, "success", jsonObj);
        }

        /// <summary>
        /// 上传分片文件
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="uploadId"></param>
        /// <param name="requestStream"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> UploadChunkAsync(string userId, string uploadId, int chunkIndex, Stream requestStream)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!Guid.TryParse(uploadId, out Guid upid)) return new DefaultMsg(1, "uploadId格式错误", null);

            // 检测任务id是否存在
            string taskDir = Path.Combine(ChunksDataPath, upid.ToString());
            if (!Directory.Exists(taskDir))
            {
                return new DefaultMsg(1, "分片上传任务不存在或已清理", null);
            }

            string cacheKey = $"{uid}-{upid}";
            string taskFilePath = Path.Combine(taskDir, "upload_task.json");

            UploadTaskModel preCheckModel = null;
            if (await _icache.ExistsAsync("MultipartUploadTask", cacheKey))
            {
                try
                {
                    string jsonStr = await _icache.GetAsync<string>("MultipartUploadTask", cacheKey);
                    preCheckModel = JsonSerializer.Deserialize<UploadTaskModel>(jsonStr);
                }
                catch { }
            }
            else if (File.Exists(taskFilePath))
            {
                try
                {
                    preCheckModel = JsonSerializer.Deserialize<UploadTaskModel>(await File.ReadAllTextAsync(taskFilePath));
                }
                catch { }
            }

            if (preCheckModel != null)
            {
                if (preCheckModel.Status == "END")
                {
                    return new DefaultMsg(1, "该任务所有分片已上传完毕或已结束，不允许再上传", null);
                }

            }

            //构建分片文件路径
            string chunkFilePath = Path.Combine(taskDir, $"{chunkIndex}.part");

            try
            {
                // FileShare.None独占文件 防止多个流写入一个文件
                using (var fileStream = new FileStream(chunkFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await requestStream.CopyToAsync(fileStream);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "分片 {ChunkIndex} 正在被写入或发生文件锁冲突", chunkIndex);
                return new DefaultMsg(1, "该分片正在被写入，请勿重复请求", null);
            }

            // 获取写入成功的当前分片实际大小 并判断大小是否符合分片要求
            ulong currentChunkSize = (ulong)new System.IO.FileInfo(chunkFilePath).Length;
            if (currentChunkSize <= 0 || currentChunkSize > _appConfigInfo.fileSetting.FileChunkSizeBytes)
            {
                File.Delete(chunkFilePath);
                return new DefaultMsg(1, "分片上传失败，分片大小为0或超过限制", null);
            }

            // 为当前的 uploadId 获取或创建一个独立锁 防止同时写配置文件
            var asyncLock = _taskLocks.GetOrAdd(upid.ToString(), _ => new SemaphoreSlim(1, 1));

            // 等待锁10秒，如果超时则返回错误，避免长时间阻塞
            bool lockAcquired = await asyncLock.WaitAsync(TimeSpan.FromSeconds(10));
            if (!lockAcquired)
            {
                return new DefaultMsg(1, "系统繁忙，更新进度超时，请让客户端稍后重试该分片", null);
            }

            try
            {
                UploadTaskModel uploadTaskModel = null;

                // 优先尝试从缓存获取
                if (await _icache.ExistsAsync("MultipartUploadTask", cacheKey))
                {
                    try
                    {
                        string jsonStr = await _icache.GetAsync<string>("MultipartUploadTask", cacheKey);
                        uploadTaskModel = JsonSerializer.Deserialize<UploadTaskModel>(jsonStr);
                    }
                    catch (Exception)
                    {
                        _logger.LogWarning("MultipartUploadTask缓存数据反序列化失败，准备从文件获取: {CacheKey}", cacheKey);
                    }
                }

                // 缓存找不到则从文件读取
                if (uploadTaskModel is null)
                {
                    if (!File.Exists(taskFilePath)) return new DefaultMsg(1, "分片上传任务 JSON 丢失", null);

                    try
                    {
                        string fileJson = await File.ReadAllTextAsync(taskFilePath);
                        uploadTaskModel = JsonSerializer.Deserialize<UploadTaskModel>(fileJson);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "读取分片上传任务文件失败. UserId: {UserId}, UploadId: {UploadId}", userId, upid);
                        return new DefaultMsg(1, "读取分片上传任务文件失败", null);
                    }
                }

                if (uploadTaskModel.Status == "END")
                {
                    File.Delete(chunkFilePath);
                    return new DefaultMsg(1, "在您上传期间，任务已被标记为结束，该分片作废", null);
                }

                // 重复上传的分片，覆盖更新其大小和完成时间
                var existingChunk = uploadTaskModel.UploadedChunks.FirstOrDefault(c => c.Index == chunkIndex);
                if (existingChunk != null)
                {
                    // 如果分片已存在，覆盖更新其大小和完成时间
                    existingChunk.Size = (int)currentChunkSize;
                    existingChunk.CompletedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
                }
                else
                {
                    // 如果分片不存在，追加新分片进度
                    uploadTaskModel.UploadedChunks.Add(new UploadedChunkModel
                    {
                        Index = chunkIndex,
                        Size = (int)currentChunkSize,
                        CompletedAt = DateTimeOffset.Now.ToUnixTimeSeconds()
                    });
                }

                // 校验进度是否全部到齐
                bool isCompleted = uploadTaskModel.UploadedChunks.Count == uploadTaskModel.Meta.TotalChunks;
                if (isCompleted)
                {
                    uploadTaskModel.Status = "END";
                }

                string updatedJson = JsonSerializer.Serialize(uploadTaskModel);

                // 写回缓存
                await _icache.SetAsync("MultipartUploadTask", cacheKey, updatedJson, DateTimeOffset.Now.AddDays(3));

                // 写回文件
                await File.WriteAllTextAsync(taskFilePath, updatedJson);

                // 返回判断结果
                if (isCompleted)
                {

                    return new DefaultMsg(0, "所有分片上传完毕，等待合并", uploadTaskModel);
                }

                return new DefaultMsg(0, $"分片 {chunkIndex} 上传成功", uploadTaskModel);
            }
            finally
            {
                // 释放锁
                asyncLock.Release();
            }
        }
        /// <summary>
        /// 合并文件并检测hash是否一致 并入数据库
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="uploadId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> MergeFilesAsync(string userId, string uploadId, string folderId)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!Guid.TryParse(uploadId, out Guid upid)) return new DefaultMsg(1, "uploadId格式错误", null);
            if (!Guid.TryParse(folderId, out Guid fid)) return new DefaultMsg(1, "folderId格式错误", null);


            // 检测任务id是否存在
            string taskDir = Path.Combine(ChunksDataPath, upid.ToString());
            if (!Directory.Exists(taskDir))
            {
                return new DefaultMsg(1, "分片上传任务不存在或已清理", null);
            }

            // 为当前的 uploadId 获取或创建一个独立锁，防止同时触发多次合并操作
            var asyncLock = _taskLocks.GetOrAdd(upid.ToString(), _ => new SemaphoreSlim(1, 1));
            bool lockAcquired = await asyncLock.WaitAsync(TimeSpan.FromSeconds(1));
            if (!lockAcquired)
            {
                return new DefaultMsg(1, "系统繁忙，正在处理合并请求，请勿频繁点击", null);
            }

            try
            {
                UploadTaskModel uploadTaskModel = null;
                string cacheKey = $"{uid}-{upid}";
                string taskFilePath = Path.Combine(taskDir, "upload_task.json");


                if (await _icache.ExistsAsync("MultipartUploadTask", cacheKey))
                {
                    try
                    {
                        string jsonStr = await _icache.GetAsync<string>("MultipartUploadTask", cacheKey);
                        uploadTaskModel = JsonSerializer.Deserialize<UploadTaskModel>(jsonStr);
                    }
                    catch (Exception)
                    {
                        _logger.LogWarning("MultipartUploadTask缓存数据反序列化失败，准备从文件获取: {CacheKey}", cacheKey);
                    }
                }

                if (uploadTaskModel is null)
                {
                    if (!File.Exists(taskFilePath)) return new DefaultMsg(1, "分片上传任务 JSON 丢失", null);
                    try
                    {
                        string fileJson = await File.ReadAllTextAsync(taskFilePath);
                        uploadTaskModel = JsonSerializer.Deserialize<UploadTaskModel>(fileJson);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "读取分片上传任务文件失败. UserId: {UserId}, UploadId: {UploadId}", userId, upid);
                        return new DefaultMsg(1, "读取分片上传任务文件失败", null);
                    }
                }


                if (uploadTaskModel.UploadedChunks.Count != uploadTaskModel.Meta.TotalChunks)
                {
                    return new DefaultMsg(1, "分片上传未完成", null);
                }


                //检测目录是否存在
                UserFolderInfo dir;
                long fileSize;
                ImmutableArray<DefinitionMatch> type = ImmutableArray<DefinitionMatch>.Empty;
                dir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                      .FirstAsync(x => x.UserId == uid && x.Id == fid && x.IsDeleted == false);
                if (dir == null) return new DefaultMsg(1, "上传失败，目录不存在", null);

                //最终文件名
                Guid UidName = Guid.NewGuid();
                string UidFileName = UidName.ToString("N") + $".{uid.ToString("N")}";
                //临时文件路径
                string finalFilePath = Path.Combine(taskDir, UidFileName);

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920]; // 80KB 缓冲区

                try
                {
                    using (var finalStream = new FileStream(finalFilePath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true))
                    {
                        // 按照分片序号进行合并
                        for (int i = 0; i < uploadTaskModel.Meta.TotalChunks; i++)
                        {
                            string chunkFilePath = Path.Combine(taskDir, $"{i}.part");
                            if (!File.Exists(chunkFilePath))
                            {
                                return new DefaultMsg(1, $"合并失败：缺少分片文件 {i}.part", null);
                            }

                            using (var chunkStream = new FileStream(chunkFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true))
                            {
                                int bytesRead;
                                while ((bytesRead = await chunkStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    // 一边向最终大文件追加写入，一边喂给 Hash 计算器
                                    await finalStream.WriteAsync(buffer, 0, bytesRead);
                                    hasher.AppendData(buffer, 0, bytesRead);
                                }
                            }
                        }

                        //记录文件大小
                        fileSize = finalStream.Length;
                        //判断存储空间是否足够
                        var storageInfoMsg = await GetUserStorageCapacityInfo(userId);
                        if (storageInfoMsg == null)
                        {
                            return new DefaultMsg(1, "获取存储信息失败，无法上传文件", null);
                        }


                        if ((storageInfoMsg.Data as StorageCapacityInfo).FreeSpaceInBytes < (ulong)fileSize)
                        {
                            _logger.LogWarning("用户{UserId}上传失败，储存空间不足", uid);
                            return new DefaultMsg(1, "上传失败，储存空间不足", null);
                        }


                    }


                    //比对文件hash值是否和一开始前端传入的一致
                    string actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    string expectedHash = uploadTaskModel.Meta.FileHash.ToLowerInvariant();

                    if (actualHash != expectedHash)
                    {
                        // 哈希不一致，说明文件在上传或合并过程中损坏

                        Directory.Delete(taskDir, true); //删除当前整个任务文件夹
                        _ = _icache.RemoveAsync("MultipartUploadTask", cacheKey);
                        return new DefaultMsg(1, "文件完整性校验失败，Hash值不匹配，请重新上传", null);
                    }
                    // 交给 MimeDetective 进行同步类型检测
                    using (var tempReadStream =
                           new FileStream(finalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        type = _contentInspector.Inspect(tempReadStream);
                    }
                    // 得到哈希值及文件，进行业务逻辑
                    //检查目录
                    string userPath = GetFileStorageDirectory(fileSize);
                    if (!Directory.Exists(userPath))
                    {
                        Directory.CreateDirectory(userPath);
                    }

                    string fileSavePath = Path.Combine(userPath, UidFileName);
                    fi item = await CheckForDuplicateAsync(actualHash);
                    fi newItem;

                    if (item != null)
                    {
                        newItem = new fi
                        {
                            Id = UidName,
                            UserId = uid,
                            FileName = uploadTaskModel.Meta.FileName,
                            FileType = _helper.GetCategory(uploadTaskModel.Meta.FileName),
                            FolderId = dir.Id,
                            FileSizeInBytes = (ulong)fileSize,
                            StoragePath = item.StoragePath,
                            FileHash = actualHash,
                            IsDeleted = false,
                            CreationTime = DateTime.Now,
                            LastModifiedTime = DateTime.Now
                        };
                        await _sqlSugarClient.Insertable<fi>(newItem).ExecuteCommandAsync();

                        _logger.LogInformation("用户{UserId}文件查找到相同文件，SHA256:{FileHash}，大小:{FileSize}字节，物理地址:{StoragePath}",
                            userId, actualHash, fileSize, item.StoragePath);
                    }
                    else
                    {
                        newItem = new fi
                        {
                            Id = UidName,
                            UserId = uid,
                            FileType = _helper.GetCategory(uploadTaskModel.Meta.FileName),
                            FileName = uploadTaskModel.Meta.FileName,
                            FolderId = dir.Id,
                            FileSizeInBytes = (ulong)fileSize,
                            StoragePath = fileSavePath,
                            FileHash = actualHash,
                            IsDeleted = false,
                            CreationTime = DateTime.Now,
                            LastModifiedTime = DateTime.Now
                        };
                        await _sqlSugarClient.Insertable<fi>(newItem).ExecuteCommandAsync();
                        File.Move(finalFilePath, fileSavePath);
                        _logger.LogInformation("用户{UserId}文件接收合并完成，SHA256:{FileHash}，大小:{FileSize}字节，物理地址:{StoragePath}",
                            userId, actualHash, fileSize, fileSavePath);


                    }

                    if (type.Any())
                    {
                        var result = type.First();
                        string mimeType = result.Definition.File.MimeType; // 获取类型 例如: "image/jpeg"
                        _logger.LogInformation("检测到用户 {UserId} 上传的类型类型:{MimeType}", userId, mimeType);
                        //图片类消息队列
                        if (mimeType.StartsWith("image/"))
                        {
                            var imgComp = new ImgCompModel()
                            { FileHash = newItem.FileHash, StoragePath = newItem.StoragePath };
                            var imgVector = new ImgVectorModel()
                            { DbType = _sqlSugarClient.CurrentConnectionConfig.DbType, FileId = newItem.Id, StoragePath = newItem.StoragePath, UserId = newItem.UserId };
                            //缩略图
                            _ = _messageBus.PublishAsync("ImgComp", imgComp);
                            //向量计算
                            if (_appConfigInfo.aiSetting.Enable)
                                _ = _messageBus.PublishAsync("ImgVector", imgVector);
                        }
                    }

                    if (Helper.TextExtensions.Contains(Path.GetExtension(newItem.FileName)) || Helper.DocExtensions.Contains(Path.GetExtension(newItem.FileName)))
                    {
                        var textVector = new TextVectorModel()
                        { DbType = _sqlSugarClient.CurrentConnectionConfig.DbType, FileId = newItem.Id, StoragePath = newItem.StoragePath, UserId = newItem.UserId, FileName = newItem.FileName };
                        _messageBus.PublishAsync("TextVector", textVector);
                    }
                    //清除缓存
                    _ = _icache.RemoveAsync("FolderVersion", $"{userId}:{fid}");
                    _ = _icache.RemoveAsync("UserStorageCapacityInfo", userId);
                    _ = _icache.RemoveAsync("UserInfoCache", userId);
                    _ = _icache.RemoveAsync("MultipartUploadTask", cacheKey);
                    _ = ClearSearchFilesCache(uid);
                    Directory.Delete(taskDir, true);
                    return new DefaultMsg(0, "上传成功", UidName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "合并分片文件时发生系统异常. UploadId: {UploadId}", upid);
                    return new DefaultMsg(1, "合并分片时发生服务器异常,请联系管理员", null);
                }
            }
            finally
            {
                // 绝对保证释放锁
                asyncLock.Release();
            }
        }
        /// <summary>
        /// 批量删除文件
        /// </summary>
        /// <param name="userId">用户ID。</param>
        /// <param name="fileIds">要删除的文件的ID。</param>
        public async Task<DefaultMsg> DeleteFileAsync(string userId, string[] fileIds, bool isAdmin = false)
        {
            // 参数校验
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            var guidFileIds = fileIds.Select(idStr => Guid.TryParse(idStr, out var guid) ? guid : Guid.Empty)
                .Where(guid => guid != Guid.Empty).ToList();
            if (guidFileIds.Count() == 0)
            {
                return new DefaultMsg(1, "fileIds错误", null);
            }

            List<FileDeletionItem> targetItems;
            //管理员调用删除
            if (isAdmin)
            {
                targetItems = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => x.UserId == uid && guidFileIds.Contains(x.Id))
                    .Select(x => new FileDeletionItem()
                    { FileId = x.Id, StoragePath = x.StoragePath, FileHash = x.FileHash }).ToListAsync();
            }
            else
            {
                targetItems = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => x.UserId == uid && guidFileIds.Contains(x.Id) && !x.IsDeleted)
                    .Select(x => new FileDeletionItem()
                    { FileId = x.Id, StoragePath = x.StoragePath, FileHash = x.FileHash }).ToListAsync();
            }

            if (targetItems.Count == 0)
            {
                _logger.LogWarning("用户{UserId}请求删除文件，但在数据库中未匹配到有效记录。", uid);
                return new DefaultMsg(1, "删除失败,文件不存在", null);
            }

            var fileId = targetItems.Select(x => x.FileId).ToList();
            int updateResult = 0;

            // 3. 执行删除操作
            if (isAdmin)
            {
                //查出要硬删的分享记录ID
                var adminShareIdsToClear = await _sqlSugarClient.Queryable<FileShareInfo>()
                    .Where(x => fileId.Contains(x.ShareFileId))
                    .Select(x => x.Id)
                    .ToListAsync();

                //硬删文件
                updateResult = await _sqlSugarClient.Deleteable<Models.FileInfo>()
                    .Where(x => fileId.Contains(x.Id))
                    .ExecuteCommandAsync();

                //硬删分享记录
                if (adminShareIdsToClear.Any())
                {
                    await _sqlSugarClient.Deleteable<FileShareInfo>()
                        .Where(x => fileId.Contains(x.ShareFileId))
                        .ExecuteCommandAsync();

                    //清理被硬删的分享链接缓存
                    foreach (var sid in adminShareIdsToClear)
                    {
                        await _icache.RemoveAsync("FileShareInfo", $"{sid}");
                    }
                }
            }
            else
            {
                //软删文件
                updateResult = await _sqlSugarClient.Updateable<fi>()
                    .SetColumns(it => new fi { IsDeleted = true, LastModifiedTime = DateTime.Now })
                    .Where(it => fileId.Contains(it.Id) && it.UserId == uid)
                    .ExecuteCommandAsync();

                // 普通用户软删文件时，连带软删分享记录
                var shareIds = await _sqlSugarClient.Queryable<FileShareInfo>()
                    .Where(x => fileId.Contains(x.ShareFileId) && !x.IsDeleted)
                    .Select(x => x.Id)
                    .ToListAsync();

                if (shareIds.Any())
                {
                    // 软删分享记录
                    await _sqlSugarClient.Updateable<FileShareInfo>()
                        .SetColumns(x => new FileShareInfo { IsDeleted = true })
                        .Where(x => shareIds.Contains(x.Id))
                        .ExecuteCommandAsync();

                    foreach (var sid in shareIds)
                    {
                        await _icache.RemoveAsync("FileShareInfo", $"{sid}");
                    }
                }
            }

            if (updateResult == 0)
            {
                _logger.LogWarning("用户{UserId}删除文件失败，更新影响的行数为0。", uid);
                return new DefaultMsg(1, "删除失败,文件不存在", null);
            }

            //尝试删除相关的向量信息
            if (_appConfigInfo.aiSetting.Enable)
            {
                await _sqlSugarClient.Deleteable<ImgVectorcs>()
                    .In(fileId)
                    .ExecuteCommandAsync();

                await _sqlSugarClient.Deleteable<TextVectorcs>()
                .Where(x => fileId.Contains(x.FileId))
                .ExecuteCommandAsync();
            }

            // 处理物理文件删除
            if (!_appConfigInfo.fileSetting.IsSoftDelete || isAdmin)
            {
                await HandlePhysicalFilesDeletionAsync(targetItems, uid);
            }

            //清除缓存
            _icache.RemoveAsync("UserStorageCapacityInfo", userId);
            ClearSearchFilesCache(uid);
            _icache.RemoveAsync("ShareInfoPrivate", $"{uid}");
            await ClearCacheForFile(uid, fileId);

            if (isAdmin)
            {
                _logger.LogInformation("管理员操作用户{UserId}批量删除文件成功，共删除 {Count} 个文件。", uid, updateResult);
            }
            else
            {
                _logger.LogInformation("用户{UserId}批量删除文件成功，共删除 {Count} 个文件。", uid, updateResult);
            }

            return new DefaultMsg(0, "删除成功", null);
        }


        /// <summary>
        /// 批量安全地处理物理文件的删除，检查是否存在其他引用。
        /// </summary>
        /// <param name="itemsToDelete">待删除的文件信息列表</param>
        /// <param name="uid">用户ID（用于日志）</param>
        private async Task HandlePhysicalFilesDeletionAsync(List<FileDeletionItem> itemsToDelete, Guid uid)
        {
            if (itemsToDelete == null || !itemsToDelete.Any()) return;

            // 提取关键信息用于批量查询
            var allHashes = itemsToDelete
                .Where(x => !string.IsNullOrEmpty(x.FileHash))
                .Select(x => x.FileHash)
                .Distinct()
                .ToList();

            var allPaths = itemsToDelete
                .Select(x => x.StoragePath)
                .Distinct()
                .ToList();

            var deletedIds = itemsToDelete.Select(x => x.FileId).ToList();

            // 批量查询数据库：查找“仍然有效且被引用”的文件
            // 需要找出哪些 Hash 或 Path 在数据库中其他的转存文件记录中仍然被引用（即 IsDeleted = false 的记录），以避免误删共享文件。
            var referencedHashes = new HashSet<string>();
            var referencedPaths = new HashSet<string>();

            // 检查 Hash 引用
            if (allHashes.Any())
            {
                var activeHashes = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => !x.IsDeleted && allHashes.Contains(x.FileHash)) // 查活着的且在列表中的
                    .Where(x => !deletedIds.Contains(x.Id)) // 排除掉本次我们要删的这些记录
                    .Select(x => x.FileHash)
                    .Distinct()
                    .ToListAsync();

                foreach (var h in activeHashes) referencedHashes.Add(h!);
            }

            // 检查 Path 引用 (仅针对没有 Hash 的情况，或者作为兜底)
            if (allPaths.Any())
            {
                var activePaths = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => !x.IsDeleted && allPaths.Contains(x.StoragePath))
                    .Where(x => !deletedIds.Contains(x.Id))
                    .Select(x => x.StoragePath)
                    .Distinct()
                    .ToListAsync();

                foreach (var p in activePaths) referencedPaths.Add(p);
            }

            // 遍历待删除列表，执行物理删除
            foreach (var item in itemsToDelete)
            {
                bool isReferenced = false;

                // 检查 Hash 引用
                if (!string.IsNullOrEmpty(item.FileHash))
                {
                    if (referencedHashes.Contains(item.FileHash))
                    {
                        isReferenced = true;
                    }
                }

                // 如果没有 Hash 或 Hash 没被引用，再检查路径引用
                if (!isReferenced && referencedPaths.Contains(item.StoragePath))
                {
                    isReferenced = true;
                }

                // 执行删除或跳过
                if (!isReferenced)
                {
                    try
                    {
                        _logger.LogInformation("用户{UserId}文件 '{StoragePath}' 已无任何引用，准备进行物理删除。", uid, item.StoragePath);
                        if (File.Exists(item.StoragePath))
                        {
                            File.Delete(item.StoragePath);
                            File.Delete(Path.Combine(_env.WebRootPath, "driveassets/imgcomp", item.FileHash + ".jpg"));
                            _logger.LogInformation("用户{UserId}物理文件 '{StoragePath}' 已成功删除。", uid, item.StoragePath);
                        }
                        else
                        {
                            // 只有当 Hash 也没有其他引用时，文件不存在才是一个 Warning，否则可能是正常的共享文件被删
                            _logger.LogWarning("用户{UserId}尝试物理删除文件 '{StoragePath}'，但文件已不存在。", uid, item.StoragePath);
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "用户{UserId}物理删除文件 '{StoragePath}' 失败：{ErrorMessage}", uid,
                            item.StoragePath, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "用户{UserId}物理删除文件 '{StoragePath}' 发生未知错误：{ErrorMessage}", uid,
                            item.StoragePath, ex.Message);
                    }
                }
                else
                {
                    _logger.LogInformation("用户{UserId}文件 '{StoragePath}' (Hash: {FileHash}) 仍被其他记录引用，跳过物理删除。", uid,
                        item.StoragePath, item.FileHash);
                }
            }
        }


        /// <summary>
        /// 直连下载文件
        /// </summary>
        /// <returns></returns>
        public async Task<FileStreamInfo> DirectLinkDownLoadFile(string fileId)
        {
            if (!Guid.TryParse(fileId, out Guid fid))
            {
                _logger.LogWarning("下载失败：无效的 fileId '{FileId}' 。", fileId);
                return null;
            }

            //获取文件信息
            FileDownLoadInfo fileInfo = null;
            try
            {
                //使用缓存
                fileInfo = JsonSerializer.Deserialize<FileDownLoadInfo>(
                    await _icache.GetAsync<string>("FileDownLoadInfo", $"{fid}"));
                _logger.LogDebug("文件id {FileId} 命中 FileDownLoadInfo 缓存", fileId);
            }
            catch (Exception)
            {
                fileInfo = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => x.Id == fid && x.IsDeleted == false)
                    .Select(x => new FileDownLoadInfo()
                    {
                        FileId = x.Id,
                        UserId = x.UserId,
                        Name = x.FileName,
                        SizeInBytes = x.FileSizeInBytes,
                        StoragePath = x.StoragePath
                    }).FirstAsync();
                _logger.LogDebug("文件id {FileId} 未命中 FileDownLoadInfo 缓存，从数据库获取", fileId);
                if (fileInfo != null)
                {
                    _icache.SetAsync("FileDownLoadInfo", $"{fid}", JsonSerializer.Serialize(fileInfo),
                        DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
                }
            }

            if (fileInfo == null)
            {
                _logger.LogWarning("下载失败：文件id '{FileId}' 不存在。", fileId);
                return null;
            }

            FileStream stream = null;

            //获取用户偏好设置
            UserPreferences userPreferences = null;
            try
            {
                //使用缓存
                userPreferences =
                    JsonSerializer.Deserialize<UserPreferences>(
                        await _icache.GetAsync<string>("UserPreferences", $"{fileInfo.UserId}"));
                _logger.LogDebug("用户偏好 {UserId}，命中缓存", fileInfo.UserId);
            }
            catch (Exception)
            {
                userPreferences = await _sqlSugarClient.Queryable<User>()
                    .Where(x => x.UserId == fileInfo.UserId)
                    .Select(x => x.Preferences).FirstAsync();
                _logger.LogDebug("用户偏好 {UserId} 未命中缓存，从数据库获取", fileInfo.UserId);
                _icache.SetAsync("UserPreferences", $"{fileInfo.UserId}", JsonSerializer.Serialize(userPreferences),
                    DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            }

            if (userPreferences == null)
            {
                _logger.LogWarning("用户 {UserId} 的偏好设置获取失败", fileInfo.UserId);
                return null;
            }

            //是否开启直连下载
            if (userPreferences.IsDirectLinkEnabled)
            {
                stream = new FileStream(fileInfo.StoragePath, FileMode.Open, FileAccess.Read);
                return new FileStreamInfo(stream, fileInfo.Name);
            }
            else
            {
                _logger.LogWarning("下载失败：文件所有者 {UserId} 未开启直连下载。请求的文件ID: {FileId}", fileInfo.UserId, fileId);
                return null;
            }
        }


        /// <summary>
        /// 移动文件或文件夹位置
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> MoveFileOrDir(string userId, FileOrDirMoveInfo fileOrDirMoveInfo)
        {
            if (!Guid.TryParse(fileOrDirMoveInfo.NewFolderId, out Guid foid))
            {
                return new DefaultMsg(1, "folderId格式错误", null);
            }

            Guid.TryParse(userId, out Guid uid);
            //使用缓存
            UserFilesInfo cacheFileInfo = null;
            UserFolderInfo dir;
            List<string> cacheFilePath = null;

            //查询新目录信息是否存在
            dir = await _sqlSugarClient.Queryable<UserFolderInfo>()
                .FirstAsync(x => x.UserId == uid && x.Id == foid && x.IsDeleted == false);


            if (dir == null)
            {
                _logger.LogWarning("移动失败，目标新目录不存在。UserId: {UserId}, TargetFolderId: {TargetFolderId}", userId, foid);
                return new DefaultMsg(1, "移动失败，新目录不存在", null);
            }

            //检测文件id是否是空
            if (fileOrDirMoveInfo.FileIds != null && fileOrDirMoveInfo.FileIds.Length != 0)
            {
                //转换类型
                var validGuids = fileOrDirMoveInfo.FileIds
                    .Select(idStr => Guid.TryParse(idStr, out var guid) ? guid : Guid.Empty)
                    .Where(guid => guid != Guid.Empty).ToList();
                if (validGuids.Count == 0) return new DefaultMsg(1, "文件移动失败，文件id全部无效", null);
                await ClearCacheForFile(uid, validGuids); //不等待 以免阻塞  清除缓存不然会导致读取到旧数据
                // 执行批量更新
                var effectRows = await _sqlSugarClient.Updateable<fi>()
                    .SetColumns(it => it.FolderId == dir.Id) // 设置要更新的列
                    .Where(it => it.UserId == uid && validGuids.Contains(it.Id) && it.IsDeleted == false) // 指定要更新的行
                    .ExecuteCommandAsync();
                if (effectRows == 0 && validGuids.Count > 0) // 如果oldPaths不为空但更新行数为0，则可能因为数据库中找不到匹配项
                {
                    _logger.LogWarning("文件移动失败，数据库中未找到匹配的文件记录。UserId: {UserId}, TargetFolderId: {TargetFolderId}",
                        userId, dir.Id);
                    return new DefaultMsg(1, "要移动的文件不存在", null);
                }
                else if (effectRows == 0 && validGuids.Count == 0) // 如果oldPaths为空，且更新行数为0，说明本来就没有要移动的
                {
                    return new DefaultMsg(1, "没有合法的文件id。", null); // 或者更友好的提示
                }

                _logger.LogInformation("用户{UserId}成功移动 {Count} 个文件到目录 {TargetFolderId}", userId, effectRows, dir.Id);
            }

            if (fileOrDirMoveInfo.FolderIds != null && fileOrDirMoveInfo.FolderIds.Length != 0)
            {
                var ValidateFolder = fileOrDirMoveInfo.FolderIds
                    .Select(idStr => Guid.TryParse(idStr, out var guid) ? guid : Guid.Empty)
                    .Where(guid => guid != Guid.Empty);
                //检测合法性  防止把父目录移入子目录内造成数据结构混乱
                ValidateFolder = ValidateFolder.Where(item =>
                {
                    return ValidateFolderMove(item, foid, uid) == 0 && item != foid;
                });


                await ClearCacheForFolder(uid, ValidateFolder.ToList()); //不等待 以免阻塞  清除缓存不然会导致读取到旧数据
                // 执行批量更新

                var effectRows = await _sqlSugarClient.Updateable<UserFolderInfo>()
                    .SetColumns(it => it.ParentId == foid) // 设置要更新的列
                    .Where(it => it.UserId == uid && ValidateFolder.Contains(it.Id) && it.IsDeleted == false) // 指定要更新的行
                    .ExecuteCommandAsync();
                if (effectRows == 0 && ValidateFolder.Count() > 0) // 如果oldPaths不为空但更新行数为0，则可能因为数据库中找不到匹配项
                {
                    await _icache.RemoveAsync("FolderVersion", $"{userId}:{foid}");
                    _logger.LogWarning(
                        "文件夹移动失败，目标文件夹不存在或触发了防环校验(防止父移入子)。UserId: {UserId}, TargetFolderId: {TargetFolderId}", userId,
                        foid);
                    return new DefaultMsg(1, "要移动的文件夹不存在或存在把父目录移入子目录内操作！", null);
                }
                else if (effectRows == 0 && ValidateFolder.Count() == 0) // 如果oldPaths为空，且更新行数为0，说明本来就没有要移动的
                {
                    return new DefaultMsg(1, "没有合法的文件夹路径可以移动。", null); // 或者更友好的提示
                }

                _logger.LogInformation("用户{UserId}成功移动 {Count} 个文件夹到目录 {TargetFolderId}", userId, effectRows, foid);
            }

            //清除缓存
            await _icache.RemoveAsync("FolderVersion", $"{userId}:{foid}");
            await _icache.RemoveAsync("FileDirectoryWebDAV", $"{userId}:{foid}");

            ClearSearchFilesCache(uid);

            return new DefaultMsg(0, "移动成功", null);
        }

        /// <summary>
        /// 检测移动的文件夹是否存在父子关系，防止把父文件夹移动到自己的子文件夹内部，造成数据结构混乱。
        /// </summary>
        /// <param name="sourceId">当前要移动的文件夹 ID </param>
        /// <param name="targetParentId">目标父文件夹 ID </param>
        public int ValidateFolderMove(Guid sourceId, Guid targetParentId, Guid uid)
        {
            //核心防环校验：使用 SqlSugar 执行向上递归的 CTE SQL
            //注意：如果你用的是 SQL Server，把 WITH RECURSIVE 改成 WITH 即可。MySQL 8.0+ 和 PostgreSQL 保持原样 这里我已经在代码里动态判断数据库类型了
            //获取当前数据库类型
            DbType dbType = _sqlSugarClient.CurrentConnectionConfig.DbType;

            //根据数据库类型，动态决定是否添加 RECURSIVE 关键字
            string cteKeyword = (dbType == DbType.SqlServer) ? "WITH" : "WITH RECURSIVE";

            //拼接最终的 SQL
            string sql = $@"
    {cteKeyword} Ancestors AS (
        SELECT id, parent_id 
        FROM folder_info 
        WHERE id = @TargetId AND user_id = @UserId
        
        UNION ALL
        
        SELECT f.id, f.parent_id 
        FROM folder_info f
        INNER JOIN Ancestors a ON f.id = a.parent_id
        WHERE f.user_id = @UserId 
          AND f.id <> f.parent_id
    )
    SELECT COUNT(1) FROM Ancestors WHERE id = @SourceId;
";


            //使用 db.Ado.GetInt 执行并传入参数，防止 SQL 注入
            int count = _sqlSugarClient.Ado.GetInt(sql,
                new { TargetId = targetParentId, SourceId = sourceId, UserId = uid });
            return count; //如果 count > 0，说明目标父文件夹在当前文件夹的祖先链上，移动不合法；如果 count = 0，说明没有父子关系，可以安全移动。
        }

        /// <summary>
        /// 查询文件路径并清除缓存
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="oldPaths"></param>
        /// <returns></returns>
        private async Task ClearCacheForFile(Guid uid, List<Guid> fileIds)
        {
            //查询缓存
            var cacheTasks = fileIds.Select(async id =>
            {
                Guid.TryParse(await _icache.GetAsync<string>("FileIdToFolderId", $"{uid}:{id}"), out Guid pid);
                return pid == Guid.Empty ? null : new { Id = id, FolderId = pid };
            });

            var cacheResults = await Task.WhenAll(cacheTasks);
            var cachedFiles = cacheResults.Where(x => x != null).ToList();

            // 准备数据：找出未命中的 ID
            var cachedFileIds = cachedFiles.Select(x => x.Id).ToHashSet();
            var missingFileIds = fileIds.Where(id => !cachedFileIds.Contains(id)).ToList();

            //查库补全数据
            var allFilesInfo = cachedFiles.ToList();

            if (missingFileIds.Any())
            {
                var dbInfos = await _sqlSugarClient.Queryable<fi>()
                    .Where(file => file.UserId == uid && missingFileIds.Contains(file.Id))
                    .Select(x => new { Id = x.Id, x.FolderId })
                    .ToListAsync();

                allFilesInfo.AddRange(dbInfos);
            }


            //更新缓存
            foreach (var item in allFilesInfo)
            {
                //_icache.RemoveAsync("FileDirectory", $"{userId}:{foid}");
                //_icache.RemoveAsync("UserStorageCapacityInfo", userId);
                _ = _icache.RemoveAsync("UserFileInfo", $"{uid}:{item.Id}");
                _icache.RemoveAsync("FileDownLoadInfo", $"{item.Id}");
                _icache.RemoveAsync("FolderVersion", $"{uid}:{item.FolderId}");
                _icache.RemoveAsync("FileDirectoryWebDAV", $"{uid}:{item.FolderId}");
                // _icache.SetAsync("FileIdToFolderId", $"{uid}:{item.Id}", $"{item.FolderId}", DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            }

            // 方法在这里直接结束，无需等待上面成百上千个 Redis 写操作完成
        }

        /// <summary>
        /// 查询文件夹路径并清除缓存
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="folderId"></param>
        /// <returns></returns>
        private async Task ClearCacheForFolder(Guid uid, List<Guid> folderId)
        {
            //查询缓存
            var cacheTasks = folderId.Select(async id =>
            {
                if (await _icache.ExistsAsync("FolderIdToPFolder", $"{uid}:{id}"))
                {
                    var parentId = await _icache.GetAsync<string>("FolderIdToPFolder", $"{uid}:{id}");
                    Guid.TryParse(parentId, out Guid pid);
                    return new { Id = id, ParentId = pid };
                }
                else
                {
                    return null;
                }
            });

            var cacheResults = await Task.WhenAll(cacheTasks);
            var cachedFiles = cacheResults.Where(x => x != null).ToList();

            // 准备数据：找出未命中的 ID
            var cachedFolderIds = cachedFiles.Select(x => x.Id).ToHashSet();
            var missingFolderIds = folderId.Where(id => !cachedFolderIds.Contains(id)).ToList();

            //查库补全数据
            var allFilesInfo = cachedFiles.ToList();
            if (missingFolderIds.Any())
            {
                var dbInfos = await _sqlSugarClient.Queryable<UserFolderInfo>()
                    .Where(x => x.UserId == uid && missingFolderIds.Contains(x.Id))
                    .Select(x => new { x.Id, x.ParentId })
                    .ToListAsync();

                allFilesInfo.AddRange(dbInfos);
            }

            //更新缓存
            foreach (var item in allFilesInfo)
            {
                string path = await _icache.GetAsync<string>("FolderIdToFullPath", $"{uid}:{item.Id}");
                _icache.RemoveAsync("FolderPathCache", $"{uid}:{item.Id}");
                _icache.RemoveAsync("FullPathToFolderId", $"{uid}:{path}");
                _icache.RemoveAsync("FolderIdToFullPath", $"{uid}:{item.Id}");
                _icache.RemoveAsync("FolderVersion", $"{uid}:{item.ParentId}");
                _icache.RemoveAsync("FileDirectoryWebDAV", $"{uid}:{item.ParentId}");
                //_icache.SetAsync("FolderIdToPFolder", $"{uid}:{item.Id}", item.ParentId, DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            }
        }

        /// <summary>
        /// 修改文件或文件夹名
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> RenameFileOrDir(string userId, FileOrDirReNameInfo fileReNameInfo)
        {
            Guid.TryParse(userId, out Guid uid);
            int effectRows = 0;
            if (!_helper.NameValidation(fileReNameInfo.NewName)) return new DefaultMsg(1, "名称不合法", null);

            if (fileReNameInfo.Type == 1 && Guid.TryParse(fileReNameInfo.Id, out Guid folderId))
            {
                _logger.LogInformation($"用户{userId}重命名文件夹id:{fileReNameInfo.Id}，新名称:{fileReNameInfo.NewName}");
                effectRows = await _sqlSugarClient.Updateable<UserFolderInfo>()
                    .SetColumns(it => it.FolderName == fileReNameInfo.NewName)
                    .Where(it => it.UserId == uid && it.Id == folderId && it.IsDeleted == false).ExecuteCommandAsync();
                await ClearCacheForFolder(uid, new List<Guid>() { folderId });
            }

            if (fileReNameInfo.Type == 0 && Guid.TryParse(fileReNameInfo.Id, out Guid fileId))
            {
                _logger.LogInformation($"用户{userId}重命名文件id:{fileReNameInfo.Id}，新名称:{fileReNameInfo.NewName}");
                effectRows = await _sqlSugarClient.Updateable<fi>()
                    .SetColumns(it => new fi()
                    {
                        FileName = fileReNameInfo.NewName,
                        LastModifiedTime = DateTime.Now
                    }).Where(it => it.UserId == uid && it.Id == fileId && it.IsDeleted == false).ExecuteCommandAsync();
                //获取删除的文件生成的分享id缓存
                _icache.RemoveAsync("FileShareInfo", $"{await _sqlSugarClient.Queryable<FileShareInfo>()
                    .Where(it => it.UserId == uid && fileId == it.ShareFileId && it.IsDeleted == false)
                    .Select(it => it.Id)
                    .FirstAsync()}");
                await ClearCacheForFile(uid, new List<Guid>() { fileId });
            }

            if (effectRows == 0)
            {
                _logger.LogWarning($"用户{userId}重命名文件夹id:{fileReNameInfo.Id}，新名称:{fileReNameInfo.NewName},失败！");
                return new DefaultMsg(1, "重命名失败，文件或文件夹不存在", null);
            }

            ClearSearchFilesCache(uid);
            //清除分享文件信息缓存
            _icache.RemoveAsync("ShareInfoPrivate", $"{uid}");
            return new DefaultMsg(0, "修改成功！", null);
        }

        /// <summary>
        /// 得到云盘信息
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> GetCloudInfo()
        {
            return new DefaultMsg(0, "success", new
            {
                Name = _appConfigInfo.basicInformation.ProjectName,
                _appConfigInfo.fileSetting.MaxFileSize,
                _appConfigInfo.fileSetting.FileChunkSizeBytes
            });
        }

        /// <summary>
        /// 创建文件夹
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> CreateFolder(string userId, string folderId, string name)
        {
            //基本校验
            if (!Guid.TryParse(userId, out Guid uid))
            {
                return new DefaultMsg(1, "uid格式错误", null);
            }

            if (!Guid.TryParse(folderId, out Guid fid))
            {
                return new DefaultMsg(1, "父文件夹id格式错误", null);
            }

            if (!_helper.NameValidation(name)) return new DefaultMsg(1, "文件夹名称不合法", null);

            var fatherFolder = await _sqlSugarClient.Queryable<UserFolderInfo>()
                .FirstAsync(x => x.UserId == uid && x.Id == fid);
            if (fatherFolder == null)
            {
                _logger.LogWarning(
                    "创建文件夹失败，上级目录不存在。UserId: {UserId}, ParentFolderId: {ParentFolderId}, FolderName: {FolderName}",
                    userId, folderId, name);
                return new DefaultMsg(1, "上级目录不存在", null);
            }

            //判断新目录是否存在
            var existFolder = await _sqlSugarClient.Queryable<UserFolderInfo>()
                .FirstAsync(x =>
                    x.UserId == uid && x.FolderName == name && x.ParentId == fatherFolder.Id && x.IsDeleted == false);
            if (existFolder != null)
            {
                _logger.LogDebug(
                    "尝试创建的文件夹已存在，直接返回现有ID。UserId: {UserId}, ParentFolderId: {ParentFolderId}, FolderName: {FolderName}",
                    userId, folderId, name);
                return new DefaultMsg(0, "文件夹已存在", existFolder.Id);
            }

            UserFolderInfo newFolder = new UserFolderInfo()
            {
                Id = Guid.NewGuid(),
                ParentId = fid,
                UserId = uid,
                FolderName = name,
                CreationTime = DateTime.Now,
                IsDeleted = false
            };
            int r = await _sqlSugarClient.Insertable<UserFolderInfo>(newFolder).ExecuteCommandAsync();
            if (r == 0)
            {
                _logger.LogWarning(
                    "创建文件夹失败，数据库插入受影响行数为0。UserId: {UserId}, ParentFolderId: {ParentFolderId}, FolderName: {FolderName}",
                    userId, folderId, name);
                return new DefaultMsg(1, "创建文件夹失败", null);
            }

            //清除缓存
            _icache.RemoveAsync("FolderVersion", $"{userId}:{fatherFolder.Id}");
            _icache.RemoveAsync("FileDirectoryWebDAV", $"{userId}:{fatherFolder.Id}");
            _logger.LogInformation(
                "成功创建新文件夹。UserId: {UserId}, ParentFolderId: {ParentFolderId}, FolderName: {FolderName}, NewFolderId: {NewFolderId}",
                userId, folderId, name, newFolder.Id);

            return new DefaultMsg(0, "创建文件夹成功", newFolder.Id);
        }

        /// <summary>
        /// 获取用户根目录id
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetFolderRoot(string userId)
        {
            Guid.TryParse(userId, out Guid uid);
            //使用缓存
            if (!await _icache.ExistsAsync("UserInfoCache", $"{uid}"))
            {
                //查询用户信息
                User u = await _sqlSugarClient.Queryable<User>().FirstAsync(x => x.UserId == uid);
                if (u != null)
                {
                    ShowUserInfo json = new ShowUserInfo(
                        u.UserId,
                        u.Username,
                        u.Nickname,
                        u.AvatarUrl,
                        u.CreatedAt,
                        u.RootFolderId,
                        u.Email,
                        u.Preferences,
                        u.Status
                    );
                    //设置缓存
                    await _icache.SetAsync("UserPreferences", $"{uid}", JsonSerializer.Serialize(json.Preferences),
                        DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
                    await _icache.SetAsync("UserInfoCache", $"{uid}", JsonSerializer.Serialize(json),
                        DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
                    return new DefaultMsg(0, "success", u.RootFolderId);
                }

                return new DefaultMsg(1, "用户不存在", null);
            }
            else
            {
                string froot = JsonSerializer
                    .Deserialize<ShowUserInfo>(await _icache.GetAsync<string>("UserInfoCache", $"{uid}")).RootFolderId
                    .toString();
                return new DefaultMsg(0, "success", froot);
            }
        }

        /// <summary>
        /// 根据路径获取文件夹id
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="RootPathId">根路径id</param>
        /// <param name="fullPath"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetFolderByPathStrict(string userId, string RootPathId, string fullPath)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!Guid.TryParse(RootPathId, out Guid fid)) return new DefaultMsg(1, "文件夹格式错误", null);
            if (!fullPath.EndsWith('/'))
            {
                fullPath += '/';
            }

            //使用缓存
            string targetFolderId = await _icache.GetAsync<string>("FullPathToFolderId", $"{uid}:{fullPath}");
            if (!string.IsNullOrWhiteSpace(targetFolderId))
            {
                return new DefaultMsg(0, "cache", targetFolderId);
            }

            // 1. 拆分路径，例如: ["test", "我的文件", "测试"]
            var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return new DefaultMsg(1, "路径格式错误", null);
            ;
            // 2.一次性查出所有名字匹配的文件夹
            var candidates = _sqlSugarClient.Queryable<UserFolderInfo>()
                .Where(f => f.UserId == uid && segments.Contains(f.FolderName) && f.IsDeleted == false)
                .ToList();

            // 3. 在内存中进行父子关系验证
            Guid? currentParentId = fid;

            UserFolderInfo targetFolder = null;

            foreach (var segmentName in segments)
            {
                // 在候选列表中查找：名字匹配 且 父ID匹配
                var match = candidates.FirstOrDefault(f =>
                    f.FolderName == segmentName && f.ParentId == currentParentId.GetValueOrDefault());

                if (match == null)
                {
                    // 路径中断找不到中间某个文件夹，说明路径无效
                    return new DefaultMsg(1, "路径不存在", null);
                    ;
                }

                // 指向下一级
                currentParentId = match.Id;
                targetFolder = match;
            }

            //设置缓存
            //根据路径得到文件夹id
            _icache.SetAsync("FullPathToFolderId", $"{uid}:{fullPath}", targetFolder.Id.toString(),
                DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            //根据文件夹id得到路径
            _icache.SetAsync("FolderIdToFullPath", $"{uid}:{targetFolder.Id}", fullPath,
                DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            return new DefaultMsg(0, "success", targetFolder.Id);
        }


        /// <summary>
        /// 批量删除文件夹
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="folderIds"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> DeleteUserFolderAsync(string userId, string[] folderIds)
        {
            // 参数校验
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);

            var guidFolderIds = folderIds
                .Select(idStr => Guid.TryParse(idStr, out var guid) ? guid : Guid.Empty)
                .Where(guid => guid != Guid.Empty)
                .ToList();

            if (guidFolderIds.Count == 0)
            {
                return new DefaultMsg(1, "folderIds错误", null);
            }

            try
            {
                // 开启 SqlSugar 异步事务
                var result = await _sqlSugarClient.AsTenant().UseTranAsync(async () =>
                {
                    //使用 CTE 递归获取所有待删除的文件夹 ID ---

                    var initialIdsString = string.Join(",", guidFolderIds.Select(id => $"'{id}'"));
                    DbType dbType = _sqlSugarClient.CurrentConnectionConfig.DbType;
                    //根据数据库类型，动态决定是否添加 RECURSIVE 关键字
                    string cteKeyword = (dbType == DbType.SqlServer) ? "WITH" : "WITH RECURSIVE";
                    // 构造递归查询 SQL
                    string cteSql = $@"
                {cteKeyword} folder_tree AS (
                    SELECT ""id"" 
                    FROM ""folder_info"" 
                    WHERE ""user_id"" = '{uid}' AND ""is_deleted"" = false AND ""id"" IN ({initialIdsString})
                    
                    UNION ALL
                    
                    SELECT f.""id"" 
                    FROM ""folder_info"" f
                    INNER JOIN folder_tree ft ON f.""parent_id"" = ft.""id""
                    WHERE f.""user_id"" = '{uid}' AND f.""is_deleted"" = false
                )
                SELECT ""id"" FROM folder_tree;
            ";

                    // 一次性拿回这棵树上所有的文件夹 ID
                    var allFolderIdsToDelete = await _sqlSugarClient.Ado.SqlQueryAsync<Guid>(cteSql);

                    if (allFolderIdsToDelete.Any())
                    {
                        // 获取这些文件夹下的所有文件 ID，用于物理删除
                        var fileIdsToDelete = await _sqlSugarClient.Queryable<fi>()
                            .Where(it =>
                                it.UserId == uid && allFolderIdsToDelete.Contains(it.FolderId) && it.IsDeleted == false)
                            .Select(it => it.Id)
                            .ToListAsync();

                        // 如果有文件，调用文件删除方法处理物理存储 
                        if (fileIdsToDelete.Any() && _appConfigInfo.fileSetting.IsSoftDelete == false)
                        {
                            var stringFileIds = fileIdsToDelete.Select(id => id.ToString()).ToArray();
                            // 调用文件系统的删除方法
                            await DeleteFileAsync(userId, stringFileIds);
                        }

                        // 批量软删除数据库记录
                        // 软删除所有关联的文件记录
                        await _sqlSugarClient.Updateable<fi>()
                            .SetColumns(it => it.IsDeleted == true)
                            .Where(it => it.UserId == uid && allFolderIdsToDelete.Contains(it.FolderId))
                            .ExecuteCommandAsync();

                        // 软删除所有的文件夹记录
                        await _sqlSugarClient.Updateable<UserFolderInfo>()
                            .SetColumns(it => it.IsDeleted == true)
                            .Where(it => it.UserId == uid && allFolderIdsToDelete.Contains(it.Id))
                            .ExecuteCommandAsync();
                        //获取删除的文件生成的分享id
                        var info = await _sqlSugarClient.Queryable<FileShareInfo>()
                            .Where(it =>
                                it.UserId == uid && fileIdsToDelete.Contains(it.ShareFileId) && it.IsDeleted == false)
                            .Select(it => it.Id)
                            .ToListAsync();
                        //清除分享链接信息
                        await _sqlSugarClient.Updateable<FileShareInfo>()
                            .SetColumns(it => it.IsDeleted == true)
                            .Where(it => it.UserId == uid && info.Contains(it.ShareFileId))
                            .ExecuteCommandAsync();
                        foreach (var item in info)
                        {
                            _icache.RemoveAsync("FileShareInfo", $"{item}");
                        }

                        //尝试删除相关向量数据
                        if (_appConfigInfo.aiSetting.Enable)
                        {
                            await _sqlSugarClient.Deleteable<ImgVectorcs>()
                                .In(fileIdsToDelete)
                                .ExecuteCommandAsync();
                        }

                        //清除文件缓存
                        await ClearCacheForFile(uid, fileIdsToDelete);
                    }

                    // --- 清除文件夹缓存 ---
                    await ClearCacheForFolder(uid, allFolderIdsToDelete);
                });

                if (result.IsSuccess)
                {
                    ClearSearchFilesCache(uid);
                    return new DefaultMsg(0, "删除成功", null);
                }
                else
                {
                    _logger.LogError(result.ErrorException, "删除文件夹失败");
                    return new DefaultMsg(1, $"删除失败: {result.ErrorMessage}", null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除文件夹系统异常");
                return new DefaultMsg(1, $"系统异常: {ex.Message}", null);
            }
        }


        /// <summary>
        /// 生成临时下载链接密钥   用于下载自己的文件
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="fileId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetTempDownLoadKey(string userId, string fileId, bool isAdmin = false)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!Guid.TryParse(fileId, out Guid fid)) return new DefaultMsg(1, "fid格式错误", null);
            //获取文件信息
            FileDownLoadInfo fileInfo = null;
            try
            {
                //使用缓存
                fileInfo = JsonSerializer.Deserialize<FileDownLoadInfo>(
                    await _icache.GetAsync<string>("FileDownLoadInfo", $"{fid}"));
                _logger.LogDebug("用户{UserId}生成下载密钥，文件id{FileId}命中FileDownLoadInfo缓存", userId, fileId);
            }
            catch (Exception)
            {
                fileInfo = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => x.Id == fid && x.IsDeleted == false)
                    .Select(x => new FileDownLoadInfo()
                    {
                        FileId = x.Id,
                        UserId = x.UserId,
                        Name = x.FileName,
                        SizeInBytes = x.FileSizeInBytes,
                        StoragePath = x.StoragePath
                    }).FirstAsync();
                _logger.LogDebug("用户{UserId}生成下载密钥，文件id{FileId}未命中FileDownLoadInfo缓存，从数据库获取", userId, fileId);
                if (fileInfo != null)
                {
                    _icache.SetAsync("FileDownLoadInfo", $"{fid}", JsonSerializer.Serialize(fileInfo),
                        DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
                }
            }

            if (fileInfo == null)
            {
                _logger.LogWarning("用户{UserId}下载失败：文件id '{FileId}' 不存在。", userId, fileId);
                return null;
            }

            if (fileInfo.UserId != uid && isAdmin != true)
            {
                _logger.LogWarning("下载失败：用户{UserId}尝试获取文件id '{FileId}' 的下载密钥，但该文件不属于该用户。", userId, fileId);
                // 修复了原代码这里漏掉的 $ 符号，使其能正常拼出 userId
                return new DefaultMsg(1, $"下载失败，用户{userId}无权通过此方式获取其他用户文件", null);
            }

            //生成密钥
            string key = Guid.NewGuid().ToString("N");
            if (isAdmin)
            {
                _logger.LogInformation("管理员代理用户{UserId}生成下载密钥成功，文件id:{FileId}，密钥:{Key}", userId, fileId, key);
            }
            else
            {
                _logger.LogInformation("用户{UserId}生成下载密钥成功，文件id:{FileId}，密钥:{Key}", userId, fileId, key);
            }

            //添加密钥映射文件信息到缓存，设置过期时间
            await _icache.SetAsync("FileDownloadKeyCache", $"{key}", JsonSerializer.Serialize(fileInfo),
                DateTimeOffset.Now.AddMinutes(_appConfigInfo.cacheSetting.TempDownLoadKeyValidityPeriod));
            return new DefaultMsg(0, "success", key);
        }

        /// <summary>
        /// 创建分享链接
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="fileId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> CreateShareKey(string userId, FileShareData fileShareData)
        {
            if (!Guid.TryParse(userId, out Guid uid))
            {
                _logger.LogWarning("生成分享链接失败：uid格式错误。UserId: {UserId}", userId);
                return new DefaultMsg(1, "uid格式错误", null);
            }

            if (fileShareData.Password?.length() > 10)
            {
                return new DefaultMsg(1, "密码长度不能大于10", null);
            }

            if (fileShareData.Introduction?.length() > 300)
            {
                return new DefaultMsg(1, "简介长度不能大于300", null);
            }

            if (fileShareData.BeginValidity > fileShareData.EndValidity || fileShareData.EndValidity < DateTime.Now)
            {
                _logger.LogWarning(
                    "生成分享链接失败：有效期异常。UserId: {UserId}, FileId: {ShareFileId}, Begin: {BeginValidity}, End: {EndValidity}",
                    userId, fileShareData.ShareFileId, fileShareData.BeginValidity, fileShareData.EndValidity);
                return new DefaultMsg(1, "有效期异常", null);
            }


            //检查是否存在文件
            if (await _sqlSugarClient.Queryable<fi>().AnyAsync(x =>
                    x.Id == fileShareData.ShareFileId && x.UserId == uid && x.IsDeleted == false) == false)
            {
                return new DefaultMsg(1, "文件不存在", null);
            }

            Guid key = Guid.NewGuid();
            var info = new FileShareInfo()
            {
                Id = key,
                ShareFileId = fileShareData.ShareFileId,
                UserId = uid,
                CreationTime = DateTime.Now,
                BeginValidity = fileShareData.BeginValidity,
                EndValidity = fileShareData.EndValidity,
                Introduction =
                    string.IsNullOrWhiteSpace(fileShareData.Introduction) ? null : fileShareData.Introduction,
                Password = string.IsNullOrWhiteSpace(fileShareData.Password) ? null : fileShareData.Password,
                IsDeleted = false
            };
            await _sqlSugarClient.Insertable<FileShareInfo>(info).ExecuteCommandAsync();

            _logger.LogInformation("用户{UserId}成功创建文件分享。ShareKey: {ShareKey}, ShareFileId: {ShareFileId}", uid, key,
                fileShareData.ShareFileId);

            //添加文件分享信息到缓存
            _ = _icache.SetAsync("FileShareInfo", $"{key}", JsonSerializer.Serialize(info),
                DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            //清除分享文件信息缓存
            _ = _icache.RemoveAsync("ShareInfoPrivate", $"{uid}");
            return new DefaultMsg(0, "success", key.ToString("N"));
        }

        /// <summary>
        /// 更新分享链接
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> UpdateShareFileInfo(string userId, string shareId, FileShareData fileShareData,
            bool isDel)
        {
            if (!Guid.TryParse(userId, out Guid uid))
            {
                _logger.LogWarning("操作分享记录失败：uid格式错误。UserId: {UserId}", userId);
                return new DefaultMsg(1, "uid格式错误", null);
            }

            if (!Guid.TryParse(shareId, out Guid sid))
            {
                _logger.LogWarning("操作分享记录失败：sid格式错误。ShareId: {ShareId}", shareId);
                return new DefaultMsg(1, "sid格式错误", null);
            }

            //删除
            if (isDel)
            {
                int delCount = await _sqlSugarClient.Updateable<FileShareInfo>()
                    .SetColumns(x => x.IsDeleted == true)
                    .Where(x => x.Id == sid && x.UserId == uid && x.IsDeleted == false)
                    .ExecuteCommandAsync();

                if (delCount == 0)
                {
                    _logger.LogWarning("用户{UserId}尝试删除分享记录失败：未找到记录或已被删除。ShareId: {ShareId}", userId, shareId);
                }
                else
                {
                    _logger.LogInformation("用户{UserId}成功删除了分享记录。ShareId: {ShareId}", userId, shareId);
                }

                //添加文件分享信息到缓存
                _ = _icache.RemoveAsync("FileShareInfo", $"{sid}");
                //清除分享文件信息缓存
                _ = _icache.RemoveAsync("ShareInfoPrivate", $"{uid}");
                return new DefaultMsg(0, "删除成功", null);
            }

            if (fileShareData == null)
            {
                _logger.LogWarning("操作分享记录失败：传入的实体数据为空。UserId: {UserId}, ShareId: {ShareId}", userId, shareId);
                return new DefaultMsg(1, "数据异常", null);
            }

            if (fileShareData.BeginValidity > fileShareData.EndValidity || fileShareData.EndValidity < DateTime.Now)
            {
                _logger.LogWarning(
                    "更新分享记录失败：有效期异常。UserId: {UserId}, ShareId: {ShareId}, Begin: {BeginValidity}, End: {EndValidity}",
                    userId, shareId, fileShareData.BeginValidity, fileShareData.EndValidity);
                return new DefaultMsg(1, "有效期异常", null);
            }

            int count = await _sqlSugarClient.Updateable<FileShareInfo>()
                .SetColumns(x => new FileShareInfo()
                {
                    BeginValidity = fileShareData.BeginValidity,
                    EndValidity = fileShareData.EndValidity,
                    Introduction = string.IsNullOrWhiteSpace(fileShareData.Introduction)
                        ? null
                        : fileShareData.Introduction,
                    Password = fileShareData.Password
                })
                .Where(x => x.Id == sid && x.UserId == uid && x.IsDeleted == false)
                .ExecuteCommandAsync();
            if (count is 0)
            {
                _logger.LogWarning("更新分享记录失败：受影响行数为0(记录可能不存在)。UserId: {UserId}, ShareId: {ShareId}", userId, shareId);
                return new DefaultMsg(1, "有效期异常", null);
            }

            _logger.LogInformation("用户{UserId}成功更新了分享记录。ShareId: {ShareId}", userId, shareId);

            //添加文件分享信息到缓存
            _ = _icache.RemoveAsync("FileShareInfo", $"{sid}");
            //清除分享文件信息缓存
            _ = _icache.RemoveAsync("ShareInfoPrivate", $"{uid}");
            return new DefaultMsg(0, "更新成功", null);
        }

        /// <summary>
        /// 获取分享信息
        /// </summary>
        /// <param name="shareKey"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetShareInfo(string shareKey)
        {
            if (!Guid.TryParse(shareKey, out Guid skey))
            {
                _logger.LogWarning("获取分享信息失败：shareKey格式错误。ShareKey: {ShareKey}", shareKey);
                return new DefaultMsg(1, "shareKey格式错误", null);
            }

            bool isInfoFromDb = false; // 标记是否来自数据库，用于决定是否回写缓存

            //尝试从缓存获取分享信息
            FileShareInfo info = null;
            if (await _icache.ExistsAsync("FileShareInfo", $"{skey}"))
            {
                info = JsonSerializer.Deserialize<FileShareInfo>(
                    await _icache.GetAsync<string>("FileShareInfo", $"{skey}"));
                _logger.LogDebug("获取分享信息，命中FileShareInfo缓存。ShareKey: {ShareKey}", shareKey);
            }


            //缓存没有，去数据库查
            if (info == null)
            {
                info = await _sqlSugarClient.Queryable<FileShareInfo>()
                    .Where(x => x.Id == skey && x.IsDeleted == false)
                    .FirstAsync();

                isInfoFromDb = true;
                _logger.LogDebug("获取分享信息，未命中FileShareInfo缓存，从数据库查询。ShareKey: {ShareKey}", shareKey);
            }

            if (info == null)
            {
                _logger.LogInformation("获取分享信息失败：分享链接无效或已被删除。ShareKey: {ShareKey}", shareKey);
                return new DefaultMsg(1, "分享链接无效或已过期", null);
            }

            //校验是否过期 (BeginValidity)
            if (info.BeginValidity > DateTime.Now)
            {
                _logger.LogInformation(
                    "获取分享信息拦截：文件未到开放下载时间。ShareKey: {ShareKey}, BeginValidity: {BeginValidity},Now:{Now}", shareKey,
                    info.BeginValidity, DateTime.Now);
                return new DefaultMsg(1, "文件未到开放下载时间", null);
            }

            //校验是否过期 (EndValidity)
            if (info.EndValidity < DateTime.Now)
            {
                await _sqlSugarClient.Updateable<FileShareInfo>()
                    .SetColumns(it => it.IsDeleted == true)
                    .Where(it => it.Id == skey)
                    .ExecuteCommandAsync();

                _logger.LogInformation(
                    "获取分享信息拦截：文件分享链接已过期，自动标记删除。ShareKey: {ShareKey}, EndValidity: {EndValidity},Now:{Now}", shareKey,
                    info.EndValidity, DateTime.Now);
                return new DefaultMsg(1, "文件分享链接已过期", null);
            }


            bool isDownloadInfoFromDb = false;
            //查询下载信息
            FileDownLoadInfo downloadInfo = null;
            if (await _icache.ExistsAsync("FileDownLoadInfo", $"{info.ShareFileId}"))
            {
                downloadInfo =
                    JsonSerializer.Deserialize<FileDownLoadInfo>(
                        await _icache.GetAsync<string>("FileDownLoadInfo", $"{info.ShareFileId}"));
                _logger.LogDebug("获取分享下载信息，命中FileDownLoadInfo缓存。ShareFileId: {ShareFileId}", info.ShareFileId);
            }


            if (downloadInfo == null)
            {
                downloadInfo = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => x.Id == info.ShareFileId && x.IsDeleted == false)
                    .Select(x => new FileDownLoadInfo()
                    {
                        FileId = x.Id,
                        UserId = x.UserId,
                        Name = x.FileName,
                        SizeInBytes = x.FileSizeInBytes,
                        StoragePath = x.StoragePath
                    })
                    .FirstAsync();

                isDownloadInfoFromDb = true;
                _logger.LogDebug("获取分享下载信息，未命中FileDownLoadInfo缓存，从数据库查询。ShareFileId: {ShareFileId}", info.ShareFileId);
            }

            // 防御性校验：原文件可能已被分享者删除
            if (downloadInfo == null)
            {
                await _sqlSugarClient.Updateable<FileShareInfo>()
                    .SetColumns(it => it.IsDeleted == true)
                    .Where(it => it.Id == skey)
                    .ExecuteCommandAsync();
                _icache.RemoveAsync("FileShareInfo", $"{skey}");
                _logger.LogInformation(
                    "获取分享信息拦截：原文件已被删除或无法访问，自动标记分享记录删除。ShareKey: {ShareKey}, ShareFileId: {ShareFileId}", shareKey,
                    info.ShareFileId);
                return new DefaultMsg(1, "原文件已被删除或无法访问", null);
            }


            //更新缓存
            var cacheExp = DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod);

            if (isInfoFromDb)
            {
                await _icache.SetAsync("FileShareInfo", $"{skey}", JsonSerializer.Serialize(info), cacheExp);
            }

            if (isDownloadInfoFromDb)
            {
                await _icache.SetAsync("FileDownLoadInfo", $"{info.ShareFileId}",
                    JsonSerializer.Serialize(downloadInfo), cacheExp);
            }

            //
            //获取分享者用户信息并返回
            var r = await _userHandler.GetUserInfo(info.UserId.ToString());
            if (r.Status == 0 && r.Data is ShowUserInfo userInfo)
            {
                _logger.LogInformation("成功获取分享信息。ShareKey: {ShareKey}, ShareFileId: {ShareFileId}", shareKey,
                    info.ShareFileId);
                return new DefaultMsg(0, "success", new
                {
                    UserId = info.UserId.ToString("N"),
                    userInfo.Nickname,
                    userInfo.AvatarUrl,
                    info.ShareFileId,
                    info.BeginValidity,
                    info.EndValidity,
                    info.Introduction,
                    info.CreationTime,
                    IsPassword = !string.IsNullOrEmpty(info.Password),
                    downloadInfo.SizeInBytes,
                    downloadInfo.Name
                });
            }

            // 如果由于某种原因找不到分享者的用户信息
            _logger.LogWarning("获取分享信息失败：分享者用户信息异常。UserId: {UserId}", info.UserId);
            return new DefaultMsg(1, "分享者信息异常", null);
        }

        /// <summary>
        /// 生成临时下载链接密钥 用于分享链接下载文件
        /// </summary>
        /// <param name="shareKey"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetShareTempDownLoadKey(string shareKey, string? pwd)
        {
            if (!Guid.TryParse(shareKey, out Guid skey))
            {
                _logger.LogWarning("获取分享下载密钥失败：shareKey格式错误。ShareKey: {ShareKey}", shareKey);
                return new DefaultMsg(1, "shareKey格式错误", null);
            }

            if (string.IsNullOrWhiteSpace(shareKey))
            {
                return new DefaultMsg(1, "shareKey为空", null);
            }

            FileShareInfo info = null;
            try
            {
                //必须先获取分享信息，才能继续获取下载信息和生成下载密钥，避免无效链接被滥用来频繁访问数据库或缓存。  
                if (!await _icache.ExistsAsync("FileShareInfo", $"{skey}"))
                {
                    _logger.LogInformation("获取分享下载密钥失败：缓存中不存在该分享记录。ShareKey: {ShareKey}", shareKey);
                    return new DefaultMsg(1, "文件过期,请刷新页面", null);
                }

                info = JsonSerializer.Deserialize<FileShareInfo>(
                    await _icache.GetAsync<string>("FileShareInfo", $"{skey}"));
                if (!await _icache.ExistsAsync("FileDownLoadInfo", $"{info.ShareFileId}"))
                {
                    _logger.LogInformation("获取分享下载密钥失败：缓存中不存在文件下载信息。ShareFileId: {ShareFileId}", info.ShareFileId);
                    return new DefaultMsg(1, "文件过期,请刷新页面", null);
                }

                FileDownLoadInfo downloadInfo =
                    JsonSerializer.Deserialize<FileDownLoadInfo>(
                        await _icache.GetAsync<string>("FileDownLoadInfo", $"{info.ShareFileId}"));
                //密码校验
                if (!string.IsNullOrEmpty(info.Password) && pwd != info.Password)
                {
                    _logger.LogInformation("获取分享下载密钥拦截：提取密码错误。ShareKey: {ShareKey}", shareKey);
                    return new DefaultMsg(1, "密码错误", null);
                }

                //校验是否过期 (EndValidity)
                if (info.BeginValidity > DateTime.Now)
                {
                    _logger.LogInformation("获取分享下载密钥拦截：文件未到开放下载时间。ShareKey: {ShareKey}", shareKey);
                    return new DefaultMsg(1, "文件未到开放下载时间", null);
                }

                //校验是否过期 (EndValidity)
                if (info.EndValidity < DateTime.Now)
                {
                    _logger.LogInformation("获取分享下载密钥拦截：文件分享链接已过期。ShareKey: {ShareKey}", shareKey);
                    return new DefaultMsg(1, "文件分享链接已过期", null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分享下载密钥时发生异常。ShareKey: {ShareKey}", shareKey);
                return new DefaultMsg(1, "请刷新页面", null);
            }

            if (info == null)
                return new DefaultMsg(1, "文件过期,请刷新页面", null);
            //添加密钥映射文件信息到缓存，设置过期时间
            string key = Guid.NewGuid().ToString("N");
            _logger.LogInformation("成功生成分享文件的临时下载密钥。ShareKey: {ShareKey}, TempKey: {TempKey}", shareKey, key);
            await _icache.SetAsync("FileDownloadKeyCache", $"{key}",
                await _icache.GetAsync<string>("FileDownLoadInfo", $"{info.ShareFileId}"),
                DateTimeOffset.Now.AddMinutes(_appConfigInfo.cacheSetting.TempDownLoadKeyValidityPeriod));
            return new DefaultMsg(0, "success", key);
        }

        public async Task<FileStreamInfo> DownloadFileWithKey(string TempKey)
        {
            if (string.IsNullOrWhiteSpace(TempKey))
            {
                return null;
            }

            if (await _icache.ExistsAsync("FileDownloadKeyCache", TempKey))
            {
                //下载信息从缓存获取  避免频繁访问数据库
                FileDownLoadInfo fileInfo =
                    JsonSerializer.Deserialize<FileDownLoadInfo>(
                        await _icache.GetAsync<string>("FileDownloadKeyCache", TempKey));
                FileStream stream;
                try
                {
                    stream = new FileStream(fileInfo.StoragePath, FileMode.Open, FileAccess.Read);
                    _logger.LogInformation("使用临时密钥成功开启文件流。TempKey: {TempKey}, FileId: {FileId}", TempKey,
                        fileInfo.FileId);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "使用下载密钥{TempKey}下载文件{FileId}失败，文件路径{StoragePath}无法访问", TempKey,
                        fileInfo.FileId, fileInfo.StoragePath);
                    return null;
                }

                return new FileStreamInfo(stream, fileInfo.Name);
            }
            else
            {
                _logger.LogWarning("下载密钥无效或已过期。TempKey: {TempKey}", TempKey);
                return null;
            }
        }

        /// <summary>
        /// 搜索文件和文件夹，支持模糊搜索和AI智能搜索（基于文本向量相似度）。
        /// 文本向量搜索需要先将文件内容生成向量并存储在数据库中，搜索时将关键词生成向量进行相似度计算。
        /// 最终结果是两部分的合并：一部分是传统的文本模糊搜索结果，另一部分是AI智能搜索结果，两者去重后返回给用户。   
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="keyword"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> SearchFiles(string userId, SearchInfo searchInfo, bool isAI = true)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (searchInfo is
                {
                    Keyword: null or "" or " ",
                    FileType: [],
                    EndCreationTime: null,
                    FileSizeInBytesMax: null,
                    FileSizeInBytesMin: null,
                    StarCreationTime: null,
                    StarLastModifiedTime: null,
                    EndLastModifiedTime: null
                })
            { return new DefaultMsg(1, "搜索参数不能全为空", null); }


            if (await _icache.ExistsAsync("SearchFilesCache", $"{uid}:{JsonSerializer.Serialize<SearchInfo>(searchInfo)}"))
            {
                try
                {
                    return new DefaultMsg(0, "cache", JsonNode.Parse(await _icache.GetAsync<string>("SearchFilesCache", $"{uid}:{JsonSerializer.Serialize<SearchInfo>(searchInfo)}")));
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "解析缓存中的搜索结果失败，可能是缓存数据损坏。UserId: {UserId}, SearchInfo: {SearchInfo}", userId, JsonSerializer.Serialize<SearchInfo>(searchInfo));
                }
            }

            UserFilesInfo userFilesInfo = new UserFilesInfo();

            if (searchInfo.FileType == null)
            {
                searchInfo.FileType = Array.Empty<string>();
            }

            if (isAI)
            {
                ISugarQueryable<fi> query = _sqlSugarClient.Queryable<fi>()
                        .Where(x => x.UserId == uid && x.IsDeleted == false);

                if (!string.IsNullOrEmpty(searchInfo.Keyword))
                {
                    query = query.Where(x => x.FileName.Contains(searchInfo.Keyword));
                }
                //判断文件类型
                if (searchInfo.FileType?.Length != 0)
                {

                    query = query.Where(x => searchInfo.FileType.Contains(x.FileType));
                }
                //修改时间范围
                if (DateTime.TryParse(searchInfo.StarLastModifiedTime, out DateTime start))
                {
                    query = query.Where(x => x.LastModifiedTime >= start);
                }
                if (DateTime.TryParse(searchInfo.EndLastModifiedTime, out DateTime end))
                {
                    query = query.Where(x => x.LastModifiedTime <= end);
                }
                //创建时间范围
                if (DateTime.TryParse(searchInfo.StarCreationTime, out DateTime startCteate))
                {
                    query = query.Where(x => x.CreationTime >= startCteate);
                }
                if (DateTime.TryParse(searchInfo.EndCreationTime, out DateTime endCreatee))
                {
                    query = query.Where(x => x.CreationTime <= endCreatee);
                }
                //文件大小
                if (searchInfo.FileSizeInBytesMin > 0)
                {
                    query = query.Where(x => x.FileSizeInBytes >= searchInfo.FileSizeInBytesMin);

                }
                if (searchInfo.FileSizeInBytesMax > 0)
                {
                    query = query.Where(x => x.FileSizeInBytes <= searchInfo.FileSizeInBytesMax);

                }


                string s =  query.Select(x => new UserFilesInfoItem()
                {
                    Id = x.Id,
                    FileName = x.FileName,
                    FileSizeInBytes = x.FileSizeInBytes,
                    FileHash = x.FileHash,
                    FolderId = x.FolderId,
                    CreationTime = x.CreationTime,
                    LastModifiedTime = x.LastModifiedTime,
                }).ToSqlString();
                //结算
                userFilesInfo.FileInfos = await query.Select(x => new UserFilesInfoItem()
                {
                    Id = x.Id,
                    FileName = x.FileName,
                    FileSizeInBytes = x.FileSizeInBytes,
                    FileHash = x.FileHash,
                    FolderId = x.FolderId,
                    CreationTime = x.CreationTime,
                    LastModifiedTime = x.LastModifiedTime,
                })
                    .ToArrayAsync();
                //搜索文件夹
                if (searchInfo.FileType is null or [] || searchInfo.FileType.Contains("文件夹"))
                {
                    ISugarQueryable<UserFolderInfo> queryFolder = _sqlSugarClient.Queryable<UserFolderInfo>()
                        .Where(x => x.UserId == uid && x.IsDeleted == false);

                    if (!string.IsNullOrEmpty(searchInfo.Keyword))
                    {
                        queryFolder = queryFolder.Where(x => x.FolderName.Contains(searchInfo.Keyword));
                    }
                    //创建时间范围 因为文件夹没有修改时间，所以只能用创建时间来搜索
                    if (searchInfo.StarCreationTime != null)
                    {
                        if (DateTime.TryParse(searchInfo.StarCreationTime, out DateTime startCteate1))
                        {
                            queryFolder = queryFolder.Where(x => x.CreationTime >= startCteate1);
                        }
                    }
                    else if (searchInfo.StarLastModifiedTime != null)
                    {
                        if (DateTime.TryParse(searchInfo.StarLastModifiedTime, out DateTime startCteate1))
                        {
                            queryFolder = queryFolder.Where(x => x.CreationTime >= startCteate1);
                        }
                    }
                    if (searchInfo.EndCreationTime != null)
                    {
                        if (DateTime.TryParse(searchInfo.EndCreationTime, out DateTime startCteate1))
                        {
                            queryFolder = queryFolder.Where(x => x.CreationTime >= startCteate1);
                        }
                    }
                    else if (searchInfo.EndLastModifiedTime != null)
                    {
                        if (DateTime.TryParse(searchInfo.EndLastModifiedTime, out DateTime startCteate1))
                        {
                            queryFolder = queryFolder.Where(x => x.CreationTime >= startCteate1);
                        }
                    }


                    userFilesInfo.Dirs = await queryFolder
                       .Select(x => new UserDirsInfoItem
                       {
                           Id = x.Id,
                           FolderName = x.FolderName,
                           CreationTime = x.CreationTime
                       })
                       .ToArrayAsync();



                }

            }
            else
            {
                //这里是点击自定义视图所用的搜索代码
                string[] key = searchInfo.Keyword?.Split(',');
                userFilesInfo.FileInfos = await _sqlSugarClient.Queryable<fi>()
                    .Where(x => x.UserId == uid && key.Any(y => x.FileName.Contains(y)) && x.IsDeleted == false)
                    .Select(x => new UserFilesInfoItem()
                    {
                        Id = x.Id,
                        FileName = x.FileName,
                        FileSizeInBytes = x.FileSizeInBytes,
                        FileHash = x.FileHash,
                        FolderId = x.FolderId,
                        CreationTime = x.CreationTime,
                        LastModifiedTime = x.LastModifiedTime,
                    })
                    .ToArrayAsync();
            }


            //计算文本向量
            if (_appConfigInfo.aiSetting.Enable && isAI && !string.IsNullOrEmpty(searchInfo.Keyword))
            {
                List<Guid> fileIds = [];
                //判断结果是否包含图片文档类型，如果包含则进行向量搜索
                if (searchInfo.FileType is not { Length: > 0 } || searchInfo.FileType.Any(x => x is "图片" or "文档"))
                {


                    //判断数据库 执行不同的向量查询语句
                    if (_sqlSugarClient.CurrentConnectionConfig.DbType == DbType.Sqlite)
                    {
                        const int vectorK = 4096;
                        if (searchInfo.FileType.Contains("图片") || searchInfo.FileType is not { Length: > 0 })
                        {
                            // 图片向量查询
                            string textVector = $"[{string.Join(',', _avs.GetTextFeature(searchInfo.Keyword))}]";

                            string sql = """
                            WITH image_candidates AS (
                                SELECT file_id, distance
                                FROM img_vectorcs
                                WHERE vector MATCH @vector
                                  AND k = @k
                                  AND user_id = @userId
                            )
                            SELECT file_id
                            FROM image_candidates
                            WHERE 1.0 - distance > @threshold;
                            """;

                            fileIds.AddRange(await _sqlSugarClient.Ado.SqlQueryAsync<Guid>(
                                sql,
                                new
                                {
                                    vector = textVector,
                                    k = vectorK,
                                    userId = uid,
                                    threshold = _appConfigInfo.aiSetting.ImageCosineThreshold
                                }));
                        }



                        if (searchInfo.FileType.Contains("文档") || searchInfo.FileType.Contains("代码") || searchInfo.FileType is not { Length: > 0 })
                        {
                            // 文本向量查询
                            string textVector = $"[{string.Join(',', _textAiVectorService.GetTextFeature(searchInfo.Keyword))}]";

                            string sql = """
                        WITH chunk_scores AS MATERIALIZED (
                            SELECT
                                file_id,
                                1.0 - distance AS score
                            FROM text_vectorcs
                            WHERE vector MATCH @vector
                              AND k = @k
                              AND user_id = @userId
                        ),
                        file_scores AS (
                            SELECT
                                file_id AS FileId,
                                MAX(score) AS Score
                            FROM chunk_scores
                            GROUP BY file_id
                        )
                        SELECT FileId, Score
                        FROM file_scores
                        WHERE Score >= @minimumThreshold;
                        """;

                            List<TextVectorSearchResult> candidates =
                                await _sqlSugarClient.Ado.SqlQueryAsync<TextVectorSearchResult>(
                                    sql,
                                    new
                                    {
                                        vector = textVector,
                                        k = vectorK,
                                        userId = uid,
                                        minimumThreshold =
                                            _appConfigInfo.aiSetting.TextCosineThreshold
                                    });

                            List<Guid> textFileIds = CutAtSignificantGap(
                                candidates,
                                _appConfigInfo.aiSetting.TextGapRatio);

                            fileIds.AddRange(textFileIds);


                        }

                    }
                    else
                    {//pgsql

                        if (searchInfo.FileType.Contains("图片") || searchInfo.FileType is not { Length: > 0 })
                        {
                            //查询图像向量数据库，获取与关键词向量相似度大于阈值的文件id
                            string textVector = $"[{string.Join(',', _avs.GetTextFeature(searchInfo.Keyword))}]";
                            string sql = $@"
                        SELECT file_id 
                        FROM img_vectorcs
                        WHERE user_id = @userId 
                          AND (1 - (vector <=> @vector::vector)) > @threshold
                        ORDER BY vector <=> @vector::vector;";
                            fileIds.AddRange(await _sqlSugarClient.Ado.SqlQueryAsync<Guid>(sql, new
                            {
                                vector = textVector,
                                userId = uid,
                                threshold = _appConfigInfo.aiSetting.ImageCosineThreshold
                            }));
                        }

                        if (searchInfo.FileType.Contains("文档") || searchInfo.FileType.Contains("代码") || searchInfo.FileType is not { Length: > 0 })
                        {
                            //查询文本向量数据库，获取与关键词向量相似度大于阈值的文件id
                            string textVector = $"[{string.Join(',', _textAiVectorService.GetTextFeature(searchInfo.Keyword))}]";

                            string sql = """
                        WITH file_scores AS (
                            SELECT
                                file_id AS "FileId",
                                MAX(1 - (vector <=> @vector::vector)) AS "Score"
                            FROM text_vectorcs
                            WHERE user_id = @userId
                            GROUP BY file_id
                        )
                        SELECT
                            "FileId",
                            "Score"
                        FROM file_scores
                        WHERE "Score" >= @minimumThreshold
                        ORDER BY "Score" DESC;
                        """;
                            List<TextVectorSearchResult> candidates =
                        await _sqlSugarClient.Ado
                            .SqlQueryAsync<TextVectorSearchResult>(
                                sql,
                                new
                                {
                                    vector = textVector,
                                    userId = uid,

                                    // 只负责排除非常不相关的结果
                                    minimumThreshold = _appConfigInfo.aiSetting.TextCosineThreshold,
                                });

                            List<Guid> textFileIds = CutAtSignificantGap(candidates, _appConfigInfo.aiSetting.TextGapRatio);
                            fileIds.AddRange(textFileIds);

                        }

                    }











                    //连接文本搜索和向量搜索结果
                    ISugarQueryable<fi> query = _sqlSugarClient.Queryable<fi>()
                        .Where(x => x.UserId == uid && fileIds.Contains(x.Id) && x.IsDeleted == false);
                    //修改时间范围

                    if (DateTime.TryParse(searchInfo.StarLastModifiedTime, out DateTime start))
                    {
                        query = query.Where(x => x.LastModifiedTime >= start);
                    }
                    if (DateTime.TryParse(searchInfo.EndLastModifiedTime, out DateTime end))
                    {
                        query = query.Where(x => x.LastModifiedTime <= end);
                    }
                    //创建时间范围
                    if (DateTime.TryParse(searchInfo.StarCreationTime, out DateTime startCteate))
                    {
                        query = query.Where(x => x.CreationTime >= startCteate);
                    }
                    if (DateTime.TryParse(searchInfo.EndCreationTime, out DateTime endCreatee))
                    {
                        query = query.Where(x => x.CreationTime <= endCreatee);
                    }
                    //文件大小
                    if (searchInfo.FileSizeInBytesMin > 0)
                    {
                        query = query.Where(x => x.FileSizeInBytes >= searchInfo.FileSizeInBytesMin);

                    }
                    if (searchInfo.FileSizeInBytesMax > 0)
                    {
                        query = query.Where(x => x.FileSizeInBytes <= searchInfo.FileSizeInBytesMax);

                    }

                    //结算
                    userFilesInfo.FileInfos = userFilesInfo.FileInfos.Concat(await query
                        .Select(x => new UserFilesInfoItem()
                        {
                            Id = x.Id,
                            FileName = x.FileName,
                            FileSizeInBytes = x.FileSizeInBytes,
                            FileHash = x.FileHash,
                            FolderId = x.FolderId,
                            CreationTime = x.CreationTime,
                            LastModifiedTime = x.LastModifiedTime,
                        })
                        .ToArrayAsync()).ToArray();



                }
            }
            //排序
            IEnumerable<UserFilesInfoItem> tmp = userFilesInfo.FileInfos.DistinctBy(x => x.Id);

            if (searchInfo.OrderByType != null)
            {

                var orderTypes = searchInfo.OrderByType
                    .Distinct()
                    .Take(3)
                    .ToArray();

                IOrderedEnumerable<UserFilesInfoItem>? ordered = null;

                foreach (var item in orderTypes)
                {
                    Func<UserFilesInfoItem, object?> keySelector = item switch
                    {
                        "时间" => x => x.CreationTime,
                        "类型" => x => Path.GetExtension(x.FileName),
                        "大小" => x => x.FileSizeInBytes,
                        _ => x => x.CreationTime
                    };

                    ordered = ordered == null
                        ? tmp.OrderByDescending(keySelector)
                        : ordered.ThenByDescending(keySelector);
                }

                userFilesInfo.FileInfos = (ordered ?? tmp.OrderByDescending(x => x.CreationTime)).ToArray();
            }
            else
            {
                userFilesInfo.FileInfos = tmp
                    .OrderByDescending(x => x.CreationTime)
                    .ToArray();
            }




            userFilesInfo.TotalFileCount = userFilesInfo.FileInfos.Length;
            var his = await _icache.GetAsync<HashSet<string>>("SearchHistory", $"{uid}") ?? new HashSet<string>();
            string p = JsonSerializer.Serialize<SearchInfo>(searchInfo);
            string r = JsonSerializer.Serialize(userFilesInfo);
            his.add(p);
            await _icache.SetAsync("SearchHistory", $"{uid}", his,
                DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            await _icache.SetAsync("SearchFilesCache", $"{uid}:{p}", r,
                DateTimeOffset.Now.AddMinutes(_appConfigInfo.cacheSetting.ValidityPeriod));

            _logger.LogInformation($"用户{userId}成功搜索文件。SearchInfo: {p}, Result: {r}");
            return new DefaultMsg(0, "success", userFilesInfo);
        }
        public sealed class TextVectorSearchResult
        {
            public Guid FileId { get; set; }

            public double Score { get; set; }
        }
        private static List<Guid> CutAtSignificantGap(
    List<TextVectorSearchResult> candidates,
    double gapRatioThreshold = 3.0)
        {
            TextVectorSearchResult[] ordered = candidates
                .OrderByDescending(x => x.Score)
                .ToArray();

            Console.WriteLine("========== 文本向量落差分析 ==========");
            Console.WriteLine($"候选文件数量：{ordered.Length}");
            Console.WriteLine($"落差比阈值：{gapRatioThreshold:F4}");

            foreach (TextVectorSearchResult item in ordered)
            {
                Console.WriteLine(
                    $"文件：{item.FileId}，相似度：{item.Score:F6}");
            }

            // 候选少于三个时，无法可靠地比较“最大落差”和其他落差
            if (ordered.Length <= 2)
            {
                Console.WriteLine("候选数量不足 3 个，不进行落差截断。");
                Console.WriteLine("====================================");

                return ordered
                    .Select(x => x.FileId)
                    .ToList();
            }

            double[] gaps = new double[ordered.Length - 1];

            for (int i = 0; i < gaps.Length; i++)
            {
                gaps[i] = ordered[i].Score - ordered[i + 1].Score;

                Console.WriteLine(
                    $"落差[{i}]：" +
                    $"{ordered[i].Score:F6} - " +
                    $"{ordered[i + 1].Score:F6} = " +
                    $"{gaps[i]:F6}");
            }

            // 找出最大落差的位置
            int largestGapIndex = 0;

            for (int i = 1; i < gaps.Length; i++)
            {
                if (gaps[i] > gaps[largestGapIndex])
                {
                    largestGapIndex = i;
                }
            }

            double largestGap = gaps[largestGapIndex];

            // 排除最大落差，再计算其他落差的中位数
            double[] otherGaps = gaps
                .Where((_, index) => index != largestGapIndex)
                .OrderBy(x => x)
                .ToArray();

            double medianGap = otherGaps.Length % 2 == 1
                ? otherGaps[otherGaps.Length / 2]
                : (
                    otherGaps[otherGaps.Length / 2 - 1] +
                    otherGaps[otherGaps.Length / 2]
                ) / 2.0;

            // 所有其他落差都为 0 时，防止除零
            double gapRatio;

            if (medianGap <= 0)
            {
                gapRatio = largestGap > 0
                    ? double.PositiveInfinity
                    : 0;
            }
            else
            {
                gapRatio = largestGap / medianGap;
            }

            Console.WriteLine("------------------------------------");
            Console.WriteLine($"最大落差位置：{largestGapIndex}");
            Console.WriteLine($"最大落差：{largestGap:F6}");
            Console.WriteLine($"其他落差中位数：{medianGap:F6}");
            Console.WriteLine($"计算得到的落差比：{gapRatio:F4}");
            Console.WriteLine($"配置的落差比阈值：{gapRatioThreshold:F4}");

            // 最大落差并不突出，认为过滤后的文件属于同一相关分组
            if (gapRatio < gapRatioThreshold)
            {
                Console.WriteLine("判定结果：没有明显断层，保留全部候选文件。");
                Console.WriteLine("====================================");

                return ordered
                    .Select(x => x.FileId)
                    .ToList();
            }

            int takeCount = largestGapIndex + 1;

            Console.WriteLine(
                $"判定结果：存在明显断层，在索引 {largestGapIndex} 后截断。");

            Console.WriteLine($"保留文件数量：{takeCount}");
            Console.WriteLine($"排除文件数量：{ordered.Length - takeCount}");
            Debug.WriteLine("====================================");

            return ordered
                .Take(takeCount)
                .Select(x => x.FileId)
                .ToList();
        }
        /// <summary>
        /// 删除搜索缓存
        /// </summary>
        /// <param name="uid"></param>
        /// <returns></returns>
        private async Task ClearSearchFilesCache(Guid uid)
        {
            var his = await _icache.GetAsync<HashSet<string>>("SearchHistory", $"{uid}");
            if (his != null)
            {
                foreach (var item in his)
                {
                    await _icache.RemoveAsync("SearchFilesCache", $"{uid}:{item}");
                }
            }
        }

        //展示自己分享的文件
        record ShareInfoPrivate(
            Guid Id,
            Guid ShareFileId,
            string FileName,
            DateTime CreationTime,
            DateTime BeginValidity,
            DateTime EndValidity,
            string? Introduction,
            string? Password);

        /// <summary>
        /// 得到自己的分享文件列表
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetShareFilesInfoPrivate(string userId)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            //使用缓存
            string cache = await _icache.GetAsync<string>("ShareInfoPrivate", $"{uid}");
            if (!string.IsNullOrWhiteSpace(cache))
            {
                try
                {
                    return new DefaultMsg(0, "cache", JsonSerializer.Deserialize<ShareInfoPrivate[]>(cache));
                }
                catch (Exception)
                {
                }
            }

            var info = (await _sqlSugarClient.Queryable<FileShareInfo>()
                .LeftJoin<fi>((x, y) => x.ShareFileId == y.Id)
                .Where(x => x.UserId == uid && x.IsDeleted == false)
                .Select((x, y) => new
                {
                    x.Id,
                    x.ShareFileId,
                    y.FileName,
                    x.CreationTime,
                    x.BeginValidity,
                    x.EndValidity,
                    x.Introduction,
                    x.Password
                }).ToListAsync()).Select(res => new ShareInfoPrivate(
                res.Id,
                res.ShareFileId,
                res.FileName,
                res.CreationTime,
                res.BeginValidity,
                res.EndValidity,
                res.Introduction,
                res.Password
            )).ToList();

            await _icache.SetAsync("ShareInfoPrivate", $"{uid}", JsonSerializer.Serialize(info),
                DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
            return new DefaultMsg(0, "success", info);
        }

        /// <summary>
        /// 根据文件夹ID获取完整的文件夹路径
        /// </summary>
        public async Task<DefaultMsg> GetFullFolderPathAsync(string userId, string folderId)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!Guid.TryParse(folderId, out Guid fid)) return new DefaultMsg(1, "fid格式错误", null);
            // 1. 尝试从缓存读取
            string cacheKey = $"{uid}:{fid}";
            string cachedPath = await _icache.GetAsync<string>("FolderPathCache", cacheKey);

            if (!string.IsNullOrEmpty(cachedPath))
            {
                return new DefaultMsg(0, "cache", cachedPath);
            }


            try
            {
                // 查询当前文件夹及其所有父级文件夹，并按根目录到当前目录的顺序生成有效文件夹名称列表
                //ToParentListAsync 这里是SqlSugar的一个扩展方法，用于递归查询父级节点
                var folderNames = (await _sqlSugarClient
                 .Queryable<FolderTreeNode>()
                 .Where(x => x.UserId == uid && !x.IsDeleted)
                 .ToParentListAsync(x => x.ParentId, fid, x => x.Id != x.ParentId))
                 .Select(x => x.FolderName)
                 .Reverse()
                 .Where(x => !string.IsNullOrWhiteSpace(x) && x.Trim() != "/")
                 .ToList();

                if (folderNames.Count == 0)
                {
                    return new DefaultMsg(1, "文件夹不存在", null);
                }

                string fullPath = "/" + string.Join("/", folderNames);

                // 写入缓存
                await _icache.SetAsync("FolderPathCache", cacheKey, fullPath,
                    DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));

                return new DefaultMsg(0, "success", fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"获取文件夹路径失败: {ex.Message}");
                return new DefaultMsg(1, "获取文件夹路径失败", null);
            }
        }

        public async Task<DefaultMsg> GetDataStatistics(string userId)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);

            //使用缓存
            string cache = await _icache.GetAsync<string>("DataStatistics", $"{uid}");
            if (!string.IsNullOrWhiteSpace(cache))
            {
                try
                {
                    _logger.LogDebug($"用户{uid}命中看板数据缓存");
                    return new DefaultMsg(0, "cache", JsonSerializer.Deserialize<DashboardStatisticsDto>(cache));
                }
                catch (Exception)
                {
                }
            }


            var info = new DashboardStatisticsDto();
            //上
            var tmp = await GetUserStorageCapacityInfo(userId);
            if (tmp.Status == 0)
            {
                var tmp2 = tmp.Data as StorageCapacityInfo;
                info.SummaryData.TotalSpaceBytes = (long)tmp2.TotalSpaceInBytes;
                info.SummaryData.UsedSpaceBytes = (long)tmp2.UsedSpaceInBytes;
            }

            info.SummaryData.FileCount = await _sqlSugarClient.Queryable<fi>()
                .Where(x => x.UserId == uid && x.IsDeleted == false).CountAsync();
            info.SummaryData.ShareCount = await _sqlSugarClient.Queryable<FileShareInfo>()
                .Where(x => x.UserId == uid && x.IsDeleted == false && x.EndValidity >= DateTime.Now).CountAsync();

            //中左
            var allTypeData = await _sqlSugarClient.Queryable<fi>()
                .Where(x => x.UserId == uid && x.IsDeleted == false)
                .GroupBy(x => x.FileType)
                .Select(x => new FileTypeDataDto()
                {
                    Name = x.FileType,
                    ValueBytes = (long)SqlFunc.AggregateSum(x.FileSizeInBytes)
                })
                .OrderByDescending(x => x.ValueBytes)
                .ToListAsync();

            if (allTypeData.Count > 5)
            {
                // 获取前 5 个类型
                var top5 = allTypeData.Take(5).ToList();

                // 跳过前 5 个，把剩下所有的字节数加起来
                long otherBytes = allTypeData.Skip(5).Sum(x => x.ValueBytes);

                // 如果剩下的字节数大于 0，就加一个“其他”分类进去
                if (otherBytes > 0)
                {
                    top5.Add(new FileTypeDataDto
                    {
                        Name = "其他",
                        ValueBytes = otherBytes
                    });
                }

                // 赋值给最终的返回对象
                info.FileTypeData = top5;
            }
            else
            {
                // 如果总分类数本来就不超过 5 个，直接原样返回即可
                info.FileTypeData = allTypeData;
            }

            //中右
            // 1. 确定时间范围：过去 7 天（包含今天）
            DateTime startTime = DateTime.Now.Date.AddDays(-6);
            DateTime endTime = DateTime.Now.Date.AddDays(1);
            // 2. 数据库分组查询：使用 SqlSugar 按天分组统计
            var dbResult = await _sqlSugarClient.Queryable<fi>()
                .Where(x => x.UserId == uid && x.CreationTime >= startTime && x.CreationTime < endTime &&
                            x.IsDeleted == false)
                .GroupBy(x => x.CreationTime.ToString("yyyy-MM-dd"))
                .Select(x => new TrendDataDto()
                {
                    Dates = x.CreationTime.ToString("yyyy-MM-dd"), // 数据库里查出来的 "2023-10-19"
                    Uploads = SqlFunc.AggregateCount(x.Id)
                })
                .ToListAsync();

            // 3. 内存“补零”算法：生成过去 7 天连续的 X 轴和 Y 轴数据
            var trendDataList = new List<TrendDataDto>();
            string[] weekNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

            for (int i = 0; i < 7; i++)
            {
                DateTime currentDay = startTime.AddDays(i);
                string queryDayStr = currentDay.ToString("yyyy-MM-dd");
                string displayDayStr = weekNames[(int)currentDay.DayOfWeek]; // 转换为周几

                var matchDay = dbResult.FirstOrDefault(x => x.Dates == queryDayStr);

                // 每次循环 new 一个新的 DTO 加进列表
                trendDataList.Add(new TrendDataDto
                {
                    Dates = displayDayStr,
                    Uploads = matchDay != null ? matchDay.Uploads : 0
                });
            }

            info.TrendData = trendDataList;
            //下

            info.TopFilesData = await _sqlSugarClient.Queryable<fi>()
                .Where(x => x.UserId == uid && x.IsDeleted == false)
                .OrderByDescending(x => x.FileSizeInBytes) // 按文件字节大小从大到小降序排列
                .Take(5) // 只取最大的 5 个
                .Select(x => new TopFilesDataDto()
                {
                    Name = x.FileName,
                    SizeByte = (long)x.FileSizeInBytes
                })
                .ToListAsync();
            await _icache.SetAsync<string>("DataStatistics", $"{uid}", JsonSerializer.Serialize(info),
                DateTimeOffset.Now.AddMinutes(30));
            return new DefaultMsg(0, "success", info);
        }

        /// <summary>
        /// 获取文件的指定长度内容，用于搜索结果预览
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="fileId">文件id</param>
        /// <param name="length">要获取文件内容的字符数 默认256字</param>
        /// <param name="fileOffset">文件偏移量 也就是从文件的哪个位置开始读取</param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetFileContent(string userId, string fileId, int length = 256, int fileOffset = 0)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (!Guid.TryParse(fileId, out Guid fid)) return new DefaultMsg(1, "fileId格式错误", null);

            var fileInfo = await _sqlSugarClient.Queryable<fi>()
                .Where(x => x.UserId == uid && x.Id == fid && x.IsDeleted == false)
                .Select(x => new { x.FileName, x.StoragePath })
                .FirstAsync();

            if (fileInfo == null)
            {
                return new DefaultMsg(1, "文件不存在或已删除或不属于你", null);
            }

            try
            {
                //这里判断文件类型，如果是文档类型就用Helper.ExtractAllText来提取文本内容，否则就用RandomAccess.Read来读取文件内容
                string content = string.Empty;
                if (Helper.DocExtensions.Contains(Path.GetExtension(fileInfo.FileName)))
                {
                    var s = Helper.ExtractAllText(fileInfo.StoragePath, fileInfo.FileName);
                    ReadOnlySpan<char> span = s.AsSpan();
                    int safeLength = Math.Min(length, Math.Max(0, span.Length - fileOffset));
                    content = span.Slice(fileOffset, safeLength).ToString();
                }
                else
                {
                    using SafeFileHandle handle = File.OpenHandle(fileInfo.StoragePath, FileMode.Open, FileAccess.Read);
                    byte[] buffer = new byte[length];
                    int bytesRead = RandomAccess.Read(handle, buffer.AsSpan(), fileOffset);
                    content = Encoding.UTF8.GetString(buffer.AsSpan(0, bytesRead));
                }


                return new DefaultMsg(0, "success", content);
            }
            catch (Exception e)
            {

                return new DefaultMsg(1, "获取文件内容失败", e.Message);
            }


        }
    }
}
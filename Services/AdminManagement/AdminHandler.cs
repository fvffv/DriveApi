using drive_api.Models;
using drive_api.Services.Cache;
using drive_api.Services.Config;
using drive_api.Services.FileManagement;
using drive_api.Services.Tool;
using Hardware.Info;
using SqlSugar;
using System.Runtime.InteropServices;
using System.Text.Json;
using fi = drive_api.Models.FileInfo;
namespace drive_api.Services.AdminManagement
{
    /// <summary>
    /// 管理员操作处理类
    /// </summary>

    public class AdminHandler(Helper helper, ILogger<AdminHandler> logger, ICache icache, ISqlSugarClient sqlSugarClient, AppConfigInfo appConfigInfo, FileHandler fh)
    {
        private readonly ILogger<AdminHandler> _logger = logger;
        private readonly ICache _icache = icache;
        private readonly Helper _helper = helper;
        private readonly ISqlSugarClient _sqlSugarClient = sqlSugarClient;
        private readonly AppConfigInfo _appConfigInfo = appConfigInfo;
        private static readonly IHardwareInfo _hardwareInfo = new HardwareInfo();
        private readonly FileHandler _fh = fh;
        /// <summary>
        /// 得到总看板数据
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> GetDataStatistics()
        {
            string cache = await _icache.GetAsync<string>("AdminDataStatistics", "admin");
            if (cache != null)
            {
                _logger.LogDebug("从缓存中获取管理员数据统计");
                return new DefaultMsg(0, "cache", JsonDocument.Parse(cache));
            }
            //用户数
            int userCount = _sqlSugarClient.Queryable<User>().Count();
            //文件数
            int fileCount = _sqlSugarClient.Queryable<fi>().Where(x => x.IsDeleted == false).Count();
            //分享链接文件数
            int shareFileCount = _sqlSugarClient.Queryable<FileShareInfo>().Where(x => x.IsDeleted == false).Count();
            //今日新增文件
            int fileTodayCount = _sqlSugarClient.Queryable<fi>().Where(x => x.IsDeleted == false && x.CreationTime > DateTime.Today).Count();
            //七日流量
            var info = _sqlSugarClient.Queryable<TrafficStatisticsModel>().OrderBy(x => x.Date, OrderByType.Desc).Take(7)
                .ToList();
            info.Reverse();
            //文件分类数据
            var allTypeData = await _sqlSugarClient.Queryable<fi>()
            .Where(x => x.IsDeleted == false)
            .GroupBy(x => x.FileType)
            .Select(x => new FileTypeDataDto()
            {
                Name = x.FileType,
                ValueBytes = (long)SqlFunc.AggregateSum(x.FileSizeInBytes)
            })
            .OrderByDescending(x => x.ValueBytes)
            .ToListAsync();
            //储存空间排名
            var topUsers = _sqlSugarClient.Queryable<fi>()
                // 1. 根据 UserId 分组
                .GroupBy(f => f.UserId)
                // 2. 选择需要的字段，并使用 SqlFunc.AggregateSum 进行求和
                .Select(f => new
                {
                    f.UserId,
                    TotalSizeBytes = SqlFunc.AggregateSum(f.FileSizeInBytes)
                })
                // 3. 按总大小降序排列
                .OrderByDescending(x => x.TotalSizeBytes)
                // 4. 取前 5 个
                .Take(5)
                // 5. 与 User 表进行 InnerJoin 获取 Username
                .InnerJoin<User>((f, u) => f.UserId == u.UserId)
                .Select((f, u) => new
                {
                    u.UserId,
                    u.Username,
                    TotalSize = f.TotalSizeBytes
                })
                .ToList();
            //日志
            object log5 = null;
            if (_sqlSugarClient.DbMaintenance.IsAnyTable("sys_logs"))
            {
                log5 = _sqlSugarClient.Queryable<SysLogs>()
                .OrderByDescending(x => x.Timestamp)
                .Take(5).Select(x => new { x.Level, x.Message, x.Timestamp })
                .ToList();
            }

            var re = new
            {
                UserCount = userCount,
                FileCount = fileCount,
                ShareFileCount = shareFileCount,
                FileTodayCount = fileTodayCount,
                traffic7day = info,
                AllTypeData = allTypeData,
                TopUsers = topUsers,
                Log = log5
            };

            _ = _icache.SetAsync("AdminDataStatistics", "admin", JsonSerializer.Serialize(re), DateTimeOffset.Now.AddMinutes(30));
            return new DefaultMsg(0, "success", re);


        }
        /// <summary>
        /// 更新系统信息
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> GetSystemInfo()
        {
            _hardwareInfo.RefreshMemoryStatus();
            _hardwareInfo.RefreshCPUList();
            //计算总内存占用率
            ulong totalMemory = _hardwareInfo.MemoryStatus.TotalPhysical;
            ulong availableMemory = _hardwareInfo.MemoryStatus.AvailablePhysical;
            ulong usedMemory = totalMemory - availableMemory;

            double memoryUsagePercent = ((double)usedMemory / totalMemory) * 100;
            // 计算总CPU占用率
            double cpuUsagePercent = 0;
            if (_hardwareInfo.CpuList.Count > 0)
            {
                cpuUsagePercent = _hardwareInfo.CpuList.Average(cpu => (double)cpu.PercentProcessorTime);
            }

            //查询磁盘总空间和已用空间
            // 1. 一次性获取操作系统当前所有 [已就绪] 的物理磁盘/挂载点
            var allReadyDrives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();

            var validDrives = _appConfigInfo.fileSetting.LocalFilePath
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(path =>
                {
                    try
                    {
                        // 将相对路径转为绝对路径，统一格式
                        string fullPath = Path.GetFullPath(path);

                        // 解决linux路径问题
                        return allReadyDrives
                            .Where(d => fullPath.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(d => d.RootDirectory.FullName.Length)
                            .FirstOrDefault();
                    }
                    catch { return null; } // 容错：跳过权限不足或非法的乱码路径
                })
                .Where(d => d != null)
                // 3. 使用 GroupBy 按照磁盘真实的根目录去重（兼容 .NET 全版本）
                // 避免两个路径都在同一个外挂硬盘上导致重复累加
                .GroupBy(d => d.RootDirectory.FullName)
                .Select(g => g.First())
                .ToList();

            // 4. 聚合求和
            long totalSpace = validDrives.Sum(d => d.TotalSize);
            long usedSpace = validDrives.Sum(d => d.TotalSize - d.AvailableFreeSpace);

            return new DefaultMsg(0, "success", new
            {
                TotalMemory = totalMemory,
                UsedMemory = usedMemory,
                MemoryUsagePercent = memoryUsagePercent,
                CpuUsagePercent = cpuUsagePercent,
                TotalDiskSpace = totalSpace,
                UsedDiskSpace = usedSpace,
            });
        }
        /// <summary>
        /// 获取所有用户列表
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> GetUsers()
        {

            //考虑到用户数量不多 暂不做分页
            var userList = await _sqlSugarClient.Queryable<User>().ToListAsync();

            var usersResult = new List<dynamic>();

            foreach (var user in userList)
            {
                var sci = await _fh.GetUserStorageCapacityInfo(user.UserId.ToString());

                usersResult.Add(new
                {
                    user.UserId,
                    user.Email,
                    user.Username,
                    user.Nickname,
                    user.AvatarUrl,
                    Sci = sci.Data,
                    user.Status,
                    user.CreatedAt,
                    user.LastLoginAt,
                    user.Preferences,
                    user.RootFolderId
                });
            }


            return new DefaultMsg(0, "success", usersResult);

        }
        /// <summary>
        /// 修改用户信息
        /// </summary>
        /// <param name="adminId">操作id</param>
        /// <param name="userInfo">表单</param>
        /// <returns></returns>
        public async Task<DefaultMsg> UpdateUserInfo(string adminId, UpdateUserModel userInfo)
        {
            var user = await _sqlSugarClient.Queryable<User>().Where(x => x.UserId == userInfo.UserId).FirstAsync();
            if (user == null)
            {
                return new DefaultMsg(1, "用户不存在", null);
            }
            user.Username = userInfo.UserName;
            user.Email = userInfo.Email;
            if (!string.IsNullOrEmpty(userInfo.Password))
            {
                user.PasswordHash = _helper.GetSHA256Hash(userInfo.Password);
            }
            user.Nickname = userInfo.NickName;
            user.Status = userInfo.Status;
            user.TotalStorageGB = userInfo.TotalStorageGB == _appConfigInfo.userSetting.DefaultTotalStorageGb ? 0 : userInfo.TotalStorageGB;
            user.Preferences = userInfo.Preferences;
            var result = await _sqlSugarClient.Updateable(user).ExecuteCommandAsync();

            if (result > 0)
            {
                _ = _icache.RemoveAsync("UserPreferences", $"{userInfo.UserId}");
                _ = _icache.RemoveAsync("UserInfoCache", $"{userInfo.UserId}");
                _ = _icache.RemoveAsync("UserStorageCapacityInfo", $"{userInfo.UserId}");
                _logger.LogInformation("管理员 {AdminId} 修改了用户 {UserId} 的信息", adminId, user.UserId);
                return new DefaultMsg(0, "修改成功", null);
            }
            else
            {
                _logger.LogWarning("管理员 {AdminId} 修改用户 {UserId} 的信息失败", adminId, user.UserId);
                return new DefaultMsg(1, "修改失败", null);
            }
        }


        public async Task<DefaultMsg> RegisterUser(string adminId, RegUserModel userRegInfo)
        {
            if (userRegInfo.UserName.Length > _appConfigInfo.userSetting.UserNameMaxLength)
            {
                return new DefaultMsg(1, $"用户名过长,最大长度为{_appConfigInfo.userSetting.UserNameMaxLength}", null);
            }
            if (userRegInfo.Password.Length < _appConfigInfo.userSetting.UserPassWordMinLength || userRegInfo.Password.Length > _appConfigInfo.userSetting.UserNameMaxLength)
            {
                return new DefaultMsg(1, $"密码过短或过长,最小长度为{_appConfigInfo.userSetting.UserPassWordMinLength}，最大为{_appConfigInfo.userSetting.UserNameMaxLength}", null);
            }


            var uid = Guid.NewGuid();
            Guid fid = Guid.NewGuid();
            User u = new User()
            {
                UserId = uid,
                Username = userRegInfo.UserName,
                Nickname = userRegInfo.UserName,
                AvatarUrl = _appConfigInfo.userSetting.DefaultUserAvatar,
                Email = userRegInfo.Email,
                CreatedAt = DateTime.Now,
                Status = userRegInfo.Status,
                TotalStorageGB = userRegInfo.TotalStorageGB,
                RootFolderId = fid,
                PasswordHash = _helper.GetSHA256Hash(userRegInfo.Password),
                Preferences = new UserPreferences() { DarkMode = false, IsDirectLinkEnabled = false }
            };

            UserFolderInfo userFolderInfo = new UserFolderInfo()
            {
                Id = fid,
                ParentId = fid,
                UserId = uid,
                FolderName = "/",
                CreationTime = DateTime.Now
            };
            //写入数据库
            int num;
            try
            {
                num = await _sqlSugarClient.Insertable(u).ExecuteCommandAsync();
                await _sqlSugarClient.Insertable(userFolderInfo).ExecuteCommandAsync();
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"管理员{adminId}，用户注册失败:{userRegInfo.UserName},{userRegInfo.Email}");
                return new DefaultMsg(1, $"注册失败:{e.Message}", null);

            }

            if (num == 1)
            {
                _logger.LogInformation($"管理员{adminId}，用户注册成功:{userRegInfo.UserName},{userRegInfo.Email}");
                return new DefaultMsg(0, "注册成功", null);
            }
            else
            {
                return new DefaultMsg(1, "注册失败,用户名或邮箱已存在", null);
            }

        }

        /// <summary>
        /// 文件管理查找
        /// </summary>
        /// <param name="req"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetGlobalFilesAsync(FilePageQueryReq req)
        {
            // SqlSugar 的异步 ref 参数，用于接收满足条件的数据总条数
            RefAsync<int> totalCount = 0;
            Guid.TryParse(req.Keyword, out Guid keywordGuid); // 尝试将关键词解析为 Guid，供 uid fid 搜索使用
            var list = _sqlSugarClient.Queryable<fi>()
                .WhereIF(!string.IsNullOrWhiteSpace(req.Keyword), x =>
                    x.FileName.Contains(req.Keyword) ||
                    x.FileHash.Contains(req.Keyword) ||
                    x.UserId == keywordGuid ||
                    x.Id == keywordGuid
                );
            //筛选软删除标记
            if (req.IsDeleted != 0)
            {
                list = list.Where(x => x.IsDeleted == (req.IsDeleted == 1 ? true : false));
            }


            var listr = await list
                 .OrderBy(x => x.CreationTime, OrderByType.Desc)
                 // 执行分页
                 .ToPageListAsync(req.PageIndex, req.PageSize, totalCount);

            // 组装返回给前端的数据格式
            var resultData = new
            {
                Items = listr,
                TotalCount = totalCount.Value // 提取出总行数
            };

            return new DefaultMsg(0, "success", resultData);
        }
        /// <summary>
        /// 删除文件
        /// </summary>
        /// <param name="fileIds"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> RemoveFiles(string adminId, FileRemoveInfoReq[] req)
        {
            foreach (var item in req)
            {
                await _fh.DeleteFileAsync(item.UserId, item.FileIds, true);
            }
            _logger.LogInformation($"管理员{adminId}执行删除操作，参数: {JsonSerializer.Serialize(req)}");
            return new DefaultMsg(0, "删除完成", null);
        }

        /// <summary>
        /// 获取临时下载密钥
        /// </summary>
        /// <param name="adminId"></param>
        /// <param name="userId"></param>
        /// <param name="fileId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetTempDownLoadKey(string adminId, string userId, string fileId)
        {
            var info = await _fh.GetTempDownLoadKey(userId, fileId, true);

            _logger.LogInformation($"管理员{adminId}获取了临时下载密钥，参数: userId={userId}, fileId={fileId}");
            return info;
        }



        /// <summary>
        /// 获取服务器配置
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> GetConfigs()
        {

            var safeConfig = new
            {
                // JWT 配置
                JwtConfig = new
                {
                    SigningKey = "",
                    _appConfigInfo.jwtSetting.Issuer,
                    _appConfigInfo.jwtSetting.Audience,
                    _appConfigInfo.jwtSetting.ExpireSeconds
                },

                // 数据库配置
                DbConfig = new
                {
                    _appConfigInfo.dbSetting.DbType,
                    ConnectionString = "",
                    _appConfigInfo.dbSetting.LogWriteToDB
                },

                // 缓存配置
                CacheConfig = new
                {
                    _appConfigInfo.cacheSetting.CacheType,
                    ConnectionString = "",
                    _appConfigInfo.cacheSetting.ValidityPeriod,
                    _appConfigInfo.cacheSetting.TempDownLoadKeyValidityPeriod
                },

                // 文件与存储规则
                FileConfig = _appConfigInfo.fileSetting,

                // 用户行为配置
                UserConfig = _appConfigInfo.userSetting,

                // 邮件配置脱敏
                EmailConfig = new
                {
                    _appConfigInfo.emailSetting.SmtpServer,
                    _appConfigInfo.emailSetting.SmtpPort,
                    _appConfigInfo.emailSetting.Email,
                    PassWord = ""
                },


                MQConfig = new
                {
                    _appConfigInfo.mqSetting.MqType,
                    ConnectionString = ""
                },

                // AI及基础信息设置
                AIConfig = _appConfigInfo.aiSetting,
                BasicInformation = _appConfigInfo.basicInformation,


                // 服务器监听设置脱敏
                ServerSettings = new
                {
                    IPv4 = new
                    {
                        _appConfigInfo.serverSettings.IPv4.Ip,
                        _appConfigInfo.serverSettings.IPv4.Port,
                        _appConfigInfo.serverSettings.IPv4.EnableSsl,
                        _appConfigInfo.serverSettings.IPv4.CertPath,
                        CertPassword = ""
                    },
                    IPv6 = new
                    {
                        _appConfigInfo.serverSettings.IPv6.Ip,
                        _appConfigInfo.serverSettings.IPv6.Port,
                        _appConfigInfo.serverSettings.IPv6.EnableSsl,
                        _appConfigInfo.serverSettings.IPv6.CertPath,
                        CertPassword = ""
                    }
                }
            };


            return new DefaultMsg(0, "success", safeConfig);
        }

        /// <summary>
        /// 更新系统配置
        /// </summary>
        /// <summary>
        /// 更新系统配置
        /// </summary>
        public async Task<DefaultMsg> UpdateSystemConfig(string adminId, SystemConfigUpdateDto req)
        {
            try
            {
                // 所有的验证和映射逻辑已经内聚到类中
                bool isSaved = _appConfigInfo.UpdateAndSaveConfig(req);

                if (isSaved)
                {
                    _logger.LogInformation($"管理员{adminId}执行保存系统配置成功，参数: {JsonSerializer.Serialize(req)}");
                    return new DefaultMsg(0, "系统配置保存成功，部分设置需重启生效", null);
                }
                else
                {
                    _logger.LogError($"管理员{adminId}执行保存系统配置失败：写入文件操作返回 false");
                    return new DefaultMsg(1, "配置内存已更新，但写入 JSON 配置文件失败", null);
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"管理员{adminId}执行保存系统配置发生异常，参数: {JsonSerializer.Serialize(req)}，异常信息: {e.Message}");
                return new DefaultMsg(1, "配置更新时发生异常，请联系管理员", null);
            }
        }

        /// <summary>
        /// 重启服务
        /// </summary>
        /// <param name="adminId"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> Restart(string adminId)
        {
            try
            {

                int rq = 0;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {

                    _logger.LogInformation($"管理员{adminId}执行了重启软件操作，正在尝试以 Windows 服务方式重启");
                    rq += Helper.ExecuteCommand("sc", "stop driveApi");
                    rq += Helper.ExecuteCommand("sc", "start driveApi");
                    if (rq != 0)
                    {
                        return new DefaultMsg(1, "Windows 服务重启失败，请保证以管理员权限执行程序并已注册服务", null);
                    }
                    else
                    {
                        return new DefaultMsg(0, "重启命令已发送", null);
                    }

                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    _logger.LogInformation($"管理员{adminId}执行了重启软件操作，正在尝试以 Linux 服务方式重启");
                    rq += Helper.ExecuteCommand("systemctl", "restart driveApi");
                    if (rq != 0)
                    {
                        return new DefaultMsg(1, "Linux 服务重启失败，请保证以管理员权限执行程序并已注册服务", null);
                    }
                    else
                    {
                        return new DefaultMsg(0, "重启命令已发送", null);
                    }
                }
                else
                {
                    _logger.LogWarning($"管理员{adminId}执行了重启软件操作，但当前操作系统不受支持，无法重启");
                    return new DefaultMsg(1, "当前操作系统不受支持，无法执行重启操作", null);
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"管理员{adminId}执行了重启软件操作时发生异常，异常信息: {e.Message}");
                return new DefaultMsg(1, e.Message, null);
            }

        }
        /// <summary>
        /// 系统日志
        /// </summary>
        /// <param name="pageIndex"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> GetLogs(int pageIndex)
        {
            if (!_appConfigInfo.dbSetting.LogWriteToDB)
            {
                return new DefaultMsg(0, "日志未写入数据库", null);
            }
            RefAsync<int> totalCount = 0;
            var info = await _sqlSugarClient.Queryable<SysLogs>().OrderBy(x => x.Timestamp, OrderByType.Desc).ToPageListAsync(pageIndex, 20, totalCount);
            return new DefaultMsg(0, "success", new
            {
                Log = info,
                TotalCount = totalCount.Value
            });
        }


        // 分页请求参数
        public class FilePageQueryReq
        {
            //页数
            public int PageIndex { get; set; } = 1;
            //每页大小
            public int PageSize { get; set; } = 10;
            //搜索关键词
            public string Keyword { get; set; }
            //查找结果是否包含已删除文件
            public int IsDeleted { get; set; } = 0;
        }

        //文件批量删除清单
        public class FileRemoveInfoReq
        {
            public string UserId { get; set; }
            public string[] FileIds { get; set; }


        }
    }



}

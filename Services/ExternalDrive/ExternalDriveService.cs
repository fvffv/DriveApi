using drive_api.Models;
using SqlSugar;

namespace drive_api.Services.ExternalDrive
{
    /// <summary>
    /// 外部网盘服务
    /// </summary>
    public class ExternalDriveService(ILogger<ExternalDriveService> logger, ISqlSugarClient sqlSugarClient, IEnumerable<ICloudDriveProvider> driveProviders)
    {
        private readonly ILogger<ExternalDriveService> _logger = logger;
        private readonly ISqlSugarClient _sqlSugarClient = sqlSugarClient;
        private readonly IEnumerable<ICloudDriveProvider> _driveProviders = driveProviders;
        /// <summary>
        /// 根据前端明确传入的网盘类型获取请求转发提供者。
        /// 此步骤不读取 external_drives；文件操作只使用调用方传入的 accessToken。
        /// </summary>
        public Task<ICloudDriveProvider> GetDriveProvider(CloudDriveType driveType)
        {
            ICloudDriveProvider? provider = _driveProviders.FirstOrDefault(x => x.DriveType == driveType);

            if (provider == null)
            {
                throw new NotSupportedException($"当前系统未注册网盘类型：{driveType}");
            }

            return Task.FromResult(provider);
        }


        /// <summary>
        /// 添加外部网盘账户。
        /// </summary>
        public async Task<DefaultMsg<ExternalDriveAccountResult>> AddExternalDrive(Guid userId, CloudDriveType driveType, string? displayName, string credentialData)
        {
            if (userId == Guid.Empty)
            {
                return new DefaultMsg<ExternalDriveAccountResult>(1, "用户ID无效", default!);
            }

            if (!Enum.IsDefined(driveType))
            {
                return new DefaultMsg<ExternalDriveAccountResult>(1, "网盘类型无效", default!);
            }

            if (string.IsNullOrWhiteSpace(credentialData))
            {
                return new DefaultMsg<ExternalDriveAccountResult>(1, "网盘认证参数不能为空", default!);
            }

            ICloudDriveProvider? provider = _driveProviders.FirstOrDefault(x => x.DriveType == driveType);
            if (provider == null)
            {
                return new DefaultMsg<ExternalDriveAccountResult>(1, $"当前系统未注册网盘类型：{driveType}", default!);
            }

            if (!string.IsNullOrWhiteSpace(displayName) && displayName.Length > 30)
            {
                return new DefaultMsg<ExternalDriveAccountResult>(1, "网盘显示名称不能超过30个字符", default!);
            }

            string normalizedCredentialData;
            try
            {
                normalizedCredentialData = NormalizeCredentialData(driveType, credentialData);
            }
            catch (ArgumentException ex)
            {
                return new DefaultMsg<ExternalDriveAccountResult>(1, ex.Message, default!);
            }

            ExternalDriveModel driveModel = new ExternalDriveModel
            {
                ExternalId = Guid.NewGuid(),
                UserId = userId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? driveType.ToString() : displayName.Trim(),
                DriveType = driveType,
                CredentialData = normalizedCredentialData,
                CreationTime = DateTime.Now,
                IsDeleted = false
            };

            try
            {
                int affectedRows = await _sqlSugarClient.Insertable(driveModel).ExecuteCommandAsync();

                if (affectedRows <= 0)
                {
                    return new DefaultMsg<ExternalDriveAccountResult>(1, "添加外部网盘失败", default!);
                }

                _logger.LogInformation("用户 {UserId} 添加外部网盘成功，ExternalId: {ExternalId}，网盘类型: {DriveType}", userId, driveModel.ExternalId, driveType);
                return new DefaultMsg<ExternalDriveAccountResult>(0, "添加外部网盘成功", new ExternalDriveAccountResult { ExternalId = driveModel.ExternalId, DisplayName = driveModel.DisplayName ?? driveType.ToString(), DriveType = driveType, CreationTime = driveModel.CreationTime });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户 {UserId} 添加外部网盘失败，网盘类型: {DriveType}", userId, driveType);
                return new DefaultMsg<ExternalDriveAccountResult>(1, "添加外部网盘失败", default!);
            }
        }

        /// <summary>
        /// 删除用户绑定的外部网盘账户。
        /// </summary>
        public async Task<DefaultMsg<ExternalDriveOperationResult>> DeleteExternalDrive(Guid userId, Guid externalId)
        {
            if (userId == Guid.Empty || externalId == Guid.Empty)
            {
                return new DefaultMsg<ExternalDriveOperationResult>(1, "用户ID或外部网盘ID无效", default!);
            }

            try
            {
                int affectedRows = await _sqlSugarClient.Updateable<ExternalDriveModel>()
                    .SetColumns(x => new ExternalDriveModel { IsDeleted = true })
                    .Where(x => x.UserId == userId && x.ExternalId == externalId && !x.IsDeleted)
                    .ExecuteCommandAsync();

                if (affectedRows <= 0)
                {
                    return new DefaultMsg<ExternalDriveOperationResult>(1, "外部网盘账户不存在或已删除", default!);
                }

                _logger.LogInformation("用户 {UserId} 删除外部网盘成功，ExternalId: {ExternalId}", userId, externalId);
                return new DefaultMsg<ExternalDriveOperationResult>(0, "删除外部网盘成功", new ExternalDriveOperationResult { Operation = "delete_drive", FileId = externalId.ToString() });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户 {UserId} 删除外部网盘失败，ExternalId: {ExternalId}", userId, externalId);
                return new DefaultMsg<ExternalDriveOperationResult>(1, "删除外部网盘失败", default!);
            }
        }

        /// <summary>
        /// 获取当前用户已添加的外部网盘列表。
        /// 不返回认证参数，避免将 CredentialData 暴露给客户端。
        /// </summary>
        public async Task<DefaultMsg<ExternalDriveAccountResult[]>> GetExternalDriveList(Guid userId)
        {
            if (userId == Guid.Empty)
            {
                return new DefaultMsg<ExternalDriveAccountResult[]>(1, "用户ID无效", []);
            }

            try
            {
                var driveList = await _sqlSugarClient
                    .Queryable<ExternalDriveModel>()
                    .Where(x => x.UserId == userId && !x.IsDeleted)
                    .OrderBy(x => x.CreationTime, OrderByType.Desc)
                    .Select(x => new ExternalDriveAccountResult
                    {
                        ExternalId = x.ExternalId,
                        DisplayName = x.DisplayName ?? x.DriveType.ToString(),
                        DriveType = x.DriveType,
                        CreationTime = x.CreationTime
                    })
                    .ToListAsync();

                return new DefaultMsg<ExternalDriveAccountResult[]>(0, "success", driveList.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户 {UserId} 的外部网盘列表失败", userId);
                return new DefaultMsg<ExternalDriveAccountResult[]>(1, "获取外部网盘列表失败", []);
            }
        }

        private static string NormalizeCredentialData(CloudDriveType driveType, string credentialData)
            => driveType switch
            {
                CloudDriveType.Baidu => BaiduDriveCredential.Normalize(credentialData),
                _ => throw new ArgumentException($"网盘类型 {driveType} 尚未实现认证参数格式")
            };
    }
}

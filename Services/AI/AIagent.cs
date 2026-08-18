using drive_api.Models; // 确保引入了你的模型命名空间
using drive_api.Services.Config;
using drive_api.Services.FileManagement;
using drive_api.Services.UserManagement;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace drive_api.Services.AI
{
    public class AIagent
    {
        private readonly ILogger<AIagent> _logger;
        private readonly FileHandler _fh;
        private readonly UserHandler _userHandler;
        private readonly AppConfigInfo _appConfigInfo;

        public AIagent(ILogger<AIagent> logger, FileHandler fh, UserHandler userHandler, AppConfigInfo appConfigInfo)
        {
            _logger = logger;
            _fh = fh;
            _userHandler = userHandler;
            _appConfigInfo = appConfigInfo;
        }

        [KernelFunction, Description("智能搜索用户网盘中的文件或文件夹（支持模糊搜索和AI向量搜索）")]
        [return: Description("返回 JSON 字符串。格式为 {\"Status\":0, \"Data\":{...}}。Data中包含 FileInfos(文件列表) 和 Dirs(文件夹列表)。每个项包含文件ID、名称和大小等。如果 Status 为 1，Data 为错误信息。")]
        public async Task<string> SearchFiles(
            [Required][Description("搜索表单,里面有关键词;文件类型;文件大小;日期等")] SearchInfo searchInfo,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            // 开启 isAI = true 进行智能向量+模糊混合搜索
            DefaultMsg result = await _fh.SearchFiles(userId, searchInfo, true);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("获取当前用户的网盘存储空间容量使用情况")]
        [return: Description("返回 JSON 字符串。Data 中包含 UsedSpaceInBytes (已用字节数) 和 TotalSpaceInBytes (总字节数)。可以用来告诉用户网盘还剩多少空间。")]
        public async Task<string> GetStorageCapacity(Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            DefaultMsg result = await _fh.GetUserStorageCapacityInfo(userId);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("获取指定目录下的文件和子文件夹列表（支持分页机制）。")]
        [return: Description("返回 JSON 字符串。Data 包含 Dirs(文件夹数组)、FileInfos(文件数组) 以及 TotalFileCount(该目录下的文件总数)。如果当前获取到的 FileInfos 数量少于 TotalFileCount，说明该目录下还有更多文件，AI 可以通过递增 pageIndex 再次调用此方法获取下一页数据。")]
        public async Task<string> ListFiles(
     [Description("目标文件夹的ID。如果不填或传入 null，系统将自动获取该用户的根目录文件列表。")] string? folderId,
     [Description("页码，默认为 1。如需获取下一页数据请递增此值。")] int pageIndex,
     [Description("每页显示的文件数量，默认为 50。如果 AI 需要一次性分析较多文件，可适当调大此值（最大建议不超过200）。")] int pageSize,
     Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();

            // 智能处理：如果大模型不知道文件夹ID，自动帮它查出根目录ID
            if (string.IsNullOrWhiteSpace(folderId))
            {
                var rootRes = await _fh.GetFolderRoot(userId);
                if (rootRes.Status == 0) folderId = rootRes.Data?.ToString();
            }

            // 参数默认值兜底（防御大模型传0或负数）
            if (pageIndex <= 0) pageIndex = 1;
            if (pageSize <= 0) pageSize = 50;

            // 调用底层已支持分页的方法
            DefaultMsg result = await _fh.GetUserDirectoryFileInfo(userId, folderId, pageIndex, pageSize);

            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("在指定目录下创建一个新的文件夹")]
        [return: Description("返回 JSON 字符串。如果 Status 为 0，Data 会返回新建文件夹的 ID；如果为 1，Data 返回失败原因。")]
        public async Task<string> CreateFolder(
            [Required][Description("要创建的文件夹名称")] string folderName,
            [Description("父级文件夹的ID。如果不填或传入 null，默认将在根目录下创建该文件夹。")] string? parentFolderId,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();

            if (string.IsNullOrWhiteSpace(parentFolderId))
            {
                var rootRes = await _fh.GetFolderRoot(userId);
                if (rootRes.Status == 0) parentFolderId = rootRes.Data?.ToString();
            }

            DefaultMsg result = await _fh.CreateFolder(userId, parentFolderId, folderName);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("重命名指定的文件或文件夹")]
        [return: Description("返回 JSON 字符串。如果 Status 为 0 表示修改成功。")]
        public async Task<string> RenameFileOrDir(
            [Required][Description("要重命名的文件或文件夹的 ID")] string targetId,
            [Required][Description("全新的名称")] string newName,
            [Required][Description("目标类型：0 代表修改文件，1 代表修改文件夹")] int type,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();

            var renameInfo = new FileOrDirReNameInfo
            {
                Id = targetId,
                NewName = newName,
                Type = type
            };

            DefaultMsg result = await _fh.RenameFileOrDir(userId, renameInfo);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("把指定的文件移入回收站（软删除），或彻底删除文件,此方法不能用于删除文件夹,注意，执行之前一定要用户二次确认后才能删除！")]
        [return: Description("返回 JSON 字符串。Status 为 0 表示删除成功。")]
        public async Task<string> DeleteFiles(
            [Required][Description("要删除的文件ID列表，如果含有多个ID请用英文逗号分隔")] string fileIds,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            var idsArray = fileIds.Split(',', StringSplitOptions.RemoveEmptyEntries);

            // 默认非管理员模式删除
            DefaultMsg result = await _fh.DeleteFileAsync(userId, idsArray, false);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("获取当前用户的基本资料和偏好设置（注意：用户的侧边栏自定义视图/快捷菜单 CustomView 列表也包含在其中）。当用户问'我有几个视图'或管理视图前需要查看现状时调用此方法。")]
        [return: Description("返回 JSON 字符串。Data 中包含 Username、Nickname 以及 Preferences(偏好设置，内含 CustomView 数组) 等信息。")]
        public async Task<string> GetUserProfile(Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            DefaultMsg result = await _userHandler.GetUserInfo(userId);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("获取用户网盘的数据统计信息（包含看板摘要、文件分类占比等）")]
        [return: Description("返回 JSON 字符串。Data 是一个仪表盘统计对象，包含各类文件占用大小、上传趋势图数据等，可用于回答用户的统计类提问。")]
        public async Task<string> GetDashboardStatistics(Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            DefaultMsg result = await _fh.GetDataStatistics(userId);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("获取指定文件的详细信息")]
        [return: Description("返回 JSON 字符串。Data 中包含文件的具体信息（如文件名、大小、所属文件夹ID、哈希值、修改时间等）。")]
        public async Task<string> GetFileInfo(
            [Required][Description("要查询详细信息的文件ID")] string fileId,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            DefaultMsg result = await _fh.GetFileInfo(userId, fileId);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("移动文件或文件夹到新的目录")]
        [return: Description("返回 JSON 字符串。Status 为 0 表示移动成功，Data 会返回成功提示。")]
        public async Task<string> MoveFileOrDir(
            [Required][Description("目标文件夹(也就是要移入的新父目录)的ID")] string newFolderId,
            [Description("要移动的文件ID列表，如果有多个请用英文逗号分隔，如果没有请留空")] string? fileIds,
            [Description("要移动的文件夹ID列表，如果有多个请用英文逗号分隔，如果没有请留空")] string? folderIds,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();

            // 帮 AI 将逗号分隔的字符串转换为你的数组模型
            var moveInfo = new FileOrDirMoveInfo
            {
                NewFolderId = newFolderId,
                FileIds = string.IsNullOrWhiteSpace(fileIds) ? Array.Empty<string>() : fileIds.Split(','),
                FolderIds = string.IsNullOrWhiteSpace(folderIds) ? Array.Empty<string>() : folderIds.Split(',')
            };

            DefaultMsg result = await _fh.MoveFileOrDir(userId, moveInfo);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("检测文件夹移动是否合法（防止把父文件夹移入自己的子文件夹内造成死循环）")]
        [return: Description("返回 JSON 字符串。Data 为 0 表示安全可以移动，大于 0 表示触发了防环校验不合法。")]
        public string ValidateFolderMove(
            [Required][Description("当前准备移动的文件夹ID")] string sourceId,
            [Required][Description("目标父文件夹ID")] string targetParentId,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            Guid.TryParse(sourceId, out Guid sId);
            Guid.TryParse(targetParentId, out Guid tId);
            Guid.TryParse(userId, out Guid uId);

            // 这个方法在你的业务里是同步返回 int 的
            int count = _fh.ValidateFolderMove(sId, tId, uId);
            return JsonSerializer.Serialize(new DefaultMsg(0, "success", count));
        }

        [KernelFunction, Description("根据绝对或相对路径字符串获取目标文件夹的ID")]
        [return: Description("返回 JSON 字符串。如果存在，Data中将返回该文件夹的ID。这在用户说“进入某某路径”时非常有用。")]
        public async Task<string> GetFolderByPathStrict(
            [Required][Description("根目录ID或起始目录ID")] string rootPathId,
            [Required][Description("文件夹的具体路径，如 '我的文件/2024/报表'")] string fullPath,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            DefaultMsg result = await _fh.GetFolderByPathStrict(userId, rootPathId, fullPath);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("获取网盘的全局系统配置信息")]
        [return: Description("返回 JSON 字符串。Data 中包含 Name(网盘名称) 和 MaxFileSize(单文件最大上传限制) 等信息。")]
        public async Task<string> GetCloudInfo()
        {
            // 这个接口不需要 UserId
            DefaultMsg result = await _fh.GetCloudInfo();
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("为指定文件创建对外的分享链接")]
        [return: Description("返回 JSON 字符串。如果 Status 为 0，Data 里面就是生成的 ShareKey（分享特征码）。")]
        public async Task<string> CreateShareKey(
            [Required][Description("要分享出去的文件ID")] string shareFileId,
            [Description("分享有效天数，默认分享 7 天")][DefaultValue(7)] int validDays,
            [Description("提取密码，如果为空则表示公开分享，任何人可看")] string? password,
            [Description("分享寄语或文件介绍说明")] string? introduction,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();


            Guid.TryParse(shareFileId, out Guid fileGuid);
            var shareData = new FileShareData
            {
                ShareFileId = fileGuid,
                BeginValidity = DateTime.Now,
                EndValidity = DateTime.Now.AddDays(validDays),
                Password = password,
                Introduction = introduction
            };

            DefaultMsg result = await _fh.CreateShareKey(userId, shareData);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("修改或取消(删除)已有的文件分享链接")]
        [return: Description("返回 JSON 字符串。Status 为 0 表示操作成功。")]
        public async Task<string> UpdateShareFileInfo(
            [Required][Description("分享记录的唯一ID (ShareId)")] string shareId,
            [Description("是否是要删除（取消分享），true表示取消分享，false表示只是修改信息")][DefaultValue(false)] bool isDel,
            [Description("更新后的有效天数（如果是修改操作）")][DefaultValue(7)] int validDays,
            [Description("更新后的密码，留空表示公开分享")] string? password,
            [Description("更新后的分享介绍说明")] string? introduction,
            Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();

            var shareData = new FileShareData
            {
                BeginValidity = DateTime.Now,
                EndValidity = DateTime.Now.AddDays(validDays),
                Password = password,
                Introduction = introduction
            };

            DefaultMsg result = await _fh.UpdateShareFileInfo(userId, shareId, shareData, isDel);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("获取他人分享链接的详细信息（对外公开接口）分享链接地址格式是:https://前端域名/share/分享ID")]
        [return: Description("返回 JSON 字符串。Data 包含分享者的昵称、头像、文件大小、有效期、是否需要密码(IsPassword)等信息，分享链接地址格式是:https://前端域名/share/分享ID")]
        public async Task<string> GetShareInfo(
            [Required][Description("分享链接的特征码 (ShareKey)")] string shareKey)
        {
            // 此方法不需要登录也能调用，所以不强制获取 userId
            DefaultMsg result = await _fh.GetShareInfo(shareKey);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("获取当前用户自己名下所有的分享链接列表")]
        [return: Description("返回 JSON 字符串。Data 包含一个数组，列出用户所有正在分享的文件、有效期和密码等。可用来回答“我分享了哪些文件”等问题。")]
        public async Task<string> GetShareFilesInfoPrivate(Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();
            DefaultMsg result = await _fh.GetShareFilesInfoPrivate(userId);
            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("全量覆盖并更新侧边栏的自定义视图/快捷菜单。警告：调用此方法会替换用户原有的所有视图配置。操作建议：先调用 GetUserProfile 获取当前视图列表，在原有数据基础上进行增删改后，将最终的完整数组传给此工具。")]
        [return: Description("返回 JSON 结果，Status 为 0 表示更新成功。")]
        public async Task<string> UpdateUserView(
    [Description("完整的、经过修改后的自定义视图对象数组")] CustomView[] customViews,
    Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();


            DefaultMsg result = await _userHandler.UpdateUserView(userId, customViews);

            return JsonSerializer.Serialize(result);
        }

        [KernelFunction, Description("根据【文件夹的唯一 ID】获取它在网盘中的完整绝对路径（例如：'/我的文件/图片'）。当用户询问某个文件夹或文件在哪里时调用此方法。🚨【严正警告】：此方法只接受文件夹ID！如果是查询“文件”的路径，你必须先获取该文件的信息，提取其属性中的 `FolderId`（即它所在的文件夹ID），然后将这个 `FolderId` 传给本方法，绝对不能直接把文件自身的ID传进来！")]
        [return: Description("返回 JSON 字符串。如果 Status 为 0，Data 里面就是完整的路径字符串。")]
        public async Task<string> GetFullFolderPath(
                    [Required][Description("目标文件夹的唯一 ID。如果是查文件路径，这里必须填该文件的 FolderId 属性值，千万不要填文件ID！")] string folderId,
                    Kernel kernel)
        {
            var userId = kernel.Data["userId"]?.ToString();


            DefaultMsg result = await _fh.GetFullFolderPathAsync(userId, folderId);

            return JsonSerializer.Serialize(result);
        }
    }
}
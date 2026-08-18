namespace drive_api.Services.WebDav
{
    // ========================================================
    // 1. 核心结果模型：用于事件返回
    // ========================================================
    public class WebDavResult
    {
        public bool IsSuccess { get; set; }
        public int StatusCode { get; set; } // HTTP 状态码 (200, 201, 204, 404 等)
        public string ErrorMessage { get; set; } = "";

        public static WebDavResult Success(int statusCode = 200) => new WebDavResult { IsSuccess = true, StatusCode = statusCode };
        public static WebDavResult Fail(int statusCode, string msg = "") => new WebDavResult { IsSuccess = false, StatusCode = statusCode, ErrorMessage = msg };
    }

    // ========================================================
    // 2. 节点模型：代表文件或文件夹信息
    // ========================================================
    public class WebDavNode
    {
        public string Name { get; set; } = "";
        public bool IsFolder { get; set; }
        public long Length { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime Modified { get; set; } = DateTime.UtcNow;
    }

    // ========================================================
    // 3. 核心驱动接口：任何想要提供 WebDAV 数据的类只需实现它
    // ========================================================
    public interface IWebDavProvider
    {
        /// <summary>
        /// 登录事件：验证用户名密码
        /// 返回 true 表示允许登录，false 则触发 401 拦截
        /// </summary>
        Task<bool> AuthenticateAsync(string username, string password);

        /// <summary>
        /// 获取节点属性（用于判断文件/文件夹是否存在）
        /// 返回 null 表示 404
        /// </summary>
        Task<WebDavNode?> GetNodeAsync(string path);

        /// <summary>
        /// 获取某文件夹下的直属子节点列表
        /// </summary>
        Task<List<WebDavNode>> GetChildrenAsync(string folderPath);

        /// <summary>
        /// 读取文件流 (供下载)
        /// </summary>
        Task<Stream?> GetFileStreamAsync(string filePath);

        /// <summary>
        /// 写入文件流 (供上传)
        /// </summary>
        Task<WebDavResult> PutFileAsync(string filePath, Stream contentStream);

        /// <summary>
        /// 创建文件夹
        /// </summary>
        Task<WebDavResult> CreateFolderAsync(string folderPath);

        /// <summary>
        /// 删除文件或文件夹
        /// </summary>
        Task<WebDavResult> DeleteAsync(string path);

        /// <summary>
        /// 移动或重命名
        /// </summary>
        Task<WebDavResult> MoveAsync(string sourcePath, string destinationPath);
    }
}

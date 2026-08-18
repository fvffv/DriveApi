using drive_api.Services.Config;
using drive_api.Services.FileManagement;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace drive_api.Services.Tool
{
    public class Helper
    {
        private Regex finalBlacklistRegex;

        private Regex linuxPathRegex;

        private readonly AppConfigInfo _appConfigInfo;

        private readonly ILogger<FileHandler> _logger;

        private static readonly Random _random = new Random();

        private readonly Dictionary<string, string> ExtensionToCategoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "jpg", "图片" },
        { "jpeg", "图片" },
        { "png", "图片" },
        { "gif", "图片" },
        { "bmp", "图片" },
        { "webp", "图片" },
        { "svg", "图片" },
        { "ico", "图片" },
        { "tif", "图片" },
        { "tiff", "图片" },
        { "heic", "图片" },
        { "raw", "图片" },
        { "doc", "文档" },
        { "docx", "文档" },
        { "xls", "文档" },
        { "xlsx", "文档" },
        { "ppt", "文档" },
        { "pptx", "文档" },
        { "pdf", "文档" },
        { "txt", "文档" },
        { "md", "文档" },
        { "csv", "文档" },
        { "rtf", "文档" },
        { "wps", "文档" },
        { "epub", "文档" },
        { "mobi", "文档" },
        { "azw3", "文档" },
        { "mp4", "视频" },
        { "avi", "视频" },
        { "mkv", "视频" },
        { "mov", "视频" },
        { "wmv", "视频" },
        { "flv", "视频" },
        { "webm", "视频" },
        { "rmvb", "视频" },
        { "m4v", "视频" },
        { "vob", "视频" },
        { "mp3", "音频" },
        { "wav", "音频" },
        { "flac", "音频" },
        { "aac", "音频" },
        { "ogg", "音频" },
        { "m4a", "音频" },
        { "wma", "音频" },
        { "ape", "音频" },
        { "zip", "压缩包" },
        { "rar", "压缩包" },
        { "7z", "压缩包" },
        { "tar", "压缩包" },
        { "gz", "压缩包" },
        { "bz2", "压缩包" },
        { "xz", "压缩包" },
        { "iso", "压缩包" },
        { "cab", "压缩包" },
        { "exe", "应用" },
        { "msi", "应用" },
        { "apk", "应用" },
        { "app", "应用" },
        { "dmg", "应用" },
        { "pkg", "应用" },
        { "deb", "应用" },
        { "rpm", "应用" },
        { "hap", "应用" },
        { "ipa", "应用" },
        { "dll", "系统" },
        { "sys", "系统" },
        { "ini", "系统" },
        { "inf", "系统" },
        { "reg", "系统" },
        { "dat", "系统" },
        { "bin", "系统" },
        { "so", "系统" },
        { "dylib", "系统" },
        { "ko", "系统" },
        { "tmp", "系统" },
        { "log", "系统" },
        { "cs", "代码" },
        { "js", "代码" },
        { "ts", "代码" },
        { "html", "代码" },
        { "css", "代码" },
        { "py", "代码" },
        { "java", "代码" },
        { "cpp", "代码" },
        { "c", "代码" },
        { "go", "代码" },
        { "json", "代码" },
        { "xml", "代码" },
        { "sql", "代码" },
        { "yml", "代码" },
        { "yaml", "代码" },
        { "vue", "代码" },
        { "php", "代码" },
        { "swift", "代码" },
        { "sh", "代码" },
        { "bat", "代码" },
        { "cmd", "代码" },
        { "ps1", "代码" }
    };

        /// <summary>
        /// 构造函数：初始化配置、日志记录器以及文件路径相关的正则表达式
        /// </summary>
        /// <param name="appConfigInfo">应用全局配置信息</param>
        /// <param name="logger">文件处理日志记录器</param>
        public Helper(AppConfigInfo appConfigInfo, ILogger<FileHandler> logger)
        {
            _appConfigInfo = appConfigInfo;
            _logger = logger;
            List<string> list = new List<string>();

            // 1. 如果配置了黑名单正则表达式，加入匹配列表
            if (!string.IsNullOrWhiteSpace(_appConfigInfo.fileSetting.FileOrDirNameBlacklistRegExp))
            {
                list.Add("(" + _appConfigInfo.fileSetting.FileOrDirNameBlacklistRegExp + ")");
            }

            // 2. 将用 '|' 分隔的文本黑名单转义后加入匹配列表
            string[] array = _appConfigInfo.fileSetting.FileOrDirNameBlacklist.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (array.Length != 0)
            {
                string text = string.Join("|", array.Select(Regex.Escape));
                list.Add("(" + text + ")");
            }

            // 3. 组合最终的文件名黑名单正则表达式
            Regex value = null;
            if (list.Any())
            {
                string pattern = string.Join("|", list);
                value = new Regex(pattern, RegexOptions.Compiled);
            }
            _logger.LogInformation("已初始化文件名黑名单: " + _appConfigInfo.fileSetting.FileOrDirNameBlacklist);
            _logger.LogInformation($"已初始化文件名黑名单正则表达式: {value}");

            // 初始化 Linux 风格绝对路径的验证正则（只允许汉字、字母、数字及 _.- 字符）
            linuxPathRegex = new Regex("^(/([\\u4e00-\\u9fa5a-zA-Z0-9_.-]+/?)*[\\u4e00-\\u9fa5a-zA-Z0-9_.-]*)?$", RegexOptions.Compiled);
            finalBlacklistRegex = value;
        }

        /// <summary>
        /// 校验指定路径是否符合 Linux 风格文件路径格式且合规
        /// </summary>
        /// <param name="path">待校验的完整路径</param>
        /// <returns>合法返回 true，否则返回 false</returns>
        public bool IsValidLinuxPathRegex(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            // 首先校验名称中是否有违规黑名单或超长字符
            if (!NameValidation(path))
            {
                return false;
            }
            // 验证路径的层级格式是否正确
            if (!linuxPathRegex.IsMatch(path))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 解析完整路径，将其拆分为“所在父目录”与“当前文件名/目录名”
        /// </summary>
        /// <param name="path">输入路径（例如 "/docs/work/file.txt"）</param>
        /// <returns>长度为 2 的数组：[0]为所在父级路径，[1]为目标文件名/文件夹名</returns>
        public string[] AnalyzePath(string path)
        {
            path = path?.Trim() ?? string.Empty;
            int num = path.LastIndexOf('/');
            if (num != -1)
            {
                // 如果以斜杠结尾，先剥离末尾斜杠（针对文件夹的情况）
                if (num == path.Length - 1)
                {
                    path = path.Substring(0, path.Length - 1);
                    num = path.LastIndexOf('/');
                    if (num == -1)
                    {
                        return new string[2] { "/", path };
                    }
                }
                string text = path.Substring(0, num + 1); // 提取父级目录路径
                string text2 = path.Substring(num + 1);   // 提取文件/文件夹名称
                return new string[2] { text, text2 };
            }
            return new string[2] { "/", "" };
        }

        /// <summary>
        /// 校验文件或文件夹名称的合法性（长度限制及黑名单检测）
        /// </summary>
        /// <param name="name">待校验的名称或路径段</param>
        /// <returns>合规返回 true，包含黑名单关键词或超长返回 false</returns>
        public bool NameValidation(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }
            try
            {
                // 按 / 分割路径段，逐段校验长度和黑名单正则
                return name.Split('/').All(delegate (string part)
                {
                    if (part.Length > _appConfigInfo.fileSetting.FileOrDirNameLengthLimit)
                    {
                        return false;
                    }
                    return (finalBlacklistRegex == null || !finalBlacklistRegex.IsMatch(part)) ? true : false;
                });
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "验证文件或文件夹名称失败: {Name}", name);
                return false;
            }
        }

        /// <summary>
        /// 使用 Fisher-Yates (Knuth) 洗牌算法对集合进行随机打乱
        /// </summary>
        /// <typeparam name="T">集合元素的类型</typeparam>
        /// <param name="source">源数据集合</param>
        /// <returns>随机打乱后的新列表</returns>
        public IEnumerable<T> ShuffleFisherYates<T>(IEnumerable<T> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }
            List<T> list = source.ToList();
            int num = list.Count;
            while (num > 1)
            {
                num--;
                int index = _random.Next(num + 1);
                T value = list[index];
                list[index] = list[num];
                list[num] = value;
            }
            return list;
        }

        /// <summary>
        /// 根据文件名或扩展名识别文件的类型大类（如：图片、文档、视频、代码等）
        /// </summary>
        /// <param name="fileNameOrExtension">文件名或纯扩展名</param>
        /// <returns>对应的类别名称（如果未匹配则返回 "其他"）</returns>
        public string GetCategory(string fileNameOrExtension)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrExtension))
            {
                return "其他";
            }
            // 尝试获取扩展名并去除前导点 '.'
            string text = Path.GetExtension(fileNameOrExtension)?.TrimStart('.');
            if (string.IsNullOrEmpty(text))
            {
                text = fileNameOrExtension.TrimStart('.');
            }
            // 查找字典映射
            if (ExtensionToCategoryMap.TryGetValue(text, out string value))
            {
                return value;
            }
            return "其他";
        }

        //增量哈希计算完整文件的 SHA-256 值，而无需将所有分片合并为一个大文件
        public async Task<string> CalculateFullHashWithoutMergingAsync(string tempDir, int totalChunks)
        {
            // 1. 创建一个增量哈希实例
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // 2. 准备一个合适大小的内存缓冲区 (例如 84KB)
            byte[] buffer = new byte[81920];

            // 3. 按序号按序读取分片文件，流式向 hasher 里喂数据
            for (int i = 0; i < totalChunks; i++)
            {
                string chunkPath = Path.Combine(tempDir, $"{i}.part");
                using var stream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);

                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    // 将每次读到的小块数据追加到哈希计算器中，不消耗额外内存，不写磁盘
                    hasher.AppendData(buffer, 0, bytesRead);
                }
            }

            // 4. 所有分片喂完后，一键获取最终的完整文件 SHA-256
            byte[] hashBytes = hasher.GetHashAndReset();
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        /// <summary>
        /// 计算给定字符串的 SHA256 哈希值
        /// </summary>
        /// <param name="input">待计算哈希的原始字符串</param>
        /// <returns>小写的 64 位十六进制 SHA256 哈希字符串</returns>
        public string GetSHA256Hash(string input)
        {
            using SHA256 sHA = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            byte[] array = sHA.ComputeHash(bytes);
            StringBuilder stringBuilder = new StringBuilder();
            byte[] array2 = array;
            foreach (byte b in array2)
            {
                stringBuilder.Append(b.ToString("x2"));
            }
            return stringBuilder.ToString();
        }

        /// <summary>
        /// 在后台静默执行系统命令行程序并捕获输出
        /// </summary>
        /// <param name="fileName">可执行程序名称或路径（如 "cmd.exe", "bash" 等）</param>
        /// <param name="arguments">执行参数</param>
        /// <returns>进程退出状态码（0 代表成功，非 0 代表发生错误）</returns>
        public static int ExecuteCommand(string fileName, string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true, // 重定向标准输出流
                RedirectStandardError = true,  // 重定向标准错误流
                UseShellExecute = false,
                CreateNoWindow = true          // 不创建外部控制台窗口
            };
            using Process process = Process.Start(startInfo);
            if (process != null)
            {
                string text = process.StandardOutput.ReadToEnd();
                string text2 = process.StandardError.ReadToEnd();
                process.WaitForExit();

                // 非正常退出时打印高亮红色错误日志
                if (process.ExitCode != 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\n❌ [命令执行失败] " + fileName + " " + arguments);
                    Console.WriteLine($"退出代码: {process.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Console.WriteLine("输出信息: " + text.Trim());
                    }
                    if (!string.IsNullOrWhiteSpace(text2))
                    {
                        Console.WriteLine("错误信息: " + text2.Trim());
                    }
                    Console.ResetColor();
                    return process.ExitCode;
                }
                Console.WriteLine(text.Trim());
                return 0;
            }
            return 1;
        }
    }
}
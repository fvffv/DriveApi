using System.ComponentModel;

namespace drive_api.Models
{
    public class UserPreferences
    {
        // 是否启用深色模式
        public bool DarkMode { get; set; } = false;
        // 是否启用文件直链功能
        public bool IsDirectLinkEnabled { get; set; } = false;
        // 是否启用WebDAV功能
        public bool IsWebDAVEnabled { get; set; } = false;
        public CustomView[] CustomView { get; set; }

    }

    public class CustomView
    {
        [Description("视图的名称，例如'我的视频'或'工作报表'")]
        public string Name { get; set; }

        [Description("视图类型：0 代表基于关键字筛选(如后缀名)，1 代表文件夹绝对路径快捷跳转(如'/工作')")]
        public int Type { get; set; }

        [Description("FontAwesome图标字符串，例如 'fa-solid fa-folder', 'fa-solid fa-film' 等。")]
        public string Icon { get; set; }

        [Description("视图关键词或文件夹路径的数组。如果 Type=0，传入关键字数组(如 ['.mp4', '.avi'])；如果 Type=1，传入包含绝对路径的单元素数组(如 ['/我的文件/报表'])。")]
        public string[] Keywords { get; set; }
    }
}

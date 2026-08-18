using drive_api.Models;
using drive_api.Services.Cache;
using drive_api.Services.Config;
using drive_api.Services.Email;
using drive_api.Services.Tool;
using driveApi.Services.JWT;
using MimeDetective;
using SqlSugar;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace drive_api.Services.UserManagement
{
    public class UserHandler(Helper helper, IWebHostEnvironment env, IContentInspector contentInspector, ILogger<UserHandler> logger, ICache icache, ISqlSugarClient sqlSugarClient, AppConfigInfo appConfigInfo, EmailHandler emailHandler, JwtEventHandler jwtEventHandler)
    {
        private static readonly Random _random = new Random();
        private const string Chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        private readonly ILogger<UserHandler> _logger = logger;
        private readonly ICache _icache = icache;
        private readonly ISqlSugarClient _sqlSugarClient = sqlSugarClient;
        private readonly AppConfigInfo _appConfigInfo = appConfigInfo;
        private readonly EmailHandler _emailHandler = emailHandler;
        private readonly JwtEventHandler _jwtEventHandler = jwtEventHandler;
        private readonly IContentInspector _contentInspector = contentInspector;
        private readonly IWebHostEnvironment _env = env;
        private readonly Helper _helper = helper;
        /// <summary>
        /// 注册新用户
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="email"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> RegisterUserAsync(UserRegInfo userRegInfo)
        {
            //获取验证码
            string code = await _icache.GetAsync<string>("EmailCode", userRegInfo.Email);
            //基本信息校验
            if (string.IsNullOrWhiteSpace(code) || userRegInfo.code != code)
            {
                return new DefaultMsg(1, "验证码已过期或错误，请重新输入", null);
            }
            if (userRegInfo.UserName.Length > _appConfigInfo.userSetting.UserNameMaxLength)
            {
                return new DefaultMsg(1, $"用户名过长,最大长度为{_appConfigInfo.userSetting.UserNameMaxLength}", null);
            }
            if (userRegInfo.PassWord.Length < _appConfigInfo.userSetting.UserPassWordMinLength || userRegInfo.PassWord.Length > _appConfigInfo.userSetting.UserNameMaxLength)
            {
                return new DefaultMsg(1, $"密码过短或过长,最小长度为{_appConfigInfo.userSetting.UserPassWordMinLength}，最大为{_appConfigInfo.userSetting.UserNameMaxLength}", null);
            }

            _logger.LogDebug("用户注册通过基本校验:{UserName},{Email}", userRegInfo.UserName, userRegInfo.Email);
            //删除验证码
            await _icache.RemoveAsync("EmailCode", userRegInfo.Email);
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
                Status = 0,
                TotalStorageGB = 0,
                RootFolderId = fid,
                PasswordHash = GetSHA256Hash(userRegInfo.PassWord),
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
                _logger.LogWarning(e, $"用户注册失败:{userRegInfo.UserName},{userRegInfo.Email}");
                return new DefaultMsg(1, "注册失败,用户名或邮箱已存在", null);

            }

            if (num == 1)
            {
                _logger.LogInformation($"用户注册成功:{userRegInfo.UserName},{userRegInfo.Email}");
                return new DefaultMsg(0, "注册成功", null);
            }
            else
            {
                return new DefaultMsg(1, "注册失败,用户名或邮箱已存在", null);
            }

        }
        public string GetSHA256Hash(string input)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = sha256.ComputeHash(bytes);

                StringBuilder builder = new StringBuilder();
                foreach (byte b in hashBytes)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }
        public async Task<DefaultMsg> LoginUserAsync(string usernameOrEmail, string password)
        {
            //检验
            if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrWhiteSpace(password))
            {
                return new DefaultMsg(1, "用户名或密码错误", null);
            }

            //查询数据
            var tmp = _sqlSugarClient.Queryable<User>();
            User user;
            if (new EmailAddressAttribute().IsValid(usernameOrEmail))
            {
                user = await tmp.FirstAsync(x => x.Email == usernameOrEmail && x.PasswordHash == GetSHA256Hash(password) && x.Status != 1);
            }
            else
            {

                user = await tmp.FirstAsync(x => x.Username == usernameOrEmail && x.PasswordHash == GetSHA256Hash(password) && x.Status != 1);
            }

            if (user == null)
            {
                return new DefaultMsg(1, "用户名或密码错误", null);
            }
            else
            {
              
                //更新最后登录时间 
                await _sqlSugarClient.Updateable<User>()
                .SetColumns(it => new User { LastLoginAt = DateTime.Now })
                .Where(it => it.UserId == user.UserId)
                .ExecuteCommandAsync();
                //构造token信息内容
                var claims = new List<Claim>
                {
                    // 用户唯一标识
                    new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                    // 用户名
                    new Claim(ClaimTypes.Name, user.Username)
                };
                await GetUserInfo(user.UserId.ToString());
                //构造token信息
                string token = _jwtEventHandler.BuildToken(claims, _appConfigInfo.jwtSetting);
                _logger.LogInformation($"用户登陆成功:{user.Username},{user.Email}");
                return new DefaultMsg(0, "登录成功", new LoginResult(token, user.Preferences));
            }

        }

        public async Task<DefaultMsg> GetUserInfo(string userId)
        {

            //尝试从缓存获取
            var cacheData = await _icache.GetAsync<string>("UserInfoCache", userId);
            if (!string.IsNullOrWhiteSpace(cacheData))
            {
                return new DefaultMsg(0, "success", JsonSerializer.Deserialize<ShowUserInfo>(cacheData));
            }

            if (!Guid.TryParse(userId, out _))
            {
                return new DefaultMsg(1, "用户不存在", null);
            }

            Guid.TryParse(userId, out Guid uid);
            //查询用户信息
            User u = await _sqlSugarClient.Queryable<User>().FirstAsync(x => x.UserId == uid);
            //查询用户存储信息
            //var sci = await _fileHandler.GetUserStorageCapacityInfo(userId);
            //sci.Data = JsonSerializer.Deserialize<object>(sci.Data as string);
            //构造返回信息
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
                await _icache.SetAsync("UserPreferences", $"{uid}", JsonSerializer.Serialize(json.Preferences), DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
                await _icache.SetAsync("UserInfoCache", $"{uid}", JsonSerializer.Serialize(json), DateTimeOffset.Now.AddDays(_appConfigInfo.cacheSetting.ValidityPeriod));
                return new DefaultMsg(0, "success", json);
            }
            else
            {
                return new DefaultMsg(1, "用户不存在", null);
            }
        }
        /// <summary>
        /// 发送验证码邮件
        /// </summary>
        /// <returns></returns>
        public async Task<DefaultMsg> SeedEmailCode(string Email)
        {
            //格式校验
            if (new EmailAddressAttribute().IsValid(Email) && !string.IsNullOrWhiteSpace(Email))
            {
                _logger.LogInformation($"正在发送验证码到邮箱:{Email}");
                string code = GenerateRandomString(5);
                _emailHandler.SeedCodeEmail(Email, code, Email);
                _icache.SetAsync<string>("EmailCode", Email, code, DateTimeOffset.Now.AddMinutes(10));
                return new DefaultMsg(0, "验证码发送成功,10分钟内有效", null);
            }
            else
            {
                return new DefaultMsg(1, "邮箱格式不正确", null);
            }


        }

        /// <summary>
        /// 使用 LINQ 生成指定长度的随机字符串.
        /// </summary>
        /// <param name="length">要生成的字符串长度</param>
        /// <returns>随机字符串</returns>
        private string GenerateRandomString(int length)
        {

            return new string(Enumerable.Repeat(Chars, length)
                .Select(s => s[_random.Next(s.Length)])
                .ToArray());
        }
        /// <summary>
        /// 上传头像
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="avatarStream"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> UploadAvatar(string userId, Stream avatarStream)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);

            //检测是否是图片类型
            var results = _contentInspector.Inspect(avatarStream);
            if (results.Any())
            {

                var result = results.First();
                string mimeType = result.Definition.File.MimeType; // 获取类型 例如: "image/jpeg"
                _logger.LogInformation($"检测到用户 {userId} 上传的头像类型:{mimeType}");
                if (!mimeType.StartsWith("image/"))
                {
                    return new DefaultMsg(1, "上传的文件不是图片类型", null);
                }
                string name = $"{uid.ToString("N")}.{result.Definition.File.Extensions.FirstOrDefault()}";
                //总路径
                var ap = Path.Combine(_env.WebRootPath, "driveassets/avatar", name);
                // 将读取游标重置到流的开头
                avatarStream.Position = 0;
                using (var stream = new FileStream(ap, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await avatarStream.CopyToAsync(stream);
                }
                //更新数据库
                await _sqlSugarClient.Updateable<User>()
                .SetColumns(it => it.AvatarUrl == name)
                .Where(it => it.UserId == uid).ExecuteCommandAsync();
                await _icache.RemoveAsync("UserInfoCache", $"{uid}");
                return new DefaultMsg(0, "上传头像成功", name);

            }
            else
            {
                return new DefaultMsg(1, "上传的文件不是图片类型", null);
            }
        }

        /// <summary>
        /// 更新用户信息（昵称等非敏感信息）
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="userInfoEdit"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> UpdateUserInfo(string userId, UserInfoEdit userInfoEdit)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (string.IsNullOrWhiteSpace(userInfoEdit.UserNick))
            {
                return new DefaultMsg(1, "昵称不可为空", null);
            }
            if (userInfoEdit.UserNick.Length > _appConfigInfo.userSetting.UserNameMaxLength)
            {
                return new DefaultMsg(1, $"昵称过长,最大长度为{_appConfigInfo.userSetting.UserNameMaxLength}", null);
            }

            int num = await _sqlSugarClient.Updateable<User>()
                .SetColumns(it => it.Nickname == userInfoEdit.UserNick)
                .Where(it => it.UserId == uid).ExecuteCommandAsync();
            if (num == 1)
            {
                _logger.LogInformation($"用户 {userId} 修改信息成功");
                await _icache.RemoveAsync("UserInfoCache", $"{uid}");
                return new DefaultMsg(0, "修改信息成功", null);
            }
            else
            {
                return new DefaultMsg(1, "修改信息失败", null);
            }
        }
        /// <summary>
        /// 更新密码
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="userPasswordEdit"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> UpdatePassword(string userId, UserPasswordEdit userPasswordEdit)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            if (userPasswordEdit.NewPassword.Length < _appConfigInfo.userSetting.UserPassWordMinLength || userPasswordEdit.NewPassword.Length > _appConfigInfo.userSetting.UserNameMaxLength)
            {
                return new DefaultMsg(1, $"密码过短或过长,最小长度为{_appConfigInfo.userSetting.UserPassWordMinLength}，最大为{_appConfigInfo.userSetting.UserNameMaxLength}", null);
            }
            int num = await _sqlSugarClient.Updateable<User>()
                .SetColumns(it => it.PasswordHash == GetSHA256Hash(userPasswordEdit.NewPassword))
                .Where(it => it.UserId == uid && it.PasswordHash == GetSHA256Hash(userPasswordEdit.OldPassword)).ExecuteCommandAsync();
            if (num == 1)
            {
                _logger.LogInformation($"用户 {userId} 修改密码成功");
                return new DefaultMsg(0, "修改密码成功", null);
            }
            else
            {
                return new DefaultMsg(1, "修改密码失败，旧密码错误", null);
            }
        }
        /// <summary>
        /// 更新用户偏好设置
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="preferences"></param>
        /// <returns></returns>
        public async Task<DefaultMsg> UpdateUserPreferences(string userId, UserPreferences preferences)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);




            int num = await _sqlSugarClient.Updateable<User>()
                .SetColumns(it => new User { Preferences = preferences })
                .Where(it => it.UserId == uid).ExecuteCommandAsync();
            if (num == 1)
            {
                _logger.LogInformation($"用户 {userId} 更新偏好设置成功");
                await _icache.RemoveAsync("UserInfoCache", $"{uid}");
                await _icache.RemoveAsync("UserPreferences", $"{uid}");
                return new DefaultMsg(0, "用户偏好设置更新成功", null);
            }
            else
            {
                return new DefaultMsg(1, "用户偏好设置更新失败", null);
            }
        }

        /// <summary>
        /// 更新用户视图信息 视图功能主要是为了满足用户自定义文件筛选和展示需求，用户可以创建多个视图，每个视图包含一个名称、一个图标和一组关键词。系统会根据这些关键词在用户的文件中进行匹配，并将匹配的文件展示在对应的视图中。用户可以通过更新视图信息来修改视图的名称、图标和关键词，以便更好地组织和管理他们的文件。
        /// 其中有两个类型 一个是根据关键词匹配的视图（Type=0），用户可以为这个视图设置一组关键词，系统会在用户的文件中查找包含这些关键词的文件，并将它们展示在这个视图中。另一个是根据文件夹路径视图（Type=1），相当于文件夹快捷方式
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="customViews">视图数组</param>
        /// <returns></returns>
        public async Task<DefaultMsg> UpdateUserView(string userId, CustomView[] customViews)
        {
            if (!Guid.TryParse(userId, out Guid uid)) return new DefaultMsg(1, "uid格式错误", null);
            var pr = await _sqlSugarClient.Queryable<User>().Where(it => it.UserId == uid).Select(it => it.Preferences).FirstAsync();
            //过滤非法视图
            customViews = customViews.DistinctBy(x => x.Name)
            .Where(view =>
            {
                return _helper.NameValidation(view.Name) &&
                view.Name.Length <= 20 &&
                view.Icon.Length <= 20 && view.Keywords.Count() < 20;
            }
           )
        .Select(view =>
          new CustomView()
          {
              Name = view.Name,
              Type = view.Type,
              Icon = view.Icon,
              Keywords = view.Type == 0 ? view.Keywords
               .Where(k => _helper.NameValidation(k) && k.Length <= 10).Distinct()
               .ToArray() : view.Keywords
          })
        .ToArray();
            pr.CustomView = customViews;
            int num = await _sqlSugarClient.Updateable<User>()
               .SetColumns(it => it.Preferences == pr)
               .Where(it => it.UserId == uid).ExecuteCommandAsync();
            if (num == 1)
            {
                _logger.LogInformation($"用户 {userId} 更新视图成功");
                await _icache.RemoveAsync("UserInfoCache", $"{uid}");
                await _icache.RemoveAsync("UserPreferences", $"{uid}");
                return new DefaultMsg(0, "更新视图成功", null);
            }
            else
            {
                return new DefaultMsg(1, "更新视图失败", null);
            }
        }



    }
}

# DriveApi · 居家网盘服务端

DriveApi 是基于 ASP.NET Core 的自托管网盘服务端，为网页和桌面客户端提供统一 API。文件保存在自己的磁盘中，账户、目录、分享和文件元数据由数据库管理。

项目支持账户注册与登录、文件与目录管理、分片上传与合并、下载与分享、文件搜索、WebDAV，以及管理端的用户管理、日志和流量统计。还可以使用本地 ONNX 模型进行图片、文档语义搜索，或配置大模型 API 使用智能助手。

- [项目源码](https://github.com/fvffv/DriveApi)
- [程序与 ONNX 模型下载](https://github.com/fvffv/DriveApi/releases)


## 快速开始

1. 从 [Releases](https://github.com/fvffv/DriveApi/releases) 下载对应系统的程序压缩包并解压。
2. 修改 `appsettings.json`：配置数据库（推荐 PostgreSQL，`DbType: 4`；SQLite 为 `2`）和正确的连接字符串。
3. **配置 `EmailConfig`**，否则无法发送注册验证码、完成账户注册。
4. 需要本地语义搜索时，单独下载 ONNX 模型，放入程序旁的 `Onnx`，再启用 `AIConfig.Enable`；需要智能助手时另外填写 `ApiKey`、`BaseURL` 和 `Model`。
5. 如需网页界面，将前端编译产物放入 `wwwroot`，确保存在 `wwwroot/index.html`。
6. 进入程序目录运行 `driveApi.exe`（Windows）或 `./driveApi`（Linux/macOS），确认配置正常后再注册系统服务。

## 文档导航

- [下载与目录结构](#download)
- [数据库、邮件、AI 等必要配置](#configuration)
- [运行与前端网页](#run)
- [注册为系统服务](#services)
- [更新与备份](#maintenance)
- [GitHub Actions 自动发布](#release)

<a id="download"></a>

## 一、下载与目录结构

根据服务器选择对应压缩包。发布包是**自包含单文件**，不需要另装 .NET 运行时，但仍需要操作系统自身的原生依赖。

| 系统 | 发布包 |
| --- | --- |
| Windows x64 | `driveApi-win-x64.zip` |
| Linux x64 | `driveApi-linux-x64.tar.gz` |
| Linux ARM64 | `driveApi-linux-arm64.tar.gz` |
| macOS Apple Silicon / ARM64 | `driveApi-osx-arm64.tar.gz` |

压缩包根目录只有以下五项，`wwwroot` 和 `Onnx` 初始为空：

```text
程序目录/
├── driveApi                 # Windows 为 driveApi.exe
├── appsettings.json
├── EmailCodeTemplate.html
├── wwwroot/
└── Onnx/
```

ONNX 模型和编译后的网页需要单独放入。首次运行后，程序会创建用户文件、临时分片、日志、静态资源等目录或文件；“只有五项”指发布压缩包，并非运行后的目录。

建议放在固定、可写的位置，例如 Windows 的 `C:\DriveApi` 或 Linux 的 `/opt/drive-api`。命令行运行时先切换到程序目录，以便正确读取配置、模板、网页和模型。

<a id="configuration"></a>

## 二、必要配置

编辑程序同目录的 `appsettings.json`，保留完整配置结构，只修改需要调整的字段。下面的 JSON 是各配置节示例，不是完整文件。

至少配置数据库和邮件服务。发布包内均为占位值，不含可直接使用的数据库密码、邮箱授权码或 AI 密钥。

### 1. DbConfig：数据库

`DbType` 使用数字：

| 值 | 数据库 | 用途 |
| --- | --- | --- |
| `4` | PostgreSQL | **推荐**，适合长期使用、多用户和并发上传；使用 pgvector 支持向量查询。 |
| `2` | SQLite | 本地数据库文件，部署方便；适合轻量使用，向量查询使用 sqlite-vec。 |

切换 `DbType` 时必须同时修改 `ConnectionString`。切换配置不会迁移原数据库中的账户和文件记录。

#### PostgreSQL（推荐）

```json
"DbConfig": {
  "DbType": 4,
  "ConnectionString": "Host=127.0.0.1;Port=5432;Database=drive;Username=postgres;Password=替换为数据库密码;Pooling=true;Maximum Pool Size=100;Timeout=15;",
  "LogWriteToDB": true,
  "CreateAdminUser": false
}
```

`Host` 是数据库地址，`Port` 是实际监听端口，`Database` 是数据库名称，`Username` / `Password` 是数据库账户。`Timeout` 是获取连接的等待时间，单位为秒；连接池上限应结合 PostgreSQL 的连接限制设置。

源码仓库提供 [install_postgresql_pgvector_ubuntu.sh](install_postgresql_pgvector_ubuntu.sh)，用于在 **Ubuntu** 一键安装 PostgreSQL 和 pgvector 向量扩展。脚本不放入程序压缩包，请从源码仓库单独获取。

将脚本上传到 Ubuntu 后执行。以下示例适用于数据库和 API 在同一台机器：

```bash
sudo env ALLOW_CIDR=127.0.0.1/32 ALLOW_CIDR_V6=::1/128 bash install_postgresql_pgvector_ubuntu.sh
```

根据提示设置 `postgres` 密码，并将它写入连接字符串。脚本默认安装 PostgreSQL 18；可用 `POSTGRES_VERSION=17` 等环境变量选择仓库提供的版本。端口以脚本最后的输出为准。

脚本会修改 PostgreSQL 监听和访问规则、设置 `postgres` 密码、重启数据库，并可能调整 UFW。**不传入上述 CIDR 参数时，默认允许所有 IPv4/IPv6 来源进行密码认证。** 数据库不在本机时，将允许范围改成 API 服务器的实际地址，并相应限制防火墙；不要无条件对公网开放数据库端口。

脚本在 `postgres` 和 `template1` 中启用 pgvector，因此随后创建的数据库默认带向量扩展。已经存在的 `drive` 数据库仍需要单独启用：

```bash
# 仅在 drive 数据库尚不存在时创建；端口不为 5432 时加上 -p 实际端口。
sudo -u postgres createdb drive
sudo -u postgres psql -d drive -c 'CREATE EXTENSION IF NOT EXISTS vector;'
```

建议在首次启动前准备好 `drive` 数据库。程序会初始化业务表；开启本地 AI 功能时也会尝试启用 `vector`，但数据库账户需要相应权限。向量扩展初始化失败会关闭相关功能，应检查启动日志。

#### SQLite

```json
"DbConfig": {
  "DbType": 2,
  "ConnectionString": "Data Source=drive.db;Pooling=True;",
  "LogWriteToDB": true,
  "CreateAdminUser": false
}
```

`drive.db` 是相对当前工作目录的数据库文件。注册成服务时建议使用绝对路径，例如：

```text
Windows：Data Source=C:\DriveApi\drive.db;Pooling=True;
Linux：  Data Source=/opt/drive-api/drive.db;Pooling=True;
```

写进 JSON 字符串的 Windows 反斜杠需要转义：`"Data Source=C:\\DriveApi\\drive.db;Pooling=True;"`。程序账户必须能写入数据库所在目录，以便创建数据库、WAL 和共享内存文件。

#### 管理员账户与数据库日志

`LogWriteToDB` 控制是否向数据库写入日志。

需要初始化管理员时，在仅自己能够访问服务的情况下，将 `CreateAdminUser` 设为 `true`，启动后立即注册自己的账户。当前实现会将启用该开关后的首个成功注册账户设为管理员，然后将开关写回 `false`。不要在公开开放注册时保留此开关。

### 2. EmailConfig：必须配置

**账户注册需要邮件验证码。未配置或配置错误时，无法完成正常注册。**

```json
"EmailConfig": {
  "SmtpServer": "smtp.example.com",
  "SmtpPort": 465,
  "Email": "your-account@example.com",
  "PassWord": "替换为邮箱SMTP授权码或SMTP密码"
}
```

- `SmtpServer`：邮件服务商提供的 SMTP 服务器地址。
- `SmtpPort`：当前代码使用连接时即启用 SSL/TLS 的方式，通常为 `465`；不能直接当成仅支持 STARTTLS 的 `587` 端口使用。
- `Email`：用于发送验证码的邮箱账户。
- `PassWord`：SMTP 密码或授权码，具体以邮件服务商要求为准，不一定是网页登录密码。

先在邮箱服务商处开启 SMTP，并确认服务器可以访问对应地址与端口。`EmailCodeTemplate.html` 必须保留在程序同目录。修改后重启，检查是否出现“邮箱服务连接成功”，再验证能否收到验证码。

### 3. AIConfig：智能助手与本地语义搜索

这两项的依赖不同：

| 功能 | 是否需要外部 AI API | 需要准备什么 |
| --- | --- | --- |
| 智能助手 | 需要 | `BaseURL`、`ApiKey`、`Model`，以及到该服务的网络连接。 |
| 图片、文档语义搜索 | 不需要 | 本地 ONNX 模型、向量数据库支持，并开启 `Enable`。 |

```json
"AIConfig": {
  "Enable": true,
  "ImageCosineThreshold": 0.25,
  "TextCosineThreshold": 0.4,
  "TextGapRatio": 2.7,
  "BaseURL": "https://api.deepseek.com",
  "ApiKey": "",
  "Model": "填写服务商提供的模型名称"
}
```

`ApiKey` 是**智能助手使用的大模型服务密钥**。需要智能助手时填写，并设置匹配的服务地址和模型名称；只使用本地图片、文档语义搜索时，可以留空。保留合法的 `BaseURL`，当前启动代码会解析该地址。

本地语义搜索在服务器上计算向量，不需要联网调用 AI 模型 API，也不需要购买大模型 Token。下载好模型和程序依赖后，模型推理可以离线运行；模型本身较大，需要预留足够的磁盘、内存和计算资源。

发布包默认 `Enable: false`，因为模型目录初始为空。准备好模型和数据库向量扩展后，改为 `true` 并重启。三个搜索阈值可以先使用默认值。

### 4. 下载并放置 ONNX 模型

到 [Releases](https://github.com/fvffv/DriveApi/releases) 单独下载 ONNX 模型。程序压缩包**不包含模型**，将模型解压到程序同目录的 `Onnx` 中，最终结构应为：

```text
Onnx/
├── bgem3/
│   ├── bgem3.onnx
│   ├── model.onnx_data
│   └── sentencepiece.bpe.model
└── vit/
    ├── Vit-L-14.img.fp32.onnx
    ├── Vit-L-14.txt.fp32.onnx
    └── vocab.txt
```

保持下载包中的模型文件名和配套数据文件名；如果模型附带其他数据文件，也要一并保留。`.onnx` 引用的外部数据文件不能自行改名。不要多套一层 `Onnx/Onnx`，Linux/macOS 下还要注意文件名大小写。

开启 `AIConfig.Enable` 后，启动日志应显示“模型加载完成”。缺文件、模型与数据不匹配或内存不足时，程序会记录初始化失败并关闭模型调用链。文件上传后还需要等待后台向量处理完成，才能参与语义搜索。

### 5. ServerSettings：监听地址与端口

```json
"ServerSettings": {
  "IPv4": {
    "Ip": "0.0.0.0",
    "Port": 5085,
    "EnableSsl": false,
    "CertPath": "",
    "CertPassword": ""
  },
  "IPv6": null
}
```

`0.0.0.0` 监听所有 IPv4 网卡，客户端访问 `http://服务器IP:5085`；`127.0.0.1` 仅允许本机访问，适合前置反向代理。不使用 IPv6 时设为 `null`。需要 IPv6 时填写独立端口和 `::` 等地址。

公网部署应配置 HTTPS，可由反向代理处理，或启用 `EnableSsl` 并填写 PFX 证书路径和密码。当前实现遇到证书不存在会退回 HTTP，因此要检查启动日志和实际访问协议。

### 6. 文件、缓存与其他配置

- `FileConfig.LocalFilePath`：文件存储的基础路径，程序会在其下创建 `UserFiles`。多个位置用 `|` 分隔；服务部署建议使用绝对路径。
- `FileConfig.TempFilePath`：临时存储基础路径，程序会在其下创建 `Temp/ChunkData`。确保有足够空间及写入权限。
- `FileConfig.MaxFileSize`：单文件大小限制，单位为字节。使用反向代理时还要匹配代理的上传大小和超时设置。
- `UserConfig.DefaultTotalStorageGb`：新用户默认存储容量。
- `CacheConfig.CacheType: "MemoryCache"`、`MQConfig.MqType: "MemoryMQ"`：单实例部署可保留默认值，无需额外安装 Redis 或 RabbitMQ。
- `JwtConfig.SigningKey`：发布模板留空，首次正常启动会生成随机密钥并写回配置。之后请保留，随意替换会使已有登录令牌失效。

<a id="run"></a>

## 三、运行与前端网页

### 直接运行

Windows PowerShell：

```powershell
Set-Location C:\DriveApi
.\driveApi.exe
```

Linux：

```bash
cd /opt/drive-api
chmod +x driveApi
./driveApi
```

macOS：进入解压后的目录，执行 `chmod +x driveApi` 和 `./driveApi`。该发布流程未配置 Apple Developer 签名或公证；首次运行如被系统拦截，请在确认下载来源后通过系统隐私与安全设置允许运行。

先确认数据库和邮件初始化成功；启用了语义搜索时再确认模型加载成功。控制台和程序同目录的 `log.txt` 可用于排查问题。

### 挂载前端网页

将**编译后的前端产物内容**复制到 `wwwroot`，例如将 `dist` 里面的文件放进去，而不是放成 `wwwroot/dist/index.html`：

```text
wwwroot/
├── index.html
└── assets/
```

前端配置 API 地址为当前服务地址，或者使用同源 `/api/...` 请求。启动后访问 `http://服务器IP:5085`；当前根路由会跳转到 `/home`，并支持回退到 `index.html` 的前端页面路由。

空的 `wwwroot` 不会自动生成网页。更新网页时保留程序生成的 `wwwroot/driveassets`，不要把用户头像、缩略图一起清空。

<a id="services"></a>

## 四、注册为系统服务

先配置并以前台方式确认能够正常启动，再停止前台进程，安装服务，避免重复占用端口。程序内置服务名为 `driveApi`。

### Windows 服务

推荐放在不含空格的固定路径，如 `C:\DriveApi`。以**管理员身份**打开 PowerShell：

```powershell
Set-Location C:\DriveApi
.\driveApi.exe install
Get-Service driveApi
```

`install` 会创建自动启动的服务并立即启动。日常管理：

```powershell
Stop-Service driveApi
Start-Service driveApi
Restart-Service driveApi
```

卸载服务：

```powershell
Set-Location C:\DriveApi
.\driveApi.exe uninstall
```

Windows 服务的工作目录可能与交互式启动不同，SQLite 文件路径、用户文件与临时文件路径请使用绝对路径，并确认服务账户有访问权限。

### Linux systemd 服务

将程序放在 `/opt/drive-api`，然后执行：

```bash
cd /opt/drive-api
chmod +x driveApi
sudo ./driveApi install
sudo systemctl status driveApi --no-pager
```

`install` 会创建 `/etc/systemd/system/driveApi.service`，设置程序目录为工作目录，并启用开机启动和异常重启。当前内置安装器没有指定 `User`，以 root 安装后默认由 root 运行；需要专用服务账户时，在 unit 中设置 `User` / `Group` 并配置文件权限。

单文件里的原生库启动时需要释放到磁盘。为 systemd 明确指定可写的解压目录：

```bash
sudo systemctl edit driveApi
```

加入：

```ini
[Service]
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/opt/drive-api/.net
```

然后执行：

```bash
sudo mkdir -p /opt/drive-api/.net
sudo chmod 700 /opt/drive-api/.net
sudo systemctl daemon-reload
sudo systemctl restart driveApi
sudo journalctl -u driveApi -n 100 --no-pager
```

以上目录权限按默认 root 服务示例设置；使用专用账户时将该目录所有者改为对应账户。

常用命令：

```bash
sudo systemctl stop driveApi
sudo systemctl start driveApi
sudo systemctl restart driveApi
sudo journalctl -u driveApi -f
```

卸载服务：

```bash
cd /opt/drive-api
sudo ./driveApi uninstall
```

Windows/Linux 的 `uninstall` 只卸载服务，不负责删除网盘数据。

### macOS 后台运行

当前程序的 `install` / `uninstall` 仅实现 Windows 和 Linux。macOS 可通过 `launchd` 管理，例如创建 `~/Library/LaunchAgents/com.driveapi.server.plist`：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>com.driveapi.server</string>
  <key>ProgramArguments</key><array><string>/Users/你的用户名/DriveApi/driveApi</string></array>
  <key>WorkingDirectory</key><string>/Users/你的用户名/DriveApi</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>/Users/你的用户名/DriveApi/service.out.log</string>
  <key>StandardErrorPath</key><string>/Users/你的用户名/DriveApi/service.err.log</string>
</dict>
</plist>
```

将示例中的全部路径替换为真实绝对路径，创建 `LaunchAgents` 目录并保存文件后执行：

```bash
launchctl bootstrap "gui/$(id -u)" "$HOME/Library/LaunchAgents/com.driveapi.server.plist"
launchctl print "gui/$(id -u)/com.driveapi.server"
```

停止并卸载当前登录会话中的服务：

```bash
launchctl bootout "gui/$(id -u)" "$HOME/Library/LaunchAgents/com.driveapi.server.plist"
```

此方式属于用户登录后运行的 LaunchAgent，不是无人登录时运行的系统 LaunchDaemon；要取消以后登录自动加载，还需移走该 plist 文件。

<a id="maintenance"></a>

## 五、更新与备份

更新前停止服务并备份数据库、用户文件和配置。将新版程序及必要模板替换到原目录，**保留自己配置的 `appsettings.json`、`Onnx` 模型、网页和用户数据**，然后重启并检查日志。不要直接用发布包中的占位配置覆盖现有配置。

SQLite 使用 WAL 时，不要只在运行中复制 `drive.db` 而遗漏未合并事务；停止服务后备份，或使用 SQLite 支持的备份方式。PostgreSQL 使用数据库备份工具，并同时保存实际上传文件。

<a id="release"></a>

## 六、GitHub Actions 自动发布

工作流位于 **DriveApi 仓库**的 [`.github/workflows/release.yml`](.github/workflows/release.yml)。辅助文件位于 [`build/`](build/)，SDK 版本由 `global.json` 固定；当前源码使用 .NET 11 RC，升级 SDK 时同步检查项目的包版本。

提交工作流、`build/`、`global.json` 和数据库安装脚本后，在 DriveApi 仓库创建并推送 `v` 开头的标签，例如：

```bash
git tag v1.0.0
git push origin v1.0.0
```

工作流分别构建 Windows x64、Linux x64、Linux ARM64、macOS ARM64，完成后将四个平台的压缩包上传到该标签的 GitHub Release。手动运行普通分支只生成 Actions 构建产物；选择 `v` 开头的标签手动运行时也会发布。

发布流程会：

1. 使用隔离的源码副本，使用 `build/appsettings.release.json` 作为公开配置，连同程序内嵌的默认配置一起替换，避免携带开发环境凭据。
2. 自包含、单文件发布，打包原生运行库；不开启裁剪，避免 MVC、SqlSugar 和反射序列化的裁剪兼容性问题。
3. 在各平台运行单文件原生依赖检查，实际加载 SQLite、sqlite-vec 和 ONNX Runtime，并执行一次向量余弦查询；失败时不发布 Release。
4. 检查压缩包只有程序、`appsettings.json`、`EmailCodeTemplate.html` 和两个空目录；网页、模型、数据库、上传文件和日志不进入压缩包。
5. 将构建及原生依赖检查日志保存为 Actions artifacts。完整模型推理和实际邮件、数据库、上传业务仍需按部署环境验证。

四个平台必须全部构建成功才会进入 Release 上传。同一标签重跑会替换同名的四个程序包，不删除单独上传的模型附件。整个流程使用仓库自带的 `GITHUB_TOKEN`，无需另填个人访问令牌。

发布流程说明参考：[.NET 单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)、[GitHub 托管运行器](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)。

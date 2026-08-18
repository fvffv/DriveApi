using drive_api.Services.TrafficStatistics;
using System.Text;
using System.Xml.Linq;

namespace drive_api.Services.WebDav
{
    public class WebDavMiddleware
    {
        private readonly RequestDelegate _next;

        public WebDavMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // 【极其重要】：从请求作用域中解析出您自己实现的 Provider
            var provider = context.RequestServices.GetService<IWebDavProvider>();
            if (provider == null)
            {
                // 如果没有注册 IWebDavProvider，则直接跳过，防止崩溃
                await _next(context);
                return;
            }

            string method = context.Request.Method.ToUpper();

            // 1. 全局 OPTIONS 探测防断连放行
            if (method == "OPTIONS")
            {
                context.Response.Headers.Append("Allow", "OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY, LOCK, UNLOCK");
                context.Response.Headers.Append("DAV", "1, 2");
                context.Response.Headers.Append("MS-Author-Via", "DAV");
                context.Response.StatusCode = 200;
                return;
            }

            // 2. 触发：登录拦截事件
            if (!await IsAuthorizedAsync(context.Request, provider))
            {
                context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"WebDAV\"");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var pathBase = context.Request.PathBase.Value ?? "";
            var reqPath = Uri.UnescapeDataString(NormalizePath(context.Request.Path.Value));
            try
            {
                switch (method)
                {
                    case "PROPFIND": // 浏览目录
                        await HandlePropFind(context, pathBase, reqPath, provider);
                        break;

                    case "PROPPATCH": // 防止 Windows 报错
                        await HandlePropPatch(context, pathBase, reqPath);
                        break;

                    case "HEAD":
                    case "GET":  // 下载文件
                        await HandleGet(context, reqPath, method == "HEAD", provider);
                        break;

                    case "PUT": // 上传文件
                        await HandlePut(context, reqPath, provider);
                        break;

                    case "MKCOL": // 新建文件夹
                        await HandleMkcol(context, reqPath, provider);
                        break;

                    case "DELETE": // 删除
                        await HandleDelete(context, reqPath, provider);
                        break;

                    case "MOVE": // 移动/重命名
                        await HandleMove(context, pathBase, reqPath, provider);
                        break;

                    case "LOCK":   // 防假死
                        await HandleLock(context, reqPath);
                        break;

                    case "UNLOCK": // 解锁
                        context.Response.StatusCode = 204;
                        break;

                    default:
                        await _next(context);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebDAV Error] {method} {reqPath} - {ex.Message}");
                context.Response.StatusCode = 500;
            }
        }

        #region [操作实现 (全部委托给 Provider)]

        private async Task HandlePropFind(HttpContext context, string pathBase, string reqPath, IWebDavProvider provider)
        {
            // 触发事件：获取当前节点
            var targetNode = await provider.GetNodeAsync(reqPath);
            if (targetNode == null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            var depth = context.Request.Headers["Depth"].ToString();
            XNamespace dav = "DAV:";
            var multistatus = new XElement(dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", "DAV:"));

            // 1. 返回自身
            multistatus.Add(CreateResponseElement(dav, pathBase + reqPath, targetNode));

            // 2. 触发事件：获取子级列表
            if (targetNode.IsFolder && (depth == "1" || string.IsNullOrEmpty(depth)))
            {
                var children = await provider.GetChildrenAsync(reqPath);
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        string childHref = pathBase + NormalizePath(reqPath + "/" + child.Name);
                        multistatus.Add(CreateResponseElement(dav, childHref, child));
                    }
                }
            }

            var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), multistatus);
            context.Response.StatusCode = 207;
            context.Response.ContentType = "application/xml; charset=\"utf-8\"";
            context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");

            await context.Response.WriteAsync(xml.ToString(), Encoding.UTF8);
        }

        private async Task HandleGet(HttpContext context, string reqPath, bool isHeadOnly, IWebDavProvider provider)
        {
            var node = await provider.GetNodeAsync(reqPath);
            if (node == null || node.IsFolder)
            {
                context.Response.StatusCode = 404;
                return;
            }


            if (isHeadOnly)
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = node.Length;
                return;
            }


            using var stream = await provider.GetFileStreamAsync(reqPath);

            if (stream == null)
            {
                // 如果返回了 null，说明数据库里有记录，但物理硬盘上文件丢了，或者代码报错了
                // 此时千万不能设 Content-Length，直接报 404 即可
                context.Response.StatusCode = 404;
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = node.Length;

            // 流量统计
            // 1. 从 DI 容器获取 TrafficStoreService[cite: 4, 6]
            var trafficStore = context.RequestServices.GetService<TrafficStoreService>();

            // 2. 使用 TrackingStream 包装原始文件流
            using var trackingStream = new TrackingStream(stream);

            try
            {
                // 3. 复制被包装的流到响应体，TrackingStream 会自动记录读取的字节数[cite: 4, 6]
                await trackingStream.CopyToAsync(context.Response.Body);
            }
            finally
            {
                // 4. 记录用户下载流量 (此处读取文件流的字节数 == 客户端下载的字节数)[cite: 4]
                if (trafficStore != null && trackingStream.TotalRead > 0)
                {
                    trafficStore.AddUpload(trackingStream.TotalRead);
                }
            }
            // ====== 流量统计适配：结束 ======
        }

        private async Task HandlePut(HttpContext context, string reqPath, IWebDavProvider provider)
        {
            // ====== 流量统计适配：开始 ======
            var trafficStore = context.RequestServices.GetService<TrafficStoreService>();

            // 用 TrackingStream 包装客户端上传的请求流[cite: 4]
            using var trackingStream = new TrackingStream(context.Request.Body);

            try
            {
                // 将包装后的流传递给 Provider 进行文件保存[cite: 4, 6]
                var result = await provider.PutFileAsync(reqPath, trackingStream);
                context.Response.StatusCode = result.IsSuccess ? result.StatusCode : (result.StatusCode == 0 ? 500 : result.StatusCode);
            }
            finally
            {
                // 记录用户上传流量 (此处从 Request.Body 读取的字节数 == 客户端上传的字节数)[cite: 4]
                if (trafficStore != null && trackingStream.TotalRead > 0)
                {
                    trafficStore.AddDownload(trackingStream.TotalRead);
                }
            }
            // ====== 流量统计适配：结束 ======
        }

        private async Task HandleMkcol(HttpContext context, string reqPath, IWebDavProvider provider)
        {
            var result = await provider.CreateFolderAsync(reqPath);
            context.Response.StatusCode = result.IsSuccess ? 201 : result.StatusCode;
        }

        private async Task HandleDelete(HttpContext context, string reqPath, IWebDavProvider provider)
        {
            var result = await provider.DeleteAsync(reqPath);
            context.Response.StatusCode = result.IsSuccess ? 204 : result.StatusCode;
        }

        private async Task HandleMove(HttpContext context, string pathBase, string reqPath, IWebDavProvider provider)
        {
            string destinationHeader = context.Request.Headers["Destination"].ToString();
            if (string.IsNullOrEmpty(destinationHeader))
            {
                context.Response.StatusCode = 400;
                return;
            }

            var destUri = new Uri(destinationHeader);
            string destPath = NormalizePath(Uri.UnescapeDataString(destUri.AbsolutePath).Substring(pathBase.Length));

            // 触发事件：移动或重命名
            var result = await provider.MoveAsync(reqPath, destPath);
            context.Response.StatusCode = result.IsSuccess ? 201 : result.StatusCode;
        }

        private async Task HandlePropPatch(HttpContext context, string pathBase, string reqPath)
        {
            context.Response.StatusCode = 207;
            context.Response.ContentType = "application/xml; charset=\"utf-8\"";
            string currentHref = pathBase + reqPath;
            string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
            <D:multistatus xmlns:D=""DAV:""><D:response><D:href>{currentHref}</D:href><D:propstat><D:prop><D:creationdate/><D:getlastmodified/></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>";
            await context.Response.WriteAsync(xml, Encoding.UTF8);
        }

        private async Task HandleLock(HttpContext context, string reqPath)
        {
            string lockToken = "urn:uuid:" + Guid.NewGuid().ToString();
            context.Response.Headers.Append("Lock-Token", $"<{lockToken}>");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/xml; charset=\"utf-8\"";
            string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
            <D:prop xmlns:D=""DAV:""><D:lockdiscovery><D:activelock><D:locktype><D:write/></D:locktype><D:lockscope><D:exclusive/></D:lockscope><D:depth>infinity</D:depth><D:locktoken><D:href>{lockToken}</D:href></D:locktoken></D:activelock></D:lockdiscovery></D:prop>";
            await context.Response.WriteAsync(xml, Encoding.UTF8);
        }

        #endregion

        #region [辅助方法]

        private async Task<bool> IsAuthorizedAsync(HttpRequest request, IWebDavProvider provider)
        {
            if (!request.Headers.ContainsKey("Authorization")) return false;
            var authHeader = request.Headers["Authorization"].ToString();
            if (authHeader.StartsWith("Basic "))
            {
                var encoded = authHeader.Substring("Basic ".Length).Trim();
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var parts = decoded.Split(':', 2);
                if (parts.Length == 2)
                {
                    // 解析出账号密码后，抛给实现类去决定是否通过！
                    return await provider.AuthenticateAsync(parts[0], parts[1]);
                }
            }
            return false;
        }

        private string NormalizePath(string? path) => string.IsNullOrEmpty(path) || path == "/" ? "/" : "/" + path.Trim('/');

        private XElement CreateResponseElement(XNamespace dav, string href, WebDavNode node)
        {

            string encodedHref = string.Join("/", href.Split('/').Select(segment => Uri.EscapeDataString(segment)));

            var resourceType = node.IsFolder ? new XElement(dav + "resourcetype", new XElement(dav + "collection")) : new XElement(dav + "resourcetype");
            string creationDate = node.Created.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string lastModified = node.Modified.ToString("R");

            var prop = new XElement(dav + "prop",
                new XElement(dav + "displayname", node.Name), // 这里保持明文，供用户展示阅读
                new XElement(dav + "creationdate", creationDate),
                new XElement(dav + "getlastmodified", lastModified),
                resourceType
            );

            if (!node.IsFolder)
            {
                prop.Add(new XElement(dav + "getcontentlength", node.Length.ToString()));
                prop.Add(new XElement(dav + "getcontenttype", "application/octet-stream"));
            }

            return new XElement(dav + "response",
                // 这里使用编码后的 encodedHref
                new XElement(dav + "href", encodedHref + (node.IsFolder && !encodedHref.EndsWith("/") ? "/" : "")),
                new XElement(dav + "propstat", prop, new XElement(dav + "status", "HTTP/1.1 200 OK"))
            );
        }

        #endregion
    }
}

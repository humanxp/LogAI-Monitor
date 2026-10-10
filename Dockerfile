# LogAI Monitor (.NET 8) - production image.
#
# Build context needs src/, templates/ and wwwroot/: the Jinja engine reads the
# templates and the static endpoint serves wwwroot from the content root, so both
# have to sit next to the published assembly.
#
#   docker build -t logaimonitor-cs .
#   docker run -d --name logaimonitor \
#     -p 5059:5059 -p 514:514/udp -p 515:515/tcp \
#     -v /var/run/docker.sock:/var/run/docker.sock:ro \
#     -e REDIS_HOST=<redis host> -e REDIS_DB=0 \
#     -e SECRET_KEY=<same value as the Python deployment> \
#     -e OLLAMA_MODEL=<model> -e AI_API_KEY=<key> \
#     logaimonitor-cs
#
# SECRET_KEY matters for a switch-over: session cookies are HMAC signed with it,
# so reusing the Python deployment's value keeps everyone logged in.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# 构建网络的网关会对 api.nuget.org 做 DNS 投毒（解析成 198.18.x.x 假地址），
# 这里用 nuget.config 把还原源切到国内可达的华为云镜像，否则 dotnet publish 还原失败。
COPY nuget.config ./nuget.config
# restore / publish 分离：先只拷 .csproj 做 restore，再拷源码做 publish。
# 只要依赖（.csproj）没变，restore 那一层就一直是缓存命中；改源码只会重做后面
# 的编译层。原先 restore 和 publish 挤在同一条 RUN 里，改一次源码就要把
# ~150MB 的 NuGet 包层整个重存一份 —— BuildKit 缓存就是这么堆到 33GB 的。
COPY src/LogAI.Core/LogAI.Core.csproj src/LogAI.Core/
COPY src/LogAI.Web/LogAI.Web.csproj src/LogAI.Web/
RUN dotnet restore src/LogAI.Web/LogAI.Web.csproj --nologo
COPY src/ ./src/
RUN dotnet publish src/LogAI.Web/LogAI.Web.csproj -c Release -o /app --no-restore --nologo

# 运行时也用 sdk:8.0 而不是 aspnet:8.0：构建网络的网关对 mcr.microsoft.com 同样做
# DNS 投毒，且 aspnet:8.0 未被本地缓存，pull 会 EOF。sdk 镜像自带完整 ASP.NET Core
# 运行时、本地已缓存，改用它能离线构建（代价是最终镜像更大，可接受）。
FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /app
# Syslog timestamps and the diagnostics output are rendered in this timezone.
ENV TZ=Asia/Shanghai
COPY --from=build /app ./
COPY templates/ ./templates/
COPY wwwroot/ ./wwwroot/

ENV ASPNETCORE_URLS=http://0.0.0.0:5059
ENV DOTNET_EnableDiagnostics=0
EXPOSE 5059

# 存活探测：镜像里没有 curl/wget，用 bash 内建的 /dev/tcp。
# 更深的健康判据看应用日志里每分钟一行的 [Health] ok ... 心跳。
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD timeout 3 bash -c 'exec 3<>/dev/tcp/127.0.0.1/5059' || exit 1
EXPOSE 514/udp
EXPOSE 515/tcp
ENTRYPOINT ["dotnet", "LogAI.Web.dll"]

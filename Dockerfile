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
COPY src/ ./src/
RUN dotnet publish src/LogAI.Web/LogAI.Web.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
# Syslog timestamps and the diagnostics output are rendered in this timezone.
ENV TZ=Asia/Shanghai
COPY --from=build /app ./
COPY templates/ ./templates/
COPY wwwroot/ ./wwwroot/

ENV ASPNETCORE_URLS=http://0.0.0.0:5059
ENV DOTNET_EnableDiagnostics=0
EXPOSE 5059
EXPOSE 514/udp
EXPOSE 515/tcp
ENTRYPOINT ["dotnet", "LogAI.Web.dll"]

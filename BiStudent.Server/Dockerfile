# Сервер API BiStudent. Контекст сборки — корень репозитория:
#   docker build -f BiStudent.Server/Dockerfile -t bistudent-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BiStudent.Core/BiStudent.Core.csproj BiStudent.Core/
COPY BiStudent.Server/BiStudent.Server.csproj BiStudent.Server/
RUN dotnet restore BiStudent.Server/BiStudent.Server.csproj -r linux-x64
COPY BiStudent.Core/ BiStudent.Core/
COPY BiStudent.Server/ BiStudent.Server/
RUN dotnet publish BiStudent.Server/BiStudent.Server.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# OpenCvSharp4.official.runtime.linux-x64: OpenMP и базовые графические библиотеки; curl — для healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 libgdiplus libgtk2.0-0 libtesseract5 curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown -R app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/data/bistudent.db"
# Постоянный том /data подключается в Railway или Docker Compose.
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "BiStudent.Server.dll"]

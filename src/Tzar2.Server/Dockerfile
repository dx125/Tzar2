# Tzar2 relay/lobby service (TDD §19.7, ADR 0161). Build from the repository root:
#   docker build -f src/Tzar2.Server/Dockerfile -t tzar2-server .
#   docker run -p 47625:47625/udp -p 8080:8080 -v tzar2-data:/data tzar2-server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/Tzar2.Analyzers/ src/Tzar2.Analyzers/
COPY src/Tzar2.Core/ src/Tzar2.Core/
COPY src/Tzar2.Net/ src/Tzar2.Net/
COPY src/Tzar2.Server/ src/Tzar2.Server/
RUN dotnet publish src/Tzar2.Server/Tzar2.Server.csproj -c Release -o /out --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out ./
RUN mkdir -p /data/desync && chown -R app:app /data
USER app
ENV TZAR2_DESYNC_DIR=/data/desync \
    DOTNET_gcServer=0
EXPOSE 47625/udp 8080/tcp
VOLUME /data
ENTRYPOINT ["dotnet", "Tzar2.Server.dll"]

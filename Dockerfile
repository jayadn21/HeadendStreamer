FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Install FFmpeg and video tools
RUN apt-get update && \
    apt-get install -y ffmpeg v4l-utils && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["HeadendStreamer.Web/HeadendStreamer.Web.csproj", "HeadendStreamer.Web/"]
RUN dotnet restore "HeadendStreamer.Web/HeadendStreamer.Web.csproj"
COPY . .
WORKDIR "/src/HeadendStreamer.Web"
RUN dotnet build "HeadendStreamer.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "HeadendStreamer.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create non-root user
RUN groupadd -r headend && \
    useradd -r -g headend -s /bin/false headend && \
    chown -R headend:headend /app

USER headend

ENTRYPOINT ["dotnet", "HeadendStreamer.Web.dll"]
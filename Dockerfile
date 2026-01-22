FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY *.csproj ./
COPY *.sln ./
RUN dotnet restore

COPY . ./

RUN dotnet publish ConfluencePublisher.csproj \
    -c Release \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl gnupg ca-certificates \
    libx11-xcb1 libxcomposite1 libxcursor1 libxdamage1 libxi6 libxtst6 \
    libnss3 libcups2 libxss1 libxrandr2 \
    libasound2t64 \
    libatk1.0-0 libatk-bridge2.0-0 \
    libgtk-3-0 libgbm1 \
    libpango-1.0-0 libcairo2 \
    fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://deb.nodesource.com/setup_lts.x | bash - \
    && apt-get install -y nodejs \
    && npm install -g @mermaid-js/mermaid-cli \
    && npm cache clean --force

WORKDIR /app
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "ConfluencePublisher.dll"]
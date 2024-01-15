#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VARIANT="20-bullseye"
ARG DOTNET_VERSION="6.0"

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /
COPY . .
WORKDIR "/src/BifrostQL.Host"
RUN dotnet restore "BifrostQL.Host.csproj"
RUN dotnet build "BifrostQL.Host.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "BifrostQL.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/vscode/devcontainers/typescript-node:${VARIANT} AS edit-db-base
WORKDIR /
COPY . .
WORKDIR "/examples/edit-db"
RUN npm install

FROM edit-db-base AS edit-db-build
RUN npm run build
RUN npm run build-storybook



FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BifrostQL.Host.dll"]
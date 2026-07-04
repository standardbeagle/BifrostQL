#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

# Projects multi-target net8.0/net9.0/net10.0; default the image build to the
# newest supported SDK/runtime rather than the long-EOL 6.0.
ARG DOTNET_VERSION="10.0"

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

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BifrostQL.Host.dll"]

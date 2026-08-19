# ---------------------------------------------------------------------------
# Build: compila la solucion completa y publica la API.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore Cocos.slnx
RUN dotnet publish src/Cocos.Api/Cocos.Api.csproj -c Release -o /app --no-restore

# ---------------------------------------------------------------------------
# Test: corre las tres suites contra el Postgres del compose.
#
# Los tests de integracion apuntan al servicio 'db' via COCOS_TEST_DB, asi que
# NO hace falta exponer el socket de Docker dentro del contenedor ni recurrir a
# Docker-in-Docker para levantar TestContainers.
# ---------------------------------------------------------------------------
FROM build AS test
WORKDIR /src
ENTRYPOINT ["dotnet", "test", "Cocos.slnx", "--nologo"]

# ---------------------------------------------------------------------------
# Runtime: imagen final, sin SDK.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Usuario sin privilegios (viene predefinido en las imagenes de .NET).
USER $APP_UID

ENTRYPOINT ["dotnet", "Cocos.Api.dll"]

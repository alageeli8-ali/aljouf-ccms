# صورة الإنتاج — تُبنى في بيئة لها وصول إلى npm وNuGet وMCR (خط التكامل المستمر أو خادم البناء).
# الإنتاج يستخدم مشغّل Npgsql الرسمي (UseNpgsql=true)، لا الموصل المصغّر الخاص بالتطوير دون اتصال (ADR-015).

FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# صفحة Swagger لا تُضمّن في صورة الإنتاج (OpenApi:Enabled=false في الإنتاج)
RUN npx tsc -b && npx vite build --outDir /out/wwwroot --emptyOutDir

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/backend/ src/backend/
COPY db/migrations/ db/migrations/
RUN dotnet publish src/backend/Ccms.Host/Ccms.Host.csproj -c Release -p:UseNpgsql=true -o /out/app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN groupadd --system ccms && useradd --system --gid ccms --home /app ccms \
 && mkdir -p /var/lib/ccms/storage /var/lib/ccms/keys && chown -R ccms:ccms /var/lib/ccms
WORKDIR /app
COPY --from=api /out/app ./
COPY --from=web /out/wwwroot ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DataProtection__KeysPath=/var/lib/ccms/keys \
    Storage__Path=/var/lib/ccms/storage
USER ccms
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s CMD wget -qO- http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "Ccms.Host.dll"]

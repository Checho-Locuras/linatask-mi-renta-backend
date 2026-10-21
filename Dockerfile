# Mi renta API — imagen independiente (no comparte nada con el monolito de LinaTask).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MiRenta.slnx ./
COPY src/MiRenta.Domain/MiRenta.Domain.csproj src/MiRenta.Domain/
COPY src/MiRenta.Infrastructure/MiRenta.Infrastructure.csproj src/MiRenta.Infrastructure/
COPY src/MiRenta.Application/MiRenta.Application.csproj src/MiRenta.Application/
COPY src/MiRenta.Api/MiRenta.Api.csproj src/MiRenta.Api/
RUN dotnet restore src/MiRenta.Api/MiRenta.Api.csproj
COPY src/ src/
RUN dotnet publish src/MiRenta.Api/MiRenta.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# pdftotext para extraer texto de PDF (sin ejecutar contenido del documento).
RUN apt-get update && apt-get install -y --no-install-recommends poppler-utils && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_ENVIRONMENT=Production
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MiRenta.Api.dll"]

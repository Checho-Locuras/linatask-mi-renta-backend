# Mi renta — microservicio de declaración de renta (formulario 210, AG 2025)

Mi renta es un servicio **independiente** del monolito de LinaTask. Ayuda a una persona natural residente con rentas
de trabajo laborales a preparar un **borrador** del formulario 210 del año gravable 2025. Tiene:

- su propia identidad,
- su propia base de datos,
- su propio bucket privado,
- sus propios trabajadores de fondo,
- su propio despliegue.

El frontend de LinaTask (`linatask-admin-web`, ruta `/mi-renta`) lo consume directamente. No hay proxy en el monolito.

> Mi renta **no presenta** la declaración, **no pide ni guarda la contraseña de la DIAN** y **no inventa reglas**:
> - Cada regla cita su norma.
> - Las reglas están *pendientes de aprobación profesional*.
> - Lo que no tiene fuente verificable bloquea el cálculo.
>
> Un contador autorizado debe revisar y aprobar el borrador antes de exportarlo.

## Estructura

```
src/MiRenta.Domain          Entidades, máquina de estados, catálogo de reglas AG2025 y motor 210 (función pura, decimal)
src/MiRenta.Infrastructure  EF Core (esquema mi_renta), migraciones, S3/R2 privado, extracción (pdftotext/OpenXml/CSV),
                            cola de trabajos (SKIP LOCKED), correo SMTP, adaptadores DIAN e IA (ambos desactivados)
src/MiRenta.Application     Autenticación, expedientes, asistente, documentos, conciliación, cálculo, borrador, contador,
                            recordatorios
src/MiRenta.Api             Minimal APIs /api/v1/my-rent, JWT propio, CORS restrictivo, rate limiting, ProblemDetails, health
tests/MiRenta.Tests         Unitarias (motor, archivos, criptografía, IA, DIAN) + integración (TestServer + PostgreSQL + S3)
tools/MiRenta.MigrationScaffolder  Genera migraciones sin la herramienta global dotnet-ef
```

Más detalle en [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Ejecutar en local

Requisitos:
- .NET 10 SDK,
- PostgreSQL 14+,
- `pdftotext` (paquete `poppler-utils`),
- opcional: un S3 local (p. ej. `s3rver`) o un bucket R2 de desarrollo.

```bash
export ConnectionStrings__MiRenta="Host=localhost;Database=mi_renta;Username=<usuario>;Password=<contraseña>"
export Jwt__SigningKey="$(openssl rand -base64 48)"
export App__UrlSigningKey="$(openssl rand -base64 48)"
export ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080
dotnet run --project src/MiRenta.Api
```

Si no hay `Storage__*` configurado, en desarrollo usa un almacenamiento en memoria. **Producción lo rechaza al
iniciar.** Las migraciones se aplican al arrancar (`Database__MigrateOnStartup=true`).

Variables completas (sin secretos): [.env.example](.env.example).

## Pruebas

```bash
export MIRENTA_TEST_POSTGRES="Host=localhost;Port=5432;Username=<rol-con-CREATEDB>;Password=<contraseña>"
export MIRENTA_TEST_S3_ENDPOINT=http://localhost:4569   # s3rver -p 4569 --configure-bucket mi-renta-test
dotnet test
```

- Cada clase de integración crea y elimina su propia base de datos.
- Todos los archivos de prueba son sintéticos.

## Nueva migración

```bash
dotnet build tools/MiRenta.MigrationScaffolder
dotnet tools/MiRenta.MigrationScaffolder/bin/Debug/net10.0/MiRenta.MigrationScaffolder.dll <Nombre>
```

## Despliegue

- `Dockerfile`: runtime ASP.NET 10 con `poppler-utils`, usuario no root, puerto 8080.
- `railway.json`: healthcheck en `/health/ready`.

Requiere su propio servicio, su propia base PostgreSQL y un bucket R2 privado exclusivo. Configura
`App__AllowedOrigins__0` con el origen https del frontend.

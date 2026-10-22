# Mi renta — backend (.NET 10)

Mi renta es una API independiente para preparar el **borrador** de la declaración de renta de personas naturales,
formulario 210 del año gravable 2025, para residentes con rentas de trabajo.

No depende de LinaTask: tiene su propia identidad, base de datos, migraciones, claves, almacenamiento y trabajadores
de fondo. La consume el frontend independiente
[`linatask-mi-renta-frontend`](https://github.com/Checho-Locuras/linatask-mi-renta-frontend).

> **Estado: listo para pruebas locales controladas, no para usuarios finales.**
> - Las reglas tributarias citan su norma, pero siguen **pendientes de aprobación profesional**.
> - El mapeo de casillas del formulario 210 no está verificado.
> - Mi renta no presenta declaraciones ni pide la contraseña de la DIAN.
> - IA y DIAN están desactivadas.

## Solución y proyectos

```
MiRenta.sln
├─ src/MiRenta.Domain            Entidades, máquina de estados, catálogo de reglas AG2025, motor 210 (función pura, decimal)
├─ src/MiRenta.Infrastructure    EF Core (esquema mi_renta) y migraciones; R2/S3, disco local o memoria; extracción
│                                (pdftotext/OpenXml/CSV); cola SKIP LOCKED; SMTP/.eml; adaptadores DIAN e IA (desactivados)
├─ src/MiRenta.Application       Autenticación, expedientes, asistente, documentos, conciliación, cálculo, borrador,
│                                contador, recordatorios
├─ src/MiRenta.Api               Minimal APIs /api/v1/my-rent, JWT propio, CORS restrictivo, rate limiting, health checks
├─ tests/MiRenta.Tests           Unitarias + integración (TestServer + PostgreSQL + S3 local)
└─ tools/MiRenta.MigrationScaffolder   Genera migraciones sin la herramienta global dotnet-ef
```

Para abrirla:
- **Visual Studio 2022:** abre `MiRenta.sln` y elige el perfil `http` del proyecto `MiRenta.Api`.
- **VS Code:** con la extensión C# Dev Kit, `code .` en la raíz; el explorador de soluciones carga `MiRenta.sln`.

## Ejecutar en local

1. Prepara PostgreSQL y los **3 secretos mínimos** con User Secrets: [docs/CONFIGURATION.md](docs/CONFIGURATION.md),
   secciones 3 y 4.
2. Compila y arranca:

```powershell
dotnet restore
dotnet build
dotnet run --project src\MiRenta.Api --launch-profile http   # http://localhost:5080
Invoke-RestMethod http://localhost:5080/health/ready
```

En desarrollo no necesitas R2 ni SMTP:
- los documentos se guardan en `src/MiRenta.Api/.local-storage/`;
- los correos, con el enlace de verificación, en `src/MiRenta.Api/.local-mail/`.

Ambas carpetas están ignoradas por Git y prohibidas en producción.

## Pruebas

```powershell
$env:MIRENTA_TEST_POSTGRES = "Host=localhost;Port=5432;Username=<rol-con-CREATEDB>;Password=<contraseña>"
npx s3rver -d .s3 -p 4569 --configure-bucket mi-renta-test   # en otra terminal
dotnet test
```

## Documentación

- [docs/CONFIGURATION.md](docs/CONFIGURATION.md): inventario de secretos, User Secrets, PostgreSQL y limitaciones locales.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): arquitectura, seguridad, motor tributario, endpoints y limitaciones.
- [.env.example](.env.example): variables para contenedores, sin valores reales.

## Nueva migración

```powershell
dotnet build tools\MiRenta.MigrationScaffolder
dotnet tools\MiRenta.MigrationScaffolder\bin\Debug\net10.0\MiRenta.MigrationScaffolder.dll <Nombre>
```

Si tienes `dotnet-ef` instalado, también funciona:

```powershell
dotnet ef migrations add <Nombre> --project src\MiRenta.Infrastructure --startup-project src\MiRenta.Api
```

## Despliegue (fase posterior; no realizado)

- `Dockerfile`: ASP.NET 10 con `poppler-utils`, usuario no root, puerto 8080.
- `railway.json`: healthcheck en `/health/ready`.

Requiere su propio servicio, base PostgreSQL, bucket R2 privado y SMTP. Ver la tabla de producción en
`docs/CONFIGURATION.md`.

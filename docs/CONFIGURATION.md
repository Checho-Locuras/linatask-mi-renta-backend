# Configuración y secretos de Mi renta (backend)

Este inventario sale del código real. Las fuentes son:
- `src/MiRenta.Api/Program.cs` y `Support.cs` (`StartupValidation`);
- las clases `*Options` (`JwtOptions`, `AppOptions`, `StorageOptions`, `EmailOptions`, `DianOptions`, `AiOptions`);
- `appsettings.json` y `appsettings.Development.json`.

Mi renta **no usa ninguna credencial de LinaTask**: ni su base de datos, ni su JWT, ni sus buckets, ni su SMTP. No
necesita credenciales de la DIAN.

**Cómo se escriben las claves en ASP.NET Core**
- En JSON y User Secrets se usa `:` (por ejemplo `Jwt:SigningKey`).
- En variables de entorno se usa `__` (`Jwt__SigningKey`). En arreglos se añade el índice: `App__AllowedOrigins__0`.

**Orden de carga (el último gana)**
1. `appsettings.json`
2. `appsettings.{Entorno}.json`
3. User Secrets (**solo si `ASPNETCORE_ENVIRONMENT=Development`**)
4. Variables de entorno
5. Argumentos de línea de comandos

Lo comprobamos ejecutando la API: con `Production` los User Secrets no se cargan y la API se niega a arrancar.

## 1. Mínimo para arrancar en local (3 valores)

| Clave | Tipo | Cómo obtenerla |
|---|---|---|
| `ConnectionStrings:MiRenta` | Secreto | Tu PostgreSQL local (sección 4). |
| `Jwt:SigningKey` | Secreto que generas tú | Clave aleatoria (sección 3). |
| `App:UrlSigningKey` | Secreto que generas tú | Otra clave aleatoria, **distinta** de la anterior. |

Todo lo demás tiene un valor por defecto de desarrollo en `appsettings.Development.json`:
- **CORS** para `http://localhost:4200`.
- **Correos:** se escriben como archivos `.eml` en `src/MiRenta.Api/.local-mail/`.
- **Documentos:** se guardan en `src/MiRenta.Api/.local-storage/` y persisten entre reinicios.
- **IA y DIAN:** desactivadas.

Las dos carpetas locales están en `.gitignore`. Producción rechaza ambas al iniciar.

## 2. Inventario completo

Leyenda:
- **Tipo:** S = secreto · C = configuración no secreta · G = valor que generas tú (aleatorio).
- **Local / Prod:** O = obligatoria · Op = opcional · — = no aplica · ✗ = prohibida.

| Clave | Tipo | Local | Prod | Para qué sirve | Dónde obtenerla / cómo generarla | Qué falla sin ella | Dónde configurarla |
|---|---|---|---|---|---|---|---|
| `ConnectionStrings:MiRenta` | S | O | O | Base PostgreSQL **propia** (esquema `mi_renta`, migraciones automáticas). | Local: [PostgreSQL](https://www.postgresql.org/download/) (sección 4). Prod: un servicio PostgreSQL **nuevo** ([Railway PostgreSQL](https://docs.railway.com/guides/postgresql)), nunca `linatask_db`. Formato Npgsql ([parámetros](https://www.npgsql.org/doc/connection-string-parameters.html)): `Host=…;Port=5432;Database=mi_renta;Username=…;Password=…`, más `;SSL Mode=Require` en prod. | La API no arranca. | Local: User Secrets. Prod: variable sellada en Railway. |
| `Jwt:SigningKey` | S, G | O | O | Firma HS256 de los tokens de acceso de Mi renta. | Sección 3. Al menos 32 bytes (se recomiendan 48). | La API no arranca. | Igual que la anterior. |
| `App:UrlSigningKey` | S, G | O | O | HMAC de enlaces de descarga (5 min) y de baja de recordatorios. | Sección 3. Distinta de `Jwt:SigningKey` (la API lo verifica). | La API no arranca. Si la rotas, caducan los enlaces emitidos. | Igual que la anterior. |
| `App:FrontendBaseUrl` | C | Op (ya está) | O (https) | Base de los enlaces de verificación (`/verificar-correo`), restablecimiento (`/restablecer`) y baja (`/recordatorios/baja`). | URL del frontend independiente. | En prod la API no arranca. Los enlaces de los correos quedarían mal. | `appsettings.Development.json` / Railway. |
| `App:AllowedOrigins:0..n` | C | Op (ya está) | O (https, sin `*`) | Lista blanca de CORS. | Origen exacto del frontend, sin barra final. | El navegador bloquea las llamadas. En prod la API no arranca. | Igual que la anterior. |
| `App:AccountantEmails:0..n` | C | Op | Op | Correos que reciben el rol Contador al verificar su cuenta. | Decisión tuya. | Nadie puede aprobar borradores, ingresar anticipos ni exportar. | User Secrets / Railway. |
| `App:RulesAdminEmails:0..n` | C | Op | Op | Rol para aprobar reglas tributarias, con nota de la fuente. | Decisión tuya: un profesional tributario. | Las reglas siguen pendientes y los cálculos quedan «Preliminar» (no bloquea). | User Secrets / Railway. |
| `App:PrivacyNoticeVersion`, `App:DownloadUrlMinutes`, `App:RunWorkers` | C | Op | Op | Versión del aviso aceptado (`2026-10-v1`); minutos de vida de los enlaces de descarga (5, rango 1–10); activa los trabajadores de fondo (`true`). | — | Con `RunWorkers=false` no se procesan documentos ni se envían correos. | `appsettings` / Railway. |
| `Storage:LocalDirectory` | C | Op (ya está) | ✗ | Documentos en disco local, solo desarrollo. | — | Sin carpeta ni R2: almacenamiento **en memoria** y los archivos se pierden al reiniciar. | `appsettings.Development.json`. |
| `Storage:ServiceUrl` | C | Op | O | Endpoint S3 de R2: `https://<ACCOUNT_ID>.r2.cloudflarestorage.com`. | [dash.cloudflare.com](https://dash.cloudflare.com) → R2 → Account ID ([API S3 de R2](https://developers.cloudflare.com/r2/api/s3/api/)). | En prod la API no arranca. | User Secrets / Railway. |
| `Storage:BucketName` | C | Op | O | Bucket **privado y exclusivo** de Mi renta, sin dominio público ni r2.dev. | R2 → Create bucket ([docs](https://developers.cloudflare.com/r2/buckets/create-buckets/)). | Igual que la anterior. | Igual que la anterior. |
| `Storage:AccessKeyId` / `Storage:SecretAccessKey` | S | Op | O | Credencial S3 del token de R2. | R2 → Manage R2 API Tokens → Create API token, permiso *Object Read & Write* **limitado a ese bucket** ([docs](https://developers.cloudflare.com/r2/api/tokens/)). El secreto se muestra una sola vez. | Igual que la anterior. | Igual que la anterior, sellada en Railway. |
| `Storage:MaxFileBytes` | C | Op | Op | Tamaño máximo por archivo (15 MB por defecto). | — | — | `appsettings` / Railway. |
| `Email:PickupDirectory` | C | Op (ya está) | ✗ | Correos como `.eml` en una carpeta local, solo desarrollo. | — | Sin carpeta ni SMTP los correos quedan `Skipped`: nadie puede verificar su cuenta. | `appsettings.Development.json`. |
| `Email:SmtpHost` / `Email:SmtpPort` / `Email:UseSsl` | C | Op | O (host) | Servidor SMTP de envío (puerto 587 y TLS por defecto). | Tu proveedor, por ejemplo [Amazon SES](https://docs.aws.amazon.com/ses/latest/dg/smtp-credentials.html) o [SendGrid](https://www.twilio.com/docs/sendgrid/for-developers/sending-email/integrating-with-the-smtp-api). | En prod la API no arranca. | Railway. |
| `Email:Username` / `Email:Password` | S | Op | Según proveedor | Credencial SMTP de envío, **no** tu contraseña personal. | La genera el proveedor. En Gmail sería una [contraseña de aplicación](https://support.google.com/accounts/answer/185833). | No salen correos. | Railway (sellada). |
| `Email:From` | C | Op | O | Remitente, por ejemplo `Mi renta <no-reply@tu-dominio>`. | Dominio verificado con SPF y DKIM en tu proveedor. | Los correos caen en spam o los rechazan. | Railway. |
| `Email:MaxRemindersPerWeek` | C | Op | Op | Tope de recordatorios por usuario y semana (2). | — | — | `appsettings` / Railway. |
| `Ai:Provider` | C | `Disabled` | `Disabled` | `Disabled` o `Anthropic`. Cualquier otro valor impide arrancar. | — | Nada: la extracción determinista funciona sin IA. | No configurar por ahora. |
| `Ai:ApiKey` | S | — | — (solo si se activa la IA) | Clave del proveedor de IA. | [console.anthropic.com → API keys](https://console.anthropic.com/settings/keys), clave dedicada con límite de gasto. | Con `Provider=Anthropic` y sin clave, la API no arranca. | **No activar** sin aprobar el tratamiento de datos. |
| `Ai:Model` / `Ai:BaseUrl` / `Ai:MaxExcerptChars` | C | Op | Op | Modelo, endpoint y tamaño máximo del extracto minimizado (4000). | — | — | — |
| `Dian:Enabled` | C | `false` | `false` | Pre-consulta «¿debo declarar?» contra endpoints **no oficiales**. | Sin credenciales. El reCAPTCHA lo resuelve el usuario: **no hay clave de CAPTCHA** que configurar. | Con `false` solo se ofrece el enlace a la [consulta oficial](https://www.dian.gov.co/). | **No activar** sin autorización. |
| `Dian:BaseUrl` / `Dian:OfficialConsultUrl` / `Dian:TimeoutSeconds` | C | Op | Op | Host observado, enlace oficial y tiempo de espera. | — | — | — |
| `RateLimiting:AuthPerMinute` | C | Op (10) | Op (10) | Límite por IP de las llamadas de autenticación. | — | — | Súbelo **solo** para la prueba E2E automatizada. |
| `RateLimiting:DianPerMinute` / `UploadPer5Minutes` / `CalculatePerMinute` / `GlobalPerMinute` | C | Op | Op | Límites 5 / 20 / 10 / 300. | — | — | No los subas en prod. |
| `Database:MigrateOnStartup` | C | Op (`true`) | Op (`true`) | Aplica las migraciones al arrancar. | — | Con `false` debes migrar a mano. | — |
| `ASPNETCORE_ENVIRONMENT` | C | `Development` | `Production` | Activa User Secrets y Swagger, y define las validaciones de arranque. | `launchSettings.json` / Dockerfile. | En Development sin User Secrets: no arranca. | — |
| `ASPNETCORE_URLS` | C | `http://localhost:5080` | `http://0.0.0.0:8080` | Puerto de escucha. El frontend local usa el 5080. | Perfil `http` de `launchSettings.json` / Dockerfile. | — | — |
| `MIRENTA_TEST_POSTGRES` | S (local) | Para `dotnet test` | — | Servidor PostgreSQL de pruebas, sin `Database=`. El rol necesita `CREATEDB`: cada clase de pruebas crea y borra su propia base. | Sección 4. | Las pruebas de integración fallan al conectar. | Variable de entorno de tu sesión. |
| `MIRENTA_TEST_S3_ENDPOINT` | C | Para `dotnet test` | — | S3 local para pruebas (`http://localhost:4569`, s3rver con las credenciales ficticias públicas `S3RVER`). | `npx s3rver -d .s3 -p 4569 --configure-bucket mi-renta-test` | Las pruebas de documentos fallan. | Variable de entorno. |

**Requisito externo opcional:** `pdftotext` (poppler) para leer PDF con texto. En Windows, instálalo y añádelo al
`PATH`; la imagen Docker ya lo incluye. Sin él, los PDF quedan en «ingreso manual» y el resto del flujo funciona.

## 3. Comandos de PowerShell (desarrollo local)

El `UserSecretsId` del proyecto de API es **`mi-renta-api-7d3f2c1a`** (en `src/MiRenta.Api/MiRenta.Api.csproj`). Los
secretos se guardan **fuera del repositorio**:
- Windows: `%APPDATA%\Microsoft\UserSecrets\mi-renta-api-7d3f2c1a\secrets.json`
- Linux/macOS: `~/.microsoft/usersecrets/mi-renta-api-7d3f2c1a/secrets.json`

La carpeta y el archivo los crea `dotnet user-secrets set` la primera vez. No hace falta crearlos a mano ni copiar
ningún `secrets.json` al repositorio.

```powershell
cd C:\ruta\linatask-mi-renta-backend

# 1) Consultar el UserSecretsId del proyecto de API
Select-String -Path src\MiRenta.Api\MiRenta.Api.csproj -Pattern 'UserSecretsId'

# 2) Inicializar User Secrets (solo si el .csproj no tuviera UserSecretsId; este ya lo tiene)
dotnet user-secrets init --project src\MiRenta.Api

# 3) Generar dos claves criptográficas distintas (48 bytes aleatorios en base64)
function New-MiRentaKey { $b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) }
$jwtKey = New-MiRentaKey
$urlKey = New-MiRentaKey

# 4) Configurar los secretos mínimos (la contraseña la escribes tú; no queda en el historial de PowerShell)
$dbPass = Read-Host "Contraseña del usuario mi_renta_app" -AsSecureString
$plain  = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($dbPass))
dotnet user-secrets set "ConnectionStrings:MiRenta" "Host=localhost;Port=5432;Database=mi_renta;Username=mi_renta_app;Password=$plain" --project src\MiRenta.Api
dotnet user-secrets set "Jwt:SigningKey" $jwtKey --project src\MiRenta.Api
dotnet user-secrets set "App:UrlSigningKey" $urlKey --project src\MiRenta.Api
# Opcional: un correo con rol de contador para probar la revisión
dotnet user-secrets set "App:AccountantEmails:0" "contador.prueba@example.test" --project src\MiRenta.Api
Remove-Variable plain, dbPass, jwtKey, urlKey

# 5) Consultar lo guardado SIN revelar valores sensibles
dotnet user-secrets list --project src\MiRenta.Api |
  ForEach-Object { $_ -replace '(Password=)[^;]*', '$1***' -replace '(SigningKey = ).*', '$1***' }

# 6) Ejecutar el backend con el perfil correcto (Development, http://localhost:5080)
dotnet run --project src\MiRenta.Api --launch-profile http

# 7) En otra terminal: comprobar que arrancó y que la base y el almacenamiento responden
Invoke-RestMethod http://localhost:5080/health/live     # Healthy
Invoke-RestMethod http://localhost:5080/health/ready    # Healthy (PostgreSQL + almacenamiento)
Start-Process http://localhost:5080/swagger            # documentación interactiva (solo Development)
```

Para borrar un valor: `dotnet user-secrets remove "<Clave>" --project src\MiRenta.Api`. Para borrarlos todos:
`dotnet user-secrets clear --project src\MiRenta.Api`.

### Plantilla de `secrets.json` (valores ficticios)

Es solo una referencia de estructura. Los comandos de arriba generan el archivo; no hace falta escribirlo a mano.

```json
{
  "ConnectionStrings": { "MiRenta": "Host=localhost;Port=5432;Database=mi_renta;Username=mi_renta_app;Password=EJEMPLO-no-real" },
  "Jwt": { "SigningKey": "EJEMPLO-base64-48-bytes-no-real=" },
  "App": {
    "UrlSigningKey": "EJEMPLO-otra-clave-base64-no-real=",
    "AccountantEmails": [ "contador.prueba@example.test" ]
  }
}
```

R2 y SMTP **no** hacen falta en local. Si algún día quieres probar R2 desde tu equipo, añade las cuatro claves
`Storage:*`: tienen prioridad sobre `Storage:LocalDirectory`.

### Dónde va cada cosa

| Valor | Local | Producción (futuro) | Nunca |
|---|---|---|---|
| Conexión de base de datos, firmas JWT y de URL, R2, SMTP, API key de IA | User Secrets | Variables **selladas** del servicio Mi renta en Railway ([guía](https://docs.railway.com/guides/variables)) | Git, `appsettings*.json`, Angular, chat |
| Orígenes, `FrontendBaseUrl`, correos de contador o de administrador de reglas | `appsettings.Development.json` o User Secrets | Variables de Railway | — |
| URL pública de la API (`apiUrl`) | `environment.development.ts` del frontend | `environment.ts` del frontend | — |

## 4. Preparar PostgreSQL en local (Windows)

Con PostgreSQL instalado ([descarga](https://www.postgresql.org/download/windows/)), abre **SQL Shell (psql)** como el
superusuario `postgres` y ejecuta estas sentencias, reemplazando las contraseñas:

```sql
-- Usuario y base de la aplicación (dueño solo de su base)
CREATE ROLE mi_renta_app LOGIN PASSWORD 'cambia-esta-contraseña';
CREATE DATABASE mi_renta OWNER mi_renta_app;

-- Usuario para las pruebas automatizadas (crea y borra bases temporales mirenta_t_*)
CREATE ROLE mi_renta_test LOGIN CREATEDB PASSWORD 'otra-contraseña-solo-local';
```

Las tablas las crea la API al arrancar (migración `InitialCreate`, esquema `mi_renta`).

Para las pruebas:

```powershell
$env:MIRENTA_TEST_POSTGRES = "Host=localhost;Port=5432;Username=mi_renta_test;Password=<la-de-pruebas>"
npx s3rver -d .s3 -p 4569 --configure-bucket mi-renta-test     # en otra terminal
dotnet test
```

## 5. Limitaciones de las alternativas locales

| Alternativa | Limitación |
|---|---|
| `.local-mail` | No envía correos reales. Para verificar una cuenta, abre el `.eml` más reciente y copia el enlace `http://localhost:4200/verificar-correo?token=…`. |
| `.local-storage` | Guarda los documentos **sin cifrar** en tu disco. Usa solo documentos sintéticos o de prueba. Borra la carpeta cuando termines. |
| Almacenamiento en memoria | Se usa si quitas `Storage:LocalDirectory` sin configurar R2. Los archivos se pierden al reiniciar y los documentos ya registrados no se podrán descargar ni procesar. |
| Sin `pdftotext` | Los PDF pasan a ingreso manual. |
| Sin `App:AccountantEmails` | No se puede completar la revisión ni la exportación. |

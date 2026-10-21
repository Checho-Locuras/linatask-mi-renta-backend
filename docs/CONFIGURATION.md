# Configuración y secretos de Mi renta

Este documento sale del código real:
- `Program.cs`, `Support.cs` (StartupValidation),
- las clases `*Options` (`JwtOptions`, `AppOptions`, `StorageOptions`, `EmailOptions`, `DianOptions`, `AiOptions`),
- `appsettings*.json`.

Reglas de nombres en ASP.NET Core:
- En JSON y User Secrets las claves usan `:` (por ejemplo `Jwt:SigningKey`).
- En variables de entorno se usa `__` (`Jwt__SigningKey`). Los arreglos usan índice: `App__AllowedOrigins__0`.
- **User Secrets solo se cargan con `ASPNETCORE_ENVIRONMENT=Development`.**

Mi renta **no usa ninguna credencial de LinaTask**: ni su base de datos, ni su JWT, ni sus buckets.

## 1. Inventario

Leyenda: **S** = secreto · **C** = configuración no secreta · **G** = valor que generas tú (aleatorio).
Obligatoriedad: Local = `dotnet run` en Development · Pruebas = `dotnet test` · Prod = Railway con `ASPNETCORE_ENVIRONMENT=Production`.

### Validación al iniciar

Si falta algo de esta lista, la API no arranca.
- **Siempre:**
  - `ConnectionStrings:MiRenta`;
  - `Jwt:SigningKey` y `App:UrlSigningKey`, de al menos 32 bytes y distintas entre sí.
- **Solo en producción:**
  - bucket configurado (`Storage:*`);
  - `App:AllowedOrigins` con orígenes `https://` explícitos (sin `*`);
  - `App:FrontendBaseUrl` en https;
  - `Email:SmtpHost`;
  - sin `Email:PickupDirectory`.
- **IA:** `Ai:Provider` debe ser `Disabled` o `Anthropic`. Si es `Anthropic`, exige `Ai:ApiKey`.

### Base de datos

| Clave | Tipo | Local | Pruebas | Prod |
|---|---|---|---|---|
| `ConnectionStrings:MiRenta` | S | **Obligatoria** | No (las pruebas usan `MIRENTA_TEST_POSTGRES`) | **Obligatoria** |
| `Database:MigrateOnStartup` | C | Opcional (`true`) | — | Opcional (`true`) |

`ConnectionStrings:MiRenta`
- **Uso:** base PostgreSQL propia de Mi renta (esquema `mi_renta`).
- **Dónde:**
  - Local: tu PostgreSQL ([descarga](https://www.postgresql.org/download/)).
  - Prod: un servicio PostgreSQL nuevo en Railway ([guía](https://docs.railway.com/guides/postgresql)). No es `linatask_db`.
- **Formato:** Npgsql, `Host=…;Port=5432;Database=mi_renta;Username=…;Password=…`. En prod añade `SSL Mode=Require` ([parámetros](https://www.npgsql.org/doc/connection-string-parameters.html)).
- **Seguridad:** usuario propio, dueño solo de esa base. Contraseña de 24 o más caracteres.
- **Sin ella:** la API no arranca.

`Database:MigrateOnStartup`
- **Uso:** aplica las migraciones al iniciar.
- **Sin ella:** con `false` debes migrar a mano.

### Firmas propias

| Clave | Tipo | Local | Pruebas | Prod |
|---|---|---|---|---|
| `Jwt:SigningKey` | S, G | **Obligatoria** | No (la fija el fixture) | **Obligatoria** |
| `Jwt:Issuer` / `Jwt:Audience` / `Jwt:AccessTokenMinutes` / `Jwt:RefreshTokenDays` | C | Opcionales (`mi-renta` / `mi-renta-web` / 15 / 14) | — | Opcionales |
| `App:UrlSigningKey` | S, G | **Obligatoria** | No | **Obligatoria** |

`Jwt:SigningKey`
- **Uso:** firma HS256 de los tokens de acceso de Mi renta.
- **Cómo generarla:** ver la sección 3.
- **Formato:** 48 bytes aleatorios en base64. Mínimo 32 bytes. Nunca la del JWT de LinaTask.
- **Sin ella:** la API no arranca.

`App:UrlSigningKey`
- **Uso:** HMAC de los enlaces temporales de descarga (5 min) y de baja de recordatorios.
- **Formato:** igual que `Jwt:SigningKey`, pero **distinta** de ella.
- **Sin ella:** la API no arranca.
- **Rotación:** si la cambias, invalida los enlaces de descarga activos y los de baja ya enviados.

### Aplicación

| Clave | Tipo | Local | Prod |
|---|---|---|---|
| `App:FrontendBaseUrl` | C | Ya viene en `appsettings.Development.json` (`http://localhost:4200`) | **Obligatoria** (https) |
| `App:AllowedOrigins:0..n` | C | Ya viene (`http://localhost:4200`) | **Obligatoria** |
| `App:AccountantEmails:0..n` | C | Opcional | Opcional |
| `App:RulesAdminEmails:0..n` | C | Opcional | Opcional |
| `App:PrivacyNoticeVersion`, `App:DownloadUrlMinutes`, `App:RunWorkers` | C | Opcionales (`2026-10-v1` / 5 / `true`) | Opcionales |

- **`App:FrontendBaseUrl`:** base de los enlaces de verificación, restablecimiento y baja que van en los correos. En
  prod, la URL https del frontend.
- **`App:AllowedOrigins`:** lista blanca de CORS. Origen exacto del frontend, sin barra final ni `*`. Si falta, el
  navegador bloquea las llamadas.
- **`App:AccountantEmails`:** correos que obtienen el rol Contador al verificarse. Sin ellos, nadie puede revisar ni
  aprobar borradores, y los expedientes se quedan en «Requiere revisión de contador».
- **`App:RulesAdminEmails`:** correos que obtienen el rol de administrador de reglas. Sin ellos, nadie puede aprobar
  reglas y los cálculos quedan «preliminares» (no bloquea el flujo).

### Almacenamiento (Cloudflare R2)

| Clave | Tipo | Local | Prod |
|---|---|---|---|
| `Storage:ServiceUrl` | C | Opcional | **Obligatoria** |
| `Storage:BucketName` | C | Opcional | **Obligatoria** |
| `Storage:AccessKeyId` | S | Opcional | **Obligatoria** |
| `Storage:SecretAccessKey` | S | Opcional | **Obligatoria** |
| `Storage:MaxFileBytes` | C | Opcional (15 MB) | Opcional |

- **Sin `Storage:*` en local:** se usa almacenamiento **en memoria** y los documentos se pierden al reiniciar.
- **`Storage:ServiceUrl`:** endpoint S3 de R2, `https://<ACCOUNT_ID>.r2.cloudflarestorage.com`. El Account ID está en
  [dash.cloudflare.com](https://dash.cloudflare.com) → R2 ([docs S3](https://developers.cloudflare.com/r2/api/s3/api/)).
- **`Storage:BucketName`:** bucket **nuevo y exclusivo** de Mi renta, sin dominio público ni r2.dev. Se crea en
  R2 → Create bucket ([docs](https://developers.cloudflare.com/r2/buckets/create-buckets/)).
- **`Storage:AccessKeyId` y `Storage:SecretAccessKey`:**
  - Se obtienen en R2 → Manage R2 API Tokens → Create API token ([docs](https://developers.cloudflare.com/r2/api/tokens/)).
  - Permiso «Object Read & Write», **limitado solo a ese bucket**. No reutilices el token de LinaTask.
  - El secret solo se muestra una vez.

### Correo

| Clave | Tipo | Local | Prod |
|---|---|---|---|
| `Email:PickupDirectory` | C | **Ya viene** (`.local-mail`) | **Prohibida** |
| `Email:SmtpHost` | C | Opcional | **Obligatoria** |
| `Email:SmtpPort` / `Email:UseSsl` | C | Opcional (587 / `true`) | Opcional |
| `Email:Username` | S | Opcional | Según proveedor |
| `Email:Password` | S | Opcional | Según proveedor |
| `Email:From` | C | Opcional | **Recomendado** |
| `Email:MaxRemindersPerWeek` | C | Opcional (2) | Opcional |

- **`Email:PickupDirectory`:** solo desarrollo. Escribe los correos como `.eml` en una carpeta local; el enlace de
  verificación queda legible. Va ignorada por Git.
- **`Email:SmtpHost`:** servidor SMTP de tu proveedor (por ejemplo
  [Amazon SES](https://docs.aws.amazon.com/ses/latest/dg/smtp-credentials.html) o
  [SendGrid](https://www.twilio.com/docs/sendgrid/for-developers/sending-email/integrating-with-the-smtp-api)).
- **`Email:Username` y `Email:Password`:** credencial SMTP de envío (no tu contraseña personal). En Gmail sería una
  [contraseña de aplicación](https://support.google.com/accounts/answer/185833).
- **`Email:From`:** remitente, por ejemplo `Mi renta <no-reply@tu-dominio>`. Usa un dominio verificado con SPF/DKIM.
- **Sin SMTP ni carpeta local:** los correos quedan `Skipped`. Nadie puede verificar su cuenta ni recuperar la
  contraseña, y no salen recordatorios.

### IA (opcional, desactivada)

| Clave | Tipo | Valor |
|---|---|---|
| `Ai:Provider` | C | `Disabled` (por defecto) o `Anthropic` |
| `Ai:ApiKey` | S | Solo si `Provider=Anthropic` |
| `Ai:Model` / `Ai:BaseUrl` / `Ai:MaxExcerptChars` | C | Opcionales |

- La IA solo sugiere valores de certificados PDF con un extracto minimizado. Sin ella, todo funciona con extracción
  determinista.
- `Ai:ApiKey` se obtiene en [console.anthropic.com → API keys](https://console.anthropic.com/settings/keys). Usa una
  clave dedicada con límite de gasto.
- **No la actives** hasta aprobar el tratamiento de datos.

### Pre-consulta DIAN (desactivada)

| Clave | Tipo | Valor |
|---|---|---|
| `Dian:Enabled` | C | `false` |
| `Dian:BaseUrl` / `Dian:OfficialConsultUrl` / `Dian:TimeoutSeconds` | C | Opcionales |

- No requiere credenciales. El reCAPTCHA lo resuelve el usuario en la DIAN: **no hay clave de CAPTCHA que
  configurar**, y Mi renta no tiene ni debe tener una.
- Con `false` solo se muestra el enlace a la [consulta oficial](https://www.dian.gov.co/).
- Los endpoints no están documentados oficialmente. **No la actives** sin autorización.

### Límites de solicitudes

| Clave | Por defecto |
|---|---|
| `RateLimiting:GlobalPerMinute` | 300 |
| `RateLimiting:AuthPerMinute` | 10 |
| `RateLimiting:DianPerMinute` | 5 |
| `RateLimiting:UploadPer5Minutes` | 20 |
| `RateLimiting:CalculatePerMinute` | 10 |

No subas estos límites en producción.

### Variables de prueba

| Variable | Tipo | Uso |
|---|---|---|
| `MIRENTA_TEST_POSTGRES` | S (local) | Servidor PostgreSQL de pruebas, sin `Database=`. El rol necesita `CREATEDB`: cada clase de pruebas crea y borra su propia base. Si falta: `Host=localhost;Port=5432;Username=postgres`. |
| `MIRENTA_TEST_S3_ENDPOINT` | C | S3 local para pruebas. Si falta: `http://localhost:4569` (s3rver, credenciales ficticias públicas `S3RVER`). |

### Servidor

| Clave | Uso |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` en local (activa User Secrets y Swagger). `Production` en Railway (lo fija el Dockerfile). |
| `ASPNETCORE_URLS` | Local: `http://localhost:5080` (perfil `http` de `launchSettings.json`, que espera el frontend). Docker: `http://0.0.0.0:8080`. |

### Herramienta externa

`pdftotext` (poppler) extrae texto de PDF.
- **Local en Windows:** instálalo y ponlo en el `PATH`.
- **Docker:** ya viene incluido.
- **Sin él:** los PDF quedan para ingreso manual.

## 2. User Secrets en desarrollo local

- **`UserSecretsId`:** `mi-renta-api-7d3f2c1a` (en `src/MiRenta.Api/MiRenta.Api.csproj`).
- **Ruta del archivo:**
  - Windows: `%APPDATA%\Microsoft\UserSecrets\mi-renta-api-7d3f2c1a\secrets.json`
  - Linux/macOS: `~/.microsoft/usersecrets/mi-renta-api-7d3f2c1a/secrets.json`

El archivo vive fuera del repositorio y no se publica.

```powershell
cd <ruta>\linatask-mi-renta-backend
dotnet user-secrets list --project src/MiRenta.Api
dotnet user-secrets set "ConnectionStrings:MiRenta" "Host=localhost;Port=5432;Database=mi_renta;Username=mi_renta_app;Password=<tu-contraseña-local>" --project src/MiRenta.Api
dotnet user-secrets set "Jwt:SigningKey" "<clave-generada-1>" --project src/MiRenta.Api
dotnet user-secrets set "App:UrlSigningKey" "<clave-generada-2>" --project src/MiRenta.Api
```

### Plantilla de `secrets.json`

Todos los valores son **ficticios**.

```json
{
  "ConnectionStrings": {
    "MiRenta": "Host=localhost;Port=5432;Database=mi_renta;Username=mi_renta_app;Password=EJEMPLO-no-real"
  },
  "Jwt": {
    "SigningKey": "EJEMPLO-base64-de-48-bytes-generado-localmente-no-real="
  },
  "App": {
    "UrlSigningKey": "EJEMPLO-otra-clave-base64-de-48-bytes-no-real=",
    "AccountantEmails": [ "contador.prueba@example.test" ],
    "RulesAdminEmails": []
  },
  "Storage": {
    "ServiceUrl": "https://EJEMPLO-ACCOUNT-ID.r2.cloudflarestorage.com",
    "BucketName": "mi-renta-private-dev",
    "AccessKeyId": "EJEMPLO-ACCESS-KEY-ID",
    "SecretAccessKey": "EJEMPLO-SECRET-ACCESS-KEY"
  },
  "Email": {
    "SmtpHost": "",
    "Username": "",
    "Password": ""
  }
}
```

- El mínimo para arrancar son las tres primeras claves: `ConnectionStrings:MiRenta`, `Jwt:SigningKey` y
  `App:UrlSigningKey`.
- `Storage` y `Email` son opcionales en local: sin ellos se usa memoria y la carpeta `.local-mail`.
- `Ai` y `Dian` **no** van en User Secrets mientras estén desactivados.

### Dónde va cada valor

| Valor | Local | Railway (prod) | Nunca |
|---|---|---|---|
| Conexión BD, firmas JWT/URL, R2, SMTP, API key de IA | User Secrets | Variables del servicio Mi renta, preferiblemente como variables selladas (Railway → Variables, [guía](https://docs.railway.com/guides/variables)) | Git, `appsettings*.json`, frontend Angular, chat |
| Orígenes, `FrontendBaseUrl`, correos de contador o administrador | `appsettings.Development.json` o User Secrets | Variables de Railway | — |
| `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS` | `launchSettings.json` | Dockerfile o Railway | — |
| `miRentaApiUrl` (URL pública de la API, no es secreto) | `environment.ts` del frontend | `environment.*.ts` del frontend | — |

## 3. Generar claves aleatorias

```powershell
# PowerShell (Windows): 48 bytes criptográficamente aleatorios en base64
$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

```bash
openssl rand -base64 48
```

Genera **dos** valores distintos: uno para `Jwt:SigningKey` y otro para `App:UrlSigningKey`. Usa valores diferentes
en local y en producción.

## 4. Verificación de correo en local

Con `Email:PickupDirectory=.local-mail` (ya configurado en Development):
1. Regístrate.
2. Abre el `.eml` más reciente de `src/MiRenta.Api/.local-mail/`.
3. Copia el enlace `…/mi-renta/verificar-correo?token=…`.

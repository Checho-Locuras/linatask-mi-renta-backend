# Mi renta — arquitectura, seguridad y alcance

## 1. Independencia respecto a LinaTask

| Aspecto | Mi renta | Monolito LinaTask |
|---|---|---|
| Repositorio | `linatask-mi-renta` | `linatask-backend` (sin cambios) |
| Base de datos | PostgreSQL propia, esquema `mi_renta`, historial `mi_renta.__ef_migrations_history` | la suya |
| Identidad | cuentas, JWT (HS256, clave propia), refresh tokens rotativos | la suya |
| Archivos | bucket privado exclusivo, clave opaca `mi-renta/cases/{caso}/documents/{doc}` | sus buckets |
| Trabajadores | cola persistente en su BD (`FOR UPDATE SKIP LOCKED`), outbox de correo, recordatorios | los suyos |
| Integración | el frontend llama directo a `/api/v1/my-rent` con CORS restrictivo | sin proxy |

- No hay federación de identidad.
- Si el usuario tiene sesión en LinaTask, el frontend solo precarga su correo para vincular la cuenta. La contraseña
  de Mi renta es propia.
- El interceptor del frontend envía el token de Mi renta únicamente al microservicio. Se registra después del de
  LinaTask, que elimina cualquier `Authorization` hacia orígenes ajenos. El token de LinaTask nunca sale hacia Mi renta.

## 2. Modelo de datos

**Claves primarias**
- Todas las claves primarias son `Guid` aleatorios.
- La cédula es un atributo protegido, con índice único `(document_type, document_number)`.
- La cédula nunca se usa como clave de autorización ni aparece en rutas, claves de almacenamiento, auditoría o correos.

| Grupo | Entidades |
|---|---|
| Identidad | `UserAccount`, `EmailToken` (solo hash, un uso, expira), `RefreshToken` (solo hash, rotación y detección de reutilización), `ConsentRecord` |
| Expediente | `Taxpayer`, `TaxCase` (concurrencia optimista con `xmin`), `ClientAuthorization`, `AuditEvent`, `ValidationIssue` |
| Documentos | `TaxDocument` (tipo real, tamaño, SHA-256 único por expediente), `DocumentExtraction` (versionada), `EvidenceItem` (localizador página/línea o fila) |
| Finanzas | `FinancialAccount`, `FinancialTransaction` (clave de deduplicación), `Reconciliation` |
| Hechos y reglas | `TaxFact` (versionado: un cambio crea una versión y reemplaza la anterior), `TaxRule` (año, versión de reglas, norma, estado) |
| Resultado | `TaxCalculation` (versión de motor y de reglas, foto de hechos, resultado), `ReturnDraft` (versión, revisión con identidad y fecha), `ReturnLine` (trazabilidad por concepto), `DraftReviewNote` |
| Asistente | `Conversation`, `ConversationMessage` (idempotente), `AssistantTask`, `UserResponse` (idempotente) |
| Operación | `BackgroundJob`, `OutboundEmail` |

La migración inicial es `20261021200633_InitialCreate`: 27 tablas, más la semilla del catálogo de reglas AG2025.

## 3. Máquina de estados del expediente

Estados:
- `NotStarted`
- `CollectingInformation`
- `AwaitingDocument`
- `ProcessingDocument`
- `AwaitingUserAnswer`
- `NeedsProfessionalReview`
- `ReadyForCalculation`
- `Calculating`
- `ReadyForReview`
- `ApprovedForExport`
- `Completed`
- `Blocked`

Las transiciones permitidas están en `CaseStateMachine`. El estado lo deriva `CaseWorkflow` de forma determinista
(nunca la IA), a partir de:
- tareas abiertas,
- documentos en proceso,
- conciliaciones abiertas,
- valores pendientes de contador,
- borradores vigentes.

Si cambia un hecho después del cálculo, el borrador queda `Superseded` y el expediente vuelve a `ReadyForCalculation`.

## 4. Asistente

**Preguntas**
- Las preguntas son progresivas (`QuestionCatalog`). Las laborales solo aparecen si el usuario declara salario; la de
  honorarios, solo si declara honorarios.
- Si una respuesta cambia, las preguntas que ya no aplican se cancelan.
- Las respuestas se convierten en hechos versionados.
- Los mensajes libres solo reciben orientación determinista y no cambian datos.

**Datos sensibles en mensajes**
- Los mensajes que parecen contener una contraseña se guardan con el dato oculto.
- Los números largos (cédulas, cuentas) también se ocultan.

**Idempotencia**
- Mensajes: clave del cliente, con índice único `(conversation_id, idempotency_key)`.
- Respuestas: índice único `(task_id, idempotency_key)`.

## 5. Documentos

**Al subir un documento**
1. Se valida el tamaño (15 MB por defecto).
2. Se detecta el tipo real por contenido. Se rechazan:
   - ejecutables;
   - HTML o SVG;
   - PDF con JavaScript, `/Launch`, `/OpenAction` o archivos embebidos;
   - XLSX con macros o con descompresión excesiva;
   - archivos cuya extensión no coincide con su contenido.
3. Se calcula el SHA-256. Si el mismo archivo ya está en el expediente, se devuelve el existente.
4. Se guarda en el bucket privado **antes** de encolar el procesamiento.

**Procesamiento asíncrono**
- Herramientas de extracción:
  - `pdftotext`, con límite de 30 páginas y 20 s;
  - OpenXml para XLSX;
  - parser CSV que neutraliza fórmulas.
- Antes de extraer se verifica de nuevo el hash.
- El texto del documento es siempre dato, nunca instrucciones: solo se buscan rótulos conocidos del certificado 220.
- Los valores encontrados quedan como evidencia `Proposed`, con su localizador, y se crea una tarea de confirmación.
  Ningún valor se usa sin confirmación.
- Imágenes y PDF escaneados: quedan en `NeedsManualEntry` (no hay OCR).
- Reintentos: 3 con espera exponencial; después el documento queda `Failed`. El error se registra sin contenido.

**Proveedor de IA (opcional, desactivado)**
- Recibe solo un extracto minimizado, sin correos ni números largos.
- Su salida se valida contra el esquema y contra la lista de campos permitidos.
- Cada monto que proponga debe aparecer literalmente en el texto.
- Solo propone evidencia; nunca cambia estados.

**Descarga**
- Enlace firmado con HMAC, ligado a documento, usuario y expiración (5 min). Lo sirve la API.
- Al servirlo se vuelve a verificar que el usuario tenga acceso.
- Cabeceras: `attachment`, `nosniff`, `CSP sandbox`.
- El bucket nunca es público.

## 6. Conciliación

Se detecta automáticamente:

| Caso | Criterio |
|---|---|
| Duplicado | Mismo movimiento en dos extractos |
| Transferencia propia | Salida y entrada por el mismo valor en cuentas distintas (≤ 3 días), o descripción explícita |
| Reverso | Pares ± con palabras de reverso |
| Depósito sin explicar | ≥ $1.000.000 no reconocido como nómina |

Comportamiento:
- Un depósito que ya forma parte de un par no se pregunta aparte.
- Resolver un duplicado excluye la copia y cierra las preguntas que dependían de ella.
- Las conciliaciones abiertas impiden calcular.

## 7. Motor del formulario 210

`Form210Engine` (`engine-210-0.1.0`) es una función pura con aritmética `decimal`. Alcance: residente fiscal, cédula
general, rentas de trabajo laborales.

**Catálogo `AG2025-r1`**

| Código | Regla | Fuente citada | Estado |
|---|---|---|---|
| `uvt` | UVT 2025 = $49.799 | Res. DIAN 000193/2024 (compilada en art. 1.2.2 Res. 000227/2025) | Pendiente |
| `obligation.thresholds` | Patrimonio > 4.500 UVT; ingresos, tarjeta, compras y consignaciones ≥/> 1.400 UVT; responsable de IVA | ET arts. 592, 593, 594-3; DUR 1625/2016 art. 1.6.1.13.2.7 | Pendiente |
| `income.incr_mandatory_contributions` | Aportes obligatorios a pensión y salud = INCR | ET arts. 55, 56 | Pendiente |
| `exempt.labor_25` | 25 %, tope 790 UVT | ET art. 206 num. 10 | Pendiente |
| `deduction.dependents_387` | 10 % del ingreso bruto, tope 384 UVT al año | ET art. 387 | Pendiente |
| `deduction.housing_interest_119` | Tope 1.200 UVT | ET art. 119 | Pendiente |
| `deduction.prepaid_health_387` | Tope 192 UVT al año | ET art. 387 | Pendiente |
| `limit.exempt_and_deductions_336` | 40 % y 1.340 UVT | ET art. 336 num. 3 | Pendiente |
| `tax.rates_241` | Tabla marginal 0/19/28/33/35/37/39 % | ET art. 241 | Pendiente |
| `rounding.thousand_577` | Múltiplo de mil más cercano | ET art. 577 | Pendiente |
| `deduction.dependents_additional_336` | 72 UVT por dependiente | ET art. 336 num. 3 (Ley 2277) | **No implementada**: la ingresa un contador |
| `advance.807` | Anticipo | ET arts. 807–809 | **No implementada**: lo ingresa un contador |
| `income.fees` | Honorarios | — | **No implementada**: bloquea |
| `scope.non_resident` | No residentes | — | **No implementada**: bloquea |

**Resultado del cálculo**

| Estado | Cuándo |
|---|---|
| `Blocked` | Faltan hechos aceptados, hay ingresos fuera de alcance, datos inconsistentes o una regla no implementada. Si lo único que falta son valores de contador, el resultado es una **vista previa** con esos valores en 0, que nunca genera borrador. |
| `Preliminary` | Se usó alguna regla pendiente de aprobación. El contador debe marcar explícitamente que lo revisó para aprobar. |
| `ReadyForApproval` | Todas las reglas usadas están aprobadas. |

**Aprobación de reglas**
- Solo la hace un usuario con rol `TaxRulesAdmin`.
- Exige una nota de la fuente verificada.
- Queda registrada con identidad y fecha.

**Trazabilidad del borrador**
- Cada línea guarda: concepto, valor, regla, fórmula, ids de los hechos de origen y advertencias.
- `BoxNumber` es `null` en todas las líneas: el mapeo a casillas del formulario oficial no está verificado. La
  exportación lo muestra como «por verificar».

## 8. Revisión, exportación y presentación

1. El contribuyente autoriza a un contador. La respuesta es genérica, así que no revela quién es contador. El acceso
   se puede revocar.
2. El contador ingresa los valores que le corresponden.
3. El contador aprueba o devuelve el borrador. La aprobación exige versión exacta y registra identidad y fecha.
4. Exportación (JSON o CSV):
   - solo es posible con el borrador aprobado;
   - queda auditada;
   - incluye advertencias e identidad del aprobador;
   - el CSV neutraliza fórmulas.
5. El contribuyente marca que presentó por su cuenta en el portal de la DIAN, y el expediente pasa a `Completed`. No
   hay presentación automática.

## 9. Pre-consulta DIAN

Adaptador `DianPrecheckClient`. Endpoints observados, no documentados oficialmente:
- `GET /adminidentity/v1/consultapreguntasbynit/{nit}/tipodocumento/{tipo}`
- `POST /adminidentity/v1/verificarrespuestas`
- `POST /exogena/api/v1/terceros/declaracion`

Comportamiento:
- Desactivado por defecto (`Dian__Enabled=false`). En ese estado responde `disabled` con el enlace a la consulta
  oficial.
- Si se activa:
  - el reCAPTCHA lo resuelve el usuario;
  - nada se persiste;
  - un mismo reCAPTCHA no se acepta dos veces (solo su hash vive 10 minutos en memoria);
  - los campos `ident*` se devuelven sin interpretar;
  - límite de 5 solicitudes por minuto y por IP.

## 10. Seguridad transversal

**Autorización (IDOR)**
- Un único punto de control, `CaseAccess`: dueño, o contador con autorización vigente.
- Cualquier otro caso recibe 404.

**Cuentas**
- Registro, login y recuperación con mensajes genéricos.
- Ante un duplicado, se avisa por correo al titular existente y nunca se asocia la cuenta.
- PBKDF2-SHA256 con 210.000 iteraciones y hash ficticio para igualar tiempos.
- Bloqueo de 15 minutos tras 5 fallos.
- Refresh tokens rotativos: reutilizar uno revoca todas las sesiones.
- Tokens de correo: un uso, expiración (verificación 24 h, restablecimiento 30 min), máximo 3 por hora.

**Límites de solicitudes**

| Ámbito | Límite |
|---|---|
| Autenticación | 10/min por IP |
| DIAN | 5/min por IP |
| Subidas | 20 cada 5 min por usuario |
| Cálculo | 10/min por usuario |
| Global | 300/min |

**Correos**
- Outbox persistente, sin cifras, documentos ni enlaces a archivos.
- El cuerpo se borra al enviarse.
- Recordatorios: deduplicados por semana y tipo, máximo 2 por semana, con baja firmada. No afirman plazos de la DIAN.

**API y configuración**
- Cabeceras de seguridad, `Cache-Control: no-store` y ProblemDetails sin detalles internos en producción.
- En producción, el inicio falla si faltan claves de al menos 32 bytes, el bucket o orígenes https explícitos.
- Logs JSON sin datos personales. Métricas con `System.Diagnostics.Metrics` (meter `MiRenta.Api`).
- Health checks: `/health/live` y `/health/ready` (PostgreSQL y almacenamiento).

## 11. Endpoints (`/api/v1/my-rent`)

**Autenticación** (pública)
- `POST auth/register`
- `POST auth/verify-email`
- `POST auth/resend-verification`
- `POST auth/login`
- `POST auth/refresh`
- `POST auth/logout`
- `POST auth/forgot-password`
- `POST auth/reset-password`

**Cuenta**
- `GET me`
- `PUT me/reminders`
- `POST reminders/unsubscribe`

**Expedientes**
- `GET|POST cases`
- `GET cases/{id}`
- `GET cases/{id}/progress`
- `POST|DELETE cases/{id}/accountants`
- `GET cases/{id}/issues`

**Documentos**
- `GET|POST cases/{id}/documents`
- `GET|DELETE cases/{id}/documents/{docId}`
- `POST cases/{id}/documents/{docId}/download-url`
- `GET files/{token}`

**Asistente**
- `GET|POST cases/{id}/assistant/messages`
- `GET cases/{id}/tasks`
- `POST cases/{id}/tasks/{taskId}/answers`

**Hechos y movimientos**
- `GET cases/{id}/facts`
- `PUT cases/{id}/facts/{code}`
- `GET cases/{id}/transactions`
- `GET cases/{id}/deposits-summary`
- `GET cases/{id}/reconciliations`
- `POST cases/{id}/reconciliations/{rid}/resolve`

**Cálculo, borrador y presentación**
- `POST cases/{id}/calculate`
- `GET cases/{id}/calculations/latest`
- `GET cases/{id}/draft`
- `POST cases/{id}/draft/review`
- `GET cases/{id}/export?format=json|csv`
- `POST cases/{id}/mark-filed`

**Reglas**
- `GET rules`
- `POST rules/{code}/approve`

**DIAN** (pública)
- `GET dian/status`
- `POST dian/questions`
- `POST dian/verify-answers`
- `POST dian/declarant`

**Salud** (fuera del prefijo)
- `GET /health/live`
- `GET /health/ready`

## 12. Limitaciones conocidas del MVP

- Solo cubre la cédula general con rentas laborales. Quedan bloqueados para revisión profesional:
  - honorarios,
  - pensiones,
  - rentas de capital y no laborales,
  - dividendos,
  - ganancias ocasionales,
  - no residentes.
- Las reglas citan fuentes oficiales, pero varios valores se tomaron de compilaciones secundarias. Todas siguen
  **pendientes de aprobación profesional**.
- La tabla del art. 241 vigente debe confirmarse con un profesional.
- El mapeo a casillas del formulario 210 no está verificado.
- El anticipo (art. 807) y la deducción adicional por dependientes no se calculan.
- No hay OCR: las imágenes y los PDF escaneados requieren ingreso manual.
- La pre-consulta DIAN depende de endpoints no documentados y está desactivada.
- No hay envío real de correos sin SMTP configurado: los correos quedan `Skipped`.
- No hay despliegue todavía. Requiere su propio servicio, BD y bucket.

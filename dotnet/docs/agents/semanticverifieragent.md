# Semantic Verifier Agent: evaluación íntegra y feedback con control humano

`TimelineFactSemanticVerifierAgent` realiza **una evaluación separada del lote completo de candidatos** frente a la misma entrada congelada y respuestas humanas que recibió Extraction. **No tiene tools, no corrige claims y no guarda facts.** `VerifyOutcomeAsync` devuelve un resultado derivado por el servidor: **Approved, NeedsClarification o Rejected**. No aprueba un prefijo ni un subconjunto favorable; la evidencia inválida sigue siendo un fallo técnico, no una pregunta para el usuario.

## 1. Alcance y contrato

**Raíz del repositorio inspeccionado:** `C:/Data/formacion/aishippinglabs-project-xabiarrieta`. Cada ruta `dotnet/...:línea` parte de esa raíz para mantener legible esta copia fuera del repositorio. El informe refleja el árbol de trabajo actual con feedback y ciclo humano, incluidos cambios sin commit; no acredita un despliegue. La migración de feedback está **PREPARADA, NO APLICADA**: una DB persistente requiere aplicarla con autorización humana. No se reinició ni desplegó el host.

| Aspecto | Contrato actual |
|---|---|
| Agente de negocio / nombre | `TimelineFactSemanticVerifierAgent` / `timelineFactSemanticVerifier` |
| Entrada | `TimelineFactExtraction source`, lote `IReadOnlyList<TimelineFactCandidate>` e índices generados por servidor |
| Datos del modelo | Fuente congelada completa, `sourceSpans`, pares `{ candidateIndex, candidate }` |
| Salida del modelo | `TimelineFactSemanticVerificationResult(Assessments, Questions)`; una assessment por índice y preguntas validadas |
| Assessment | `CandidateIndex`, `Supported`, `SupportScore`, `Issues`, `EvidenceIds` |
| Salida del servidor | `TimelineFactVerificationOutcome(Disposition, Result)`; Disposition no es un campo que el modelo pueda decidir |
| Efecto del provider/worker | Approved permite Generated; noaprobación conserva snapshots/findings/scores en feedback duradero, nunca como facts ni recibo de verdad |
| No hace | Extraer claims nuevos, reparar/reducir candidatos, verificar contradicciones históricas, aceptar propuestas, escribir en DB o ejecutar embeddings |

Firmas públicas, no tools:

```csharp
Task<TimelineFactVerificationOutcome> VerifyOutcomeAsync(TimelineFactExtraction source, IReadOnlyList<TimelineFactCandidate> candidates, CancellationToken ct)
Task<TimelineFactSemanticVerificationResult> VerifyAsync(TimelineFactExtraction source, IReadOnlyList<TimelineFactCandidate> candidates, CancellationToken ct)
```

No confundirlo con `Framework/JudgeAgent`, auxiliar de evaluación, ni con `InvalidationAssessments` de Optimization: esas assessments las emite el planificador y **no pasan por este agente**. Los wrappers `LocalCustomAgent`/Harness son infraestructura, no agentes funcionales adicionales [V1, V2, V14].

## 2. Proceso real desde el llamador hasta la persistencia

```text
Petición de generación o job Extraction autorizado
  → worker congela entrada + respuestas humanas vinculadas
  → TimelineFactOpenAIProvider
      → ExtractionAgent obtiene candidatos/preguntas
      → preguntas: descarta provisionales y pausa ANTES del verificador
      → sin preguntas y cero candidatos: outcome vacío, sin verificador
      → sin preguntas y lote no vacío:
          VerifyOutcomeAsync(fuente congelada, lote completo)
            → validar entrada/forma → generar spans
            → una ejecución tipada sin tools
            → validar sobre, todos los índices/IDs y preguntas
            → derivar Approved | NeedsClarification | Rejected
      → solo Approved: reconstruir citas literales y SourceKind
  → worker revalida outcome, fuente/input/lease y persiste atómicamente
      → NeedsClarification: feedback + preguntas + AwaitingClarification
      → Rejected sin preguntas: feedback + Failed expuesto NeedsCorrection
      → Approved / vacío válido: propuesta Generated + job Completed
  → respuestas sin modelo; resume/regeneración explícitos crean sucesor
  → reconciliación + aceptación explícita posterior, fuera del verificador
```

### 2.1. Hosting y ámbito autorizado

La Web API registra el par Extraction/SemanticVerifier en `AddTimelineFacts`, sobre el mismo cliente de clave explícita. Ambos usan `TimelineFacts:ExtractionModel`, default **`gpt-4o-mini`**; el nombre admite hasta **100 caracteres**, no vacío. Aunque el cliente del provider tiene `isolateHarnessFacilities: false`, esta ruta crea un **`LocalCustomAgent` normal**, no un Harness: no se activan por ello tools locales de Harness [V3, V4].

El verificador no tiene endpoint propio ni decide el proyecto. La aplicación autoriza escritura al pedir generación; el worker vuelve a comprobar la fuente, usuario autor activo con escritura, modelo y estado del job. Usa una identidad de input duradera y el mismo snapshot para extracción y verificación. El inicializador explícito también construye el mismo par de agentes; no es un cuarto tipo de verificación distinto [V5, V6, V15].

El poller permanece apagado por defecto (`ProcessingEnabled = false`); cuando se habilita toma **10 jobs/ciclo**, intervalo permitido **1–100**, y espera **15 s**, intervalo permitido **1–300 s** entre ciclos. La generación explícita de la aplicación espera el worker sin depender de ese gate. Estos límites de scheduling **no son límites de candidatos, llamadas del verificador o duración por invocación** [V3, V5].

### 2.2. Admisión previa y llamada al modelo

1. `VerifyOutcomeAsync` congela la fuente, valida sus límites y la forma de cada candidato. Rechaza **200 o más candidatos**. No consulta DB ni busca facts.
2. Comprueba cancelación. Si el lote está vacío, devuelve `Approved` con `Assessments = []` **sin construir ni llamar al agente remoto**. El provider ya evita normalmente invocar el verificador en ese caso.
3. Genera `sourceSpans` del servidor sobre título, contenido y respuestas reales; numera candidatos por su posición en el lote, no por un ID inventado por el modelo.
4. Inicia el bloque acotado `TimelineFactProviderCall`; dentro serializa `{ source, sourceSpans, candidates }` con límite de bytes y construye un agente normal con **tools `[]`**.
5. Ejecuta una única evaluación estructurada del lote; no hay bucle propio por candidato, voting, umbral numérico, reparación de citas escritas ni reintento correctivo [V1, V2, V4].

### 2.3. Validación del resultado completo

La admisión se realiza en varias capas:

| Capa | Control exigido |
|---|---|
| Sobre OpenAI | `RawRepresentation` real, fin `Stop`, sin refusal/content filter, exactamente un mensaje, tamaño permitido |
| JSON/esquema | Profundidad 16; raíz `assessments` y `questions`; assessments con longitud exacta del lote. Outbound `fact_semantic_assessments` estricto exige todas las propiedades, sin extras; inbound legacy permite omitir questions como `[]` |
| Tipado | Campos requeridos, propiedades extra y duplicadas no admitidas; nulabilidad respetada |
| Cobertura | Índices dentro de rango, únicos y cubriendo todos los candidatos; no falta cola ni vale una evaluación duplicada |
| Valores | Score finito entre 0 y 1; issues no nulos, sin duplicados, como máximo 5 y enum válido; evidenceIds no nulos |
| Evidencia | Reconstruir cada selección suministrada, **incluidas rechazadas**: sin IDs extraños, históricos, duplicados, vacíos o mezcla no permitida. Supported requiere evidencia; unsupported puede devolver `[]` |
| Preguntas | Key/text/reason/evidenceIds requeridos; claves únicas, texto con `?`, motivo cerrado y 1–81 IDs actuales reconstruibles; respuestas heredadas + nuevas preguntas ≤20 |
| Disposición | Preguntas válidas → **NeedsClarification**; sin preguntas, todos Supported y sin issues → **Approved**; de lo contrario → **Rejected** |

La lista no necesita llegar ordenada: el provider resuelve por `CandidateIndex`. No se admite cobertura incompleta, ni un score alto anula un blocker; un score cero puede aprobar si los otros requisitos se cumplen. Malformaciones producen `MalformedAssessment` u otro diagnóstico de sobre/schema; selecciones no reconstruibles, incluso en rechazados, producen `InvalidEvidence`; preguntas malformadas, `InvalidQuestions`. **Neutralidad y genuina necesidad se instruyen y restringen estructuralmente, no se prueban semánticamente** [V1, V4, V7, V18].

La API estricta `VerifyAsync` adapta el outcome: toda noaprobación lanza `TimelineFactProviderException("InvalidResponse", "SemanticUnsupported")`. No devuelve un rechazo como aparente lista vacía aprobada.

### 2.4. Qué hace después el provider

Tras éxito, el provider vuelve a crear los mismos spans y conserva por candidato todas las identidades seleccionadas, ordenadas de forma estable. Elige un span primario determinista y reconstruye `SourceQuote` desde el texto original, asignando `SourceKind` a `Entry` o `Clarification`. **No usa texto de evidencia redactado por el verificador ni modifica claim/entity key/type** [V2].

El worker valida evidencia literal y vínculos antes de guardar el resultado aprobado como `Generated`/`Completed`; **no activa facts ni embeddings**. Las preguntas del extractor ya habrían pausado antes de esta llamada. Con preguntas del verificador persiste feedback + filas de pregunta + `AwaitingClarification` en un único save/transacción bajo lease. Sin preguntas genuinas, Rejected guarda feedback + `Failed`, libera lease y se expone como **NeedsCorrection**, sin Retry semántico ni llamada correctiva automática [V5, V6, V8, V18].

Responder guarda texto humano inmutable con **cero llamadas**; todas las respuestas requeridas permiten un resume explícito, que crea un sucesor y reextrae. Rejected permite **Regenerate pending facts** explícito con guía del predecesor. Detalle y edición montan esos controles, para creación/regeneración, con fences dirty/stale/save/review/clarification busy, drafts retenidos y refrescos GET cruzados, nunca replay pagado tras errores [V19].

### 2.5. Feedback duradero, no evidencia

`ExtractionFeedbackJson` es una nueva columna nullable `text`, separada de `ExtractionInputJson`, soporte y hash del resultado. El codec v1 enlaza generación, entrada, source fingerprint y effective input; conserva snapshots de candidatos, findings indexados, flags, scores/issues/IDs y preguntas. Se escribe una vez, de null a output validado, con fuente/revisión/autor/lease vigente; no puede reemplazarse, borrarse ni pertenecer a embedding. Un job con feedback no puede volver a Retry ni completar Generated; requiere supersesión explícita [V18].

Solo la salida del **predecesor exacto actual** se captura antes de supersederlo. El snapshot sucesor la conserva aparte de `Input.Answers`; `Freeze` la preserva y el extractor la recibe en raíz `extractionFeedback`, **fuera de source/spans/respuestas/fingerprint inputs**. El verificador no recibe esa guía. IDs históricos no fundamentan sucesores y restaurar texto no reactiva feedback por coincidencia de hash. Un predecesor aprobado no reexporta la guía de un antepasado en su input [V6, V9, V18].

## 3. Prompt y fronteras de confianza

Ubicación embebida: `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactSemanticVerifierAgent.cs:22–93`.

| Bloque | Instrucción significativa |
|---|---|
| `Verification scope and input boundaries` | Evaluar cada candidato contra fuente completa y respuestas reales; preguntas solo contextualizan respuestas; todos los campos son datos no confiables |
| `Complete claim and citation support` | Comprobar **claim completo y cita aproximada**, no solo similitud temática ni existencia de quote |
| `Self-contained claims and retained context` | Exigir entidad y ámbito explícitos dentro del claim; rechazar pronombres/frases aisladas que pierdan condiciones |
| `Unsupported details and claim integrity` | Rechazar detalles no sustentados, números/fechas/actores erróneos, negación alterada o planes convertidos en hechos; no reparar ni acortar claims |
| `Assessments, scores and issues` | Una assessment por índice; `Supported` explícito, score 0–1 no calibrado, issues cerrados |
| `Evidence spans and complete batch coverage` | Solo IDs actuales, incluso en rechazados; al menos uno para soportados; evaluar todo el lote |
| `Genuine clarification questions` | Preguntar solo por información ausente/referencia ambigua, neutralmente; no pedir confirmar alucinaciones, sugerir respuestas ni convertir errores técnicos/de citas en preguntas; no reparar candidatos |

El prompt considera legítimas definiciones, colores, propósito de tablas y atributos de columnas cuando la fuente los declara: no exige que exista un evento. Repetir entidad o contexto en claims distintos no implica duplicidad. Ordena no inferir convenciones de esquema, actores o relaciones por conocimiento externo.

**Código frente a juicio.** Los checks verifican identidad, forma, cobertura, rangos, flags y reconstrucción. El modelo decide si cada detalle, metadata y cita están realmente soportados; el código no puede demostrar esa relación semántica solo por encontrar un substring. Tampoco comprueba por algoritmo que el extractor no omitió hechos de la fuente. «Todos soportados» no significa «todas las afirmaciones de la fuente fueron extraídas» [V1, V4].

## 4. Catálogo de tools y codec de evidencia

**Tools registradas: ninguna.** No hay búsqueda léxica/vectorial, tools de recuperación de facts/entradas ni acceso de escritura. La allowlist relevante no es la de fact IDs de Search: es la selección de `sourceSpans` **suministrados en este mismo input congelado** [V1].

### Spans, identidades y reconstrucción

- Ventanas mecánicas de hasta **256 unidades UTF-16** por campo, evitando partir pares sustitutos. No son oraciones ni garantías de separación semántica.
- IDs derivados de fuente y, con input duradero, entrada/job/effective fingerprint; los spans de respuestas enlazan el ID de respuesta y tienen `SourceKind = Clarification`.
- Para un candidato soportado o pregunta se exigen **1–81 IDs únicos, actuales y no vacíos**. Unsupported permite `[]`; toda selección suministrada se valida con el mismo codec. El máximo de array inferido de esquema no sustituye ese control efectivo de 81.
- Sin respuestas, los spans elegidos deben pertenecer al mismo campo `Title` o `Content`. Con respuestas presentes pueden combinarse fuentes; el conjunto completo de soporte se conserva por separado.
- `Primary` prioriza Clarification y después fuente/respuesta/posición en orden determinista. La quote canónica es el intervalo desde el mínimo inicio hasta el máximo final **dentro de ese campo/respuesta primario**, incluyendo texto literal intermedio. No concatena artificialmente todas las fuentes seleccionadas.
- Las preguntas no son spans factuales. Solo las respuestas humanas reales pueden justificar nuevos datos; sus IDs, autoría y linaje se revalidan fuera del modelo antes de persistir [V7, V9].

La reconstrucción demuestra que el texto procede de la fuente congelada y mantiene su kind; **no demuestra que el span seleccionado sustente cada palabra del claim**. Esa evaluación sigue siendo del modelo, incluso cuando hay un score alto.

## 5. Límites efectivos y unidad de ejecución

| Recurso | Límite activo | Alcance |
|---|---:|---|
| Fuente | Título ≤200; contenido ≤20.000 caracteres | Mismo snapshot de extracción; no se trunca |
| Candidatos | **0–199** | 0 omite remoto; `>=200` se rechaza, aunque el esquema genérico anuncie maxItems 200 |
| Claim / entityKey / entityType / quote | 4.000 / 300 / 100 / 20.000 caracteres | No vacíos, candidatos iniciales kind `Entry`; duplicados exactos rechazados |
| Payload | ≤768 KiB UTF-8 | Fuente + spans + todos los candidatos; no equivale a límite de tokens de entrada |
| Assessments | Exactamente una por candidato | Índices únicos y completos |
| `SupportScore` | Finito, **0–1** | **Sin umbral numérico de aceptación**; cero no se rechaza por sí mismo si los demás checks pasan |
| `Issues` | **0–5**, únicos | Valores exactos `Numeric`, `Date`, `Actor`, `Negation`, `Unsupported`; para aceptar exige cero |
| Evidencia | **1–81 IDs** si Supported o pregunta; **0–81** si unsupported | Todo ID suministrado debe ser actual, único y reconstruible |
| Preguntas | ≤20 menos respuestas heredadas | Key ≤100, text ≤2.000 con `?`, reason MissingInformation/AmbiguousReference; respuesta humana posterior ≤4.000 |
| Feedback v1 persistido | ≤512 × 1024 unidades UTF-16 de JSON; profundidad 16 | 1–199 findings, índices completos y ≤81 IDs/item; no son bytes ni tokens |
| Salida solicitada al modelo | **12.000 tokens por llamada** | No 1.500; no un cupo monetario ni agregado del job |
| Respuesta tipada | ≤512 KiB UTF-8, profundidad **16** | Un mensaje, fin `Stop`, sin refusal/corte |
| Plazo de esta evaluación | **90 segundos** | Bloque `TimelineFactProviderCall`; cancelación cooperativa |
| Transporte explícito del host | **0 reintentos SDK**, red **90 s** | Cuerpo HTTP ≤512 KiB; JSON exitoso profundidad 32; una elección |

Fuentes: [V1, V4, V7, V10, V11, V18]. Longitudes C# y feedback serializado usan unidades UTF-16; payload/respuesta remotos usan bytes UTF-8. La extracción sucesora incluyendo guía sigue bajo **768 KiB UTF-8**: un feedback persistible puede superar el prompt combinado y fallar `InputLimit`; no se trunca.

### Per-invocation no significa per-job

El diseño realiza una evaluación de **todo el lote no vacío**, no una llamada por candidato. No hay llamadas repetidas para «mejorar score» ni otra llamada de reparación tras `SemanticUnsupported`. El provider encadena extracción y verificación, cada una con su propio bloque de **90 s**. El worker no instala un deadline total equivalente a esos bloques [V2].

El job toma lease de **3 minutos**; extracción + verificación + overhead pueden consumirlo y terminar en `LostLease` al revalidar propiedad, versión y expiración. Los **fallos técnicos** se registran como `Retry` hasta que uno no cancelado con `Attempts >= 8` pasa a `Failed`; la cancelación no aplica ese corte. Backoff: `min(3600, 5 × 2^min(Attempts,10))` segundos. Un retry técnico vuelve a ejecutar la cadena completa. **La noaprobación semántica válida no entra en esa política**: NeedsClarification espera; Rejected termina Failed/NeedsCorrection. Ninguna programa un intento ciego sobre input inalterado [V6, V18].

Esos ocho intentos son política del **job**, no reintentos HTTP ni techo vitalicio garantizado de llamadas: las cancelaciones y regeneraciones explícitas cambian esa contabilidad. Una nueva generación o reanudación con respuestas vuelve a evaluar el lote frente al nuevo input vinculado.

### Guardias opcionales y límites no establecidos

- El hosting/provider/worker de esta ruta **no crea `AgentExecutionBudget.Start`**. El decorador de hoja solo limita si existe scope ambiental; sin él reenvía transparentemente. No heredar Search **20 admisiones / 20 tools / 60 s** [V3, V6, V12].
- No hay cupo propio de tokens totales por job, precio, admisiones de proveedor o memoria total. La contabilidad de uso no es un presupuesto de gasto.
- La implementación normal usa una ejecución tipada tool-free y no tiene bucle de reintentos de dominio. No se configuran en fuente máximos numéricos de iteraciones/reintentos internos del SDK **1.24.0**; no se afirman defaults de Harness, que ni siquiera es el wrapper de esta ruta [V4, V13].
- Un cliente arbitrario inyectado puede tener su propia política de transporte. Las garantías de reintentos/cuerpo HTTP anteriores corresponden al cliente explícito realmente construido por el host, no a todas las inyecciones posibles [V10].

## 6. Fallos, incertidumbre y uso recomendado

| Situación | Resultado |
|---|---|
| Lote vacío del API verificador | Approved con `Assessments = []`, sin remoto; el provider evita la llamada en extracción vacía o con preguntas |
| Entrada excesiva o lote saturado | `InputLimit` / `CandidateLimit`; no se recorta para simular éxito |
| Falta un índice, se repite o está fuera de rango | `MalformedAssessment`; no aceptar subconjunto |
| Score NaN/infinito/fuera de 0–1 o issues inválidos | Rechazo; score no es texto ni probabilidad calibrada |
| Preguntas válidas en salida completa | NeedsClarification, sin candidatos; feedback/preguntas duraderos y espera humana |
| Algún unsupported/issue y ninguna pregunta | Rejected, feedback + Failed/NeedsCorrection; la API legacy estricta lanza SemanticUnsupported |
| Span inexistente, histórico, duplicado, mezcla inválida o falta requerida | InvalidEvidence técnico, incluso en assessment rechazada; no inventar preguntas ni reparar IDs |
| Fin `Length`, refusal o content filter | `ResponseTruncated` / `ResponseRefused`; no admitir prefijo |
| Timeout / 429 / error remoto | `Timeout` / `RateLimited` / `Unavailable`; worker decide retry/fallo |
| Cancelación del cliente/host | Se propaga; worker intenta registrar cancelación y liberar lease |
| Fuente, respuesta o lease cambiados después | Se rechaza la publicación aunque el modelo hubiera respondido «soportado» |

Los errores de proveedor usan códigos y diagnósticos cerrados, sin adjuntar mensajes o payloads remotos. La aplicación muestra estado del job/generación y un **resumen validado de la generación actual**: candidateCount, unsupportedCount, issues cerrados y questionCount; no claims crudos de feedback como evidencia ni scores por fact. Puede verse `Retry`, `Failed`, `NeedsCorrection`, `AwaitingClarification`, `Pending`, `Empty` o `Reconciled`; no son categorías elegidas por el modelo. Scores/findings no aprobados sí quedan en el JSON de auditoría, sin autoridad factual [V5, V6, V11, V18].

**Flujo recomendado:** pausar primero las preguntas del extractor; verificar después el lote no vacío, sin preguntas, contra el mismo snapshot. Exigir índices completos y citas válidas para cada ID suministrado. Ante NeedsClarification, guardar respuestas reales y reanudar; ante NeedsCorrection, decidir regeneración explícita con feedback o edición de fuente. Nunca publicar el subset bueno, inventar evidencia, confirmar alucinaciones ni bajar un umbral inexistente. Revisar Generated y reconciliar/aceptar por separado.

Compatibilidad fail-closed: el provider `ExtractAsync` comprueba feedback **antes de preguntas** y lanza `SemanticUnsupported` también para NeedsClarification del verificador. Preguntas originadas solo en Extraction lanzan `TimelineFactClarificationRequiredException` tanto en su agente como en el provider. El adaptador legacy de `ITimelineFactProvider` rechaza answers/feedback no consumibles con `ClarificationInputUnsupported`; wrappers del inicializador reenvían el envelope moderno sin perder feedback ni controles pre/post [V2, V6, V20].

## 7. Salvedades respaldadas por el código

1. **Independencia limitada:** es otra llamada con otro prompt sobre el mismo snapshot, pero por defecto el mismo modelo/proveedor que Extraction. No hay diversidad de modelos, calibración estadística, prueba formal ni comparación contra fuentes externas.
2. **No es revisión histórica:** evalúa soporte del claim en su fuente. No decide si ese claim invalida otro de la timeline; eso pertenece al juicio contextual de Optimization [V14].
3. **Literalidad no equivale a verdad:** IDs válidos, `Supported = true` y score en rango son necesarios según contrato, no demostración semántica. El código no comprueba exhaustividad de extracción ni que el modelo haya resistido toda instrucción maliciosa.
4. **Sin anonimización general:** fuente, candidatos, quotes y respuestas humanas autorizadas llegan al modelo. La captura de contenido de telemetría tiene default `true`; si hay exportación activa puede incluir esos mensajes. El host posee una frontera de privacidad configurable, no una garantía de que ningún contenido se exporte [V16].
5. **Activación separada:** aunque `Effective` admite origen histórico de extracción directa, el worker actual completa un borrador `Generated`; la verificación por sí sola no activa facts [V8, V17].

## 8. Verificación y límites de este informe

T2 independiente combinado (`mvw71169-g-e85p`): **1.386 backend**, **38 frontend enfocados / 95 completos**, cero fallos/omitidos; enfocado backend previo **225** válido y repetición del padre **38** frontend verde. Vite aislado pasó **37 módulos**, `envDir:false`, salida temporal del SO. React real de producción en Chrome pasó **33 aserciones** detalle/edición con **66 peticiones falsas interceptadas (56 GET/10 escrituras)** y cero destinos inesperados/errores de página. Es evidencia de interacción, **no precisión semántica, neutralidad ni calibración**; no hubo backend/modelo/PostgreSQL reales.

La primera suite completa obtuvo **1.385 aprobados/1 fallo**: `TimelineFactModelTests.Npgsql_model_has_real_vector_mapping_and_migration_snapshot_matches_without_connecting` comparaba un target histórico con el snapshot nuevo. Se corrigió a último target registrado de `IMigrationsAssembly`, sin quitar asserts ni alterar targets históricos. Pasaron **36** tests independientes de migración/modelo y finalmente los **1.386** completos. ASSESS nativo no disponible por ámbito untracked no declarado; fallback independiente de alto riesgo completado. Solo warnings Node `MODULE_TYPELESS_PACKAGE_JSON` y Git LF/CRLF. El RED histórico y GREEN enfocado 225 del escritor no son un RED reejecutado independientemente.

T3 es documentación pasiva sin RED/GREEN significativo: inspección estática, estructura y referencias, no nuevas suites. Evidencia recibida en `odd/tasks/extraction-verification-feedback.md`. Migración `20261026000100_ExtractionGenerationFeedback`: **un campo nullable text**, target congelado/snapshot/históricos y SQL verificados offline, **no aplicada** [V21]. Sin reinicio del host, despliegue ni mediciones de producción ni garantía de calidad de una DB completa.

## 9. Referencias de fuente

| Ref. | Ruta desde la raíz | Evidencia |
|---|---|---|
| V1 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactSemanticVerifierAgent.cs:8–151` | Prompt, lote vacío, tools vacías y validación íntegra |
| V2 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactOpenAIProvider.cs:9–82` | Llamada separada, reconstrucción y bloques de 90 s |
| V3 | `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactHosting.cs:19–90` | Modelo compartido, cliente explícito y scheduling |
| V4 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactStructuredResponse.cs:13–109`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/LocalCustomAgent.cs:24–104` | Esquema estricto, límites, wrapper no Harness y token |
| V5 | `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactApplication.cs:43–94`; `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactApplication.cs:137–207`; `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactEndpoints.cs:43–92` | Generación autorizada, estados y errores públicos |
| V6 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactProcessing.cs:142–370` | Lease, permisos, llamadas, persistencia y retry del job |
| V7 | `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFactExtractionInputs.cs:19–100`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactSourceEvidence.cs:5–29` | Spans, selección, Primary y reconstrucción |
| V8 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactService.cs:63–119`; `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactService.cs:172–216` | Activación posterior frente a borrador Generated |
| V9 | `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineEntryClarifications.cs:29–63`; `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFactExtractionInputs.cs:174–295` | Snapshot, respuestas reales, fingerprints y linaje |
| V10 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/CustomOpenAIClient.cs:20–115` | Transporte explícito sin reintentos y cotas HTTP |
| V11 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/AgentModels/TimelineFactSemanticVerificationResult.cs:5–36`; `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactProcessing.cs:80–94`; `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactProcessing.cs:393–404` | Contrato/enum y diagnósticos/forma de candidato |
| V12 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/AgentExecutionBudget.cs:95–118`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/AgentExecutionBudget.cs:157–179` | Guard opcional, no presupuesto Search activado aquí |
| V13 | `dotnet/src/ProjectTimeline.BusinessLogic/ProjectTimeline.BusinessLogic.csproj:6–8` | Versiones declaradas, no defaults internos inventados |
| V14 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactOptimizationAgent.cs:103–127`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/AgentModels/TimelineFactReviewResult.cs:12–16` | Diferencia entre assessment del planner y verificador separado |
| V15 | `dotnet/src/ProjectTimeline.FactsInitializer/Program.cs:53–72` | Inicializador también consume el mismo par de agentes |
| V16 | `dotnet/src/ProjectTimeline.UI.WebApi/LogfireTelemetry.cs:63–65`; `dotnet/src/ProjectTimeline.UI.WebApi/LogfireAgentPrivacyProcessor.cs:36–48`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/AgentTelemetry.cs:55–84` | Política de contenido y observación, no presupuesto monetario |
| V17 | `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFacts.cs:29–40` | Compatibilidad con facts efectivos históricos de extracción |
| V18 | `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFactGenerationFeedback.cs:7–163`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactClarificationQuestions.cs:6–23` | Bounds, atomicidad, salida inmutable y preguntas estructurales |
| V19 | `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/App.jsx:411–525`; `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/entryClarifications.js:55–154`; `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/FactReview.jsx:6–140` | Detalle/edición, guardas, resumen y GET seguro |
| V20 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactProcessing.cs:9–78`; `dotnet/src/ProjectTimeline.FactsInitializer/FactInitializer.cs:163–181`; `dotnet/src/ProjectTimeline.FactsInitializer/TimelineFactRetryRunner.cs:161–179` | Freeze, adaptador legacy y forwarding completo |
| V21 | `dotnet/src/ProjectTimeline.Database/Migrations/20261026000100_ExtractionGenerationFeedback.cs:8–16`; `dotnet/src/ProjectTimeline.Database/Migrations/20261026000100_ExtractionGenerationFeedback.Designer.cs:6–21`; `dotnet/src/ProjectTimeline.Database/Migrations/ProjectTimelineDbContextModelSnapshot.cs:651–656`; `dotnet/tests/ProjectTimeline.Tests/TimelineFactGenerationFeedbackTests.cs:336–350` | Metadata/SQL offline, no despliegue |

**Conclusión:** la verificación es un gate de soporte de lote completo con evidencia del servidor, no un reparador ni un publicador. Reduce la aceptación accidental de salidas incompletas o mal vinculadas, pero no convierte el juicio del modelo en prueba independiente de verdad o exhaustividad.

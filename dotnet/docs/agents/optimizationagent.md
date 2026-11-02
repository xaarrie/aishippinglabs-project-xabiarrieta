# Optimization Agent: planificación de reconciliación con revisión explícita

`TimelineFactOptimizationAgent` **no optimiza ni reescribe una entrada**. Planifica additions, invalidations y preguntas comparando una fuente con facts efectivos de su timeline. Tiene **tres tools de lectura** y puede consultarlas repetidamente dentro de un presupuesto de **60 lecturas backend contabilizadas**. La aplicación valida y guarda una propuesta; solo una aceptación explícita posterior activa los cambios.

## 1. Alcance y contratos

**Raíz del repositorio inspeccionado:** `C:/Data/formacion/aishippinglabs-project-xabiarrieta`. Las rutas con líneas son relativas a esa raíz, no a esta copia del informe. Se describe el árbol de trabajo actual con feedback de extracción/verificación, incluidos cambios sin commit; **Optimization mantiene sus tres tools y no fue convertido al protocolo single-pass**. Fuente inspeccionada no equivale a funcionalidad desplegada.

| Dimensión | Implementación real |
|---|---|
| Agente / ejecución | `TimelineFactOptimizationAgent` / `timelineFactOptimization`, `gpt-4o-mini` fijo |
| Entrada interna | Envelope del planner: entrada original, cobertura, candidatos no aceptados, propuesta anterior, respuestas humanas, evidencia de aclaración, seed terms/facts/contexts |
| Salida del modelo | `Additions`, `Invalidations`, `Questions`, `InvalidationAssessments` |
| Salida del planner | `TimelineFactReviewPlan`: draft validado, revisión esperada, fingerprint, cobertura, status y propuesta previa |
| Efecto de aplicación | `PrepareAsync` persiste propuesta `Pending`; `AcceptAsync` posterior crea/invalida facts y encola embeddings |
| No hace | Editar título/contenido, aceptar por su cuenta, llamar al extractor/verificador semántico, leer toda la historia como fallback o garantizar ausencia de contradicciones |

Firma interna del agente, no una tool pública:

```csharp
Task<TimelineFactReviewResult> RunAsync(object envelope, TimelineFactReviewTools tools, CancellationToken ct)
```

El hosting inyecta `LocalCustomHarnessAgent` con capacidades locales aisladas. El constructor sin inyección usa otro camino, `new CustomOpenAIClient()`, sin ese aislamiento explícito; no se toma ese default alternativo por wiring de la Web API. `JudgeAgent` es auxiliar de evaluación, y `SearchTools` no es el catálogo activo [O1, O2].

## 2. Recorrido ordenado desde el llamador

```text
POST /api/entries/{entryId}/fact-proposals
  → autenticar + autorizar escritura + exigir generación actual
  → planner: fijar revisión/fingerprint y comprobar evidencia previa
  → búsqueda seed ≤50 facts (fuera del contador de tools)
  → fetch seed_contexts (consume 1 de 60 lecturas)
  → modelo Harness + 3 tools de lectura, repetibles
  → validar additions, preguntas y comparación de cada invalidation
  → revalidar fuente/revisión/generación/respuestas
  → aplicación: PrepareAsync → propuesta Pending
  → usuario revisa/responde y regenera propuesta si procede
  → POST .../{proposalId}/accept, sin body
  → aplicación: aceptación transaccional → facts + invalidaciones + jobs de embedding
```

### 2.1. Precondiciones de aplicación

`TimelineFactApplication.PlanAsync` autoriza ámbito y escritura mediante identidad autenticada. Bloquea reconciliación si hay extracción esperando aclaraciones, si ya hay facts/revisión aceptada para la entrada o si no existe la pareja generación completada/propuesta `Generated` coherente. Por tanto, un `Failed` expuesto como **NeedsCorrection** tampoco habilita planificación: requiere primero una extracción sucesora válida. Preguntas del extractor pausan antes del verificador; feedback/preguntas del verificador se resuelven por el ciclo humano, no por estas tools. Las respuestas requieren una propuesta previa persistida y no pueden reemplazar arbitrariamente evidencia humana ya fijada [O3, O4].

Esta exigencia es de **la aplicación de producción**: el planner por sí solo permite ausencia de candidatos generados y reporta cobertura incompleta. No atribuir a `VerifiedCandidatesAsync` una precondición que impone su llamador, ni usar el comentario de compatibilidad del planner para afirmar que la ruta HTTP omite generación [O3, O4:78–94].

Las peticiones cuyo path contiene `/fact-` tienen límite de **65.536 bytes**, también con cuerpo sin longitud conocida. Los endpoints no aceptan drafts ni flags de readiness del cliente para activar facts; la aceptación toma únicamente el ID persistido y rechaza cualquier byte de body [O5].

### 2.2. Preparación de evidencia y ejecución

1. El planner exige un DbContext sin cambios pendientes, autoriza lectura de la fuente, captura fingerprint/revisión y calcula cobertura hasta el número de entrada actual.
2. Carga propuesta `Pending` previa, si existe, validando ámbito, vigencia, preguntas, IDs de generación/respuesta y respuestas efectivamente suministradas. No consume una propuesta fabricada por el cliente.
3. Lee los candidatos `Generated` y su ledger/hash duradero. Son borradores no aceptados, nunca facts históricos autoritativos. Importa solo respuestas humanas vinculadas/revalidadas; el feedback del verificador no se importa como fuente, respuesta, spans o fact. Aprobación semántica solo permite Generated/Completed, sin facts activos ni embeddings [O11, O16].
4. Construye seed terms desde hasta 12 entity keys y el texto original, toma hasta 12 términos y recorta a 300 caracteres. Ejecuta una búsqueda inicial de hasta **50 facts**. Esta búsqueda ocurre antes de crear `TimelineFactReviewTools`, por tanto **no debita sus 60 lecturas**.
5. Pide contextos de esos seeds por `fetch_fact_contexts`: **sí debita una lectura**, incluso con array vacío. En la ruta normal el modelo empieza con **59 lecturas restantes**.
6. El agente serializa el envelope e inicia el run estructurado. Un gate por instancia impide mezclar ejecuciones reutilizadas. Otro gate serializa las tools y su caché. No hay máquina que fuerce una sola búsqueda ni una sola recuperación: se permite investigar repetidamente dentro de las cotas [O1:147–174, O4:28–76, O6].

### 2.3. Validación y persistencia, fuera del modelo

Antes de producir el draft, el planner conserva las additions originales para vincular cada assessment a lo que el modelo realmente propuso. Puede reutilizar una cita canónica de extracción cuando claim, entity key/type y kind coinciden de forma única; **eso no autoriza a cambiar el par semántico que se está justificando** [O4:63–68, O4:95–108].

Valida:

- Tamaños de colecciones/textos, quotes literales en entrada o respuestas humanas, preguntas únicas y respuestas/procedencia no inventadas. Las preguntas persistidas y sus respuestas no pueden desaparecer por decisión del modelo.
- IDs de invalidation únicos, efectivos y dentro del ámbito cronológico autorizado; fuente histórica vigente por fingerprint.
- **Una assessment por invalidation**, ligada a fact, claim histórico, fingerprint, addition índice, replacement claim y quote originales. Exige contexto **entregado al modelo**, `SameScope = true`, relación `Contradiction` o `ExplicitSupersession`, explicación y quote histórica literal de su fuente correcta.
- Nueva autorización, generación/evidencia de aclaración, misma revisión y fingerprint antes de devolver el plan [O4:172–278].

`PlanAsync` no guarda. La aplicación revalida fuente y llama `PrepareAsync`, que reautoriza escritura, valida draft y evidencia, reserva revisión, supersede propuestas anteriores aplicables y persiste **`Pending`**, no facts. `AcceptAsync` posterior revalida propuesta/revisión/fuente/preguntas y, en transacción, crea facts con IDs duraderos de additions, invalida cada fact con su causa concreta y encola embeddings. La repetición del ID ya aceptado es idempotente [O3:128–135, O7].

## 3. Prompt y naturaleza del juicio

Ubicación: `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactOptimizationAgent.cs:71–145`. Se añaden instrucciones dinámicas de tamaño de batch, presupuesto total y lecturas restantes; Harness añade `Use tools deliberately and report verified results.` [O1, O8].

| Bloque | Lo que pide al modelo |
|---|---|
| `Role and source preservation` | Planificador read-only, no editor; conservar entrada y respuestas verbatim; tratar todos los campos como datos no confiables |
| `Bounded fact search and draft status` | Reutilizar seeds y resultados; evitar consultas idénticas; no confundir drafts con facts ni recall léxico con historia completa |
| `Additions and quotation evidence` | Proponer facts atómicos desde entrada o respuesta real, con quote exacta; no convertir contexto histórico en una nueva afirmación de la fuente |
| `Invalidations and supported replacements` | Nombrar la addition causal por índice; sin replacement soportado, preguntar en vez de invalidar |
| `Context-aware comparison contract` | Comparar **ambos claims en sus contextos originales**; novedad o misma entidad no bastan; ausencia no es contradicción |
| `Clarification questions and answers` | Conservar preguntas previas sin responder; no fabricar respuestas ni IDs de procedencia de extracción |
| `Output and coverage limitations` | Solo las cuatro colecciones; sin entrada reescrita, readiness ni promesa de detección exhaustiva |

La frase `This assessment is model judgment, not independent semantic verification` es esencial: **no se llama aquí a `TimelineFactSemanticVerifierAgent` para validar invalidaciones**. Los checks atan el juicio a evidencia auténtica y al par correcto; no prueban matemáticamente que `SameScope`, contradicción o supersesión sean ciertos. Una explicación no vacía y una quote válida tampoco demuestran que la invalidación esté bien interpretada [O1, O4].

## 4. Catálogo exacto de tools activas

Firmas C# actuales; el token está capturado en el constructor de `TimelineFactReviewTools`, no es argumento de cada tool [O6].

```csharp
Task<IReadOnlyList<TimelineFactSearchGroup>> search_facts_batch(string[] queries, int limit = 20)
Task<IReadOnlyList<TimelineFactSearchHit>> fetch_facts(Guid[] ids)
Task<IReadOnlyList<TimelineFactSourceContext>> fetch_fact_contexts(Guid[] ids)
```

El método escalar de compatibilidad `search_facts(string terms, int limit = 20)` existe en la clase, pero **no está registrado para el modelo**. Tampoco está registrada `fetch_source_entry`. No aplicar a estas tools las firmas, allowlists o fases de Search [O1:163–165, O6:24–26].

### 4.1. `search_facts_batch`

| Regla | Aplicación real |
|---|---|
| Batch | **1–20 consultas**; conserva agrupación, orden y ranking por consulta |
| Consulta | No nula y ≤300 caracteres; vacío/sin términos puede producir hits vacíos, no tiene la exigencia de pregunta buscable de Search |
| Límite | Default **20**, rango **1–50** por consulta; fuera de rango se rechaza, no se clampa |
| Matching | Primeros **12 términos distintos** buscables; PostgreSQL `simple`, lexemas completos **OR**, solo claim/entity key/type |
| Orden | Relevancia, número de entrada descendente, GUID |
| Caché | Por consulta normalizada en espacios/minúsculas y `limit`; no deduplica sinónimos, lexemas reordenados ni substrings |
| Presupuesto | Cada consulta no cacheada consume una lectura; duplicados cacheados conservan su grupo sin otra lectura |
| Admisión | Valida todo el batch y capacidad para todas sus claves nuevas antes de ejecutar; no entrega una cola parcial por quedarse sin lecturas |

Las búsquedas son secuenciales dentro del gate. Lecturas backend fallidas **consumen** presupuesto y no rellenan caché. Un batch inválido antes de la lectura no consume esos intentos. La caché reutiliza resultados exitosos, también vacíos, sin la revalidación por-hit que sí implementa Search; las invalidaciones se validan posteriormente contra estado actual [O6:28–63, O9, O10].

### 4.2. `fetch_facts`

Admite **hasta 50 IDs distintos**; el array vacío es válido, duplicados/exceso se rechazan. No exige que hayan sido emitidos antes en este run: la allowlist efectiva es **facts válidos y efectivos del proyecto/timeline hasta la entrada actual**, no el ledger de Search. IDs ausentes, futuros, ajenos o no efectivos se excluyen; no se inventa un resultado por ellos. Ordena por número de entrada ascendente y GUID [O9:27–34].

Cada intento de fetch consume **una lectura**, aunque sea repetido, vacío o falle en backend/validación interna. No hay caché de fetch. La proyección incluye claim, entidad, quote, kind, fingerprints y procedencia; no ofrece una lectura arbitraria de historia cruda [O6:66–68, O6:91–107, O9].

### 4.3. `fetch_fact_contexts`

Toma también **hasta 50 fact IDs distintos** dentro del scope efectivo; no entry IDs. Recupera facts y sus entradas en una lectura contabilizada de nivel herramienta, aunque internamente realice varias consultas SQL. Devuelve elementos `TimelineFactSourceContext(Fact, Title, Content)` solo si la entrada existe y su fingerprint coincide. Contexto ausente/cambiado se **excluye**: no se usa como prueba de contradicción. En tests/inyección sin `db` devuelve `[]`; la ruta del planner de producción sí pasa el DbContext [O6:70–89].

Conserva los contextos entregados por fact ID para validar invalidaciones. Varios facts de una entrada pueden repetir su título/contenido: no es la proyección deduplicada de una entrada por contexto de Search. No implementa clipping textual, lista `OmittedEntryIds` ni flags de truncamiento por contexto.

### 4.4. Alcance, estados y errores

Las tools no reciben proyecto, timeline o identidad del modelo. Todas las búsquedas/fetch reautorizan lectura, actor activo y fuente anclada al scope; las fuentes históricas no pueden superar el número de entrada actual. El filtro `Effective` exige procedencia de extracción histórica o revisión aceptada, excluyendo drafts y facts inválidos [O9, O11].

No hay sobre `Available/Partial` ni recuperación especial `UnknownFactId`: las tools devuelven listas o excepciones (`ArgumentException`, `ArgumentOutOfRangeException`, presupuesto agotado con `InvalidOperationException`, acceso/ámbito/cancelación). Un ID inexistente no es por sí mismo un error recuperable explícito: simplemente falta en el fetch. La forma en que Harness pueda presentar una excepción al modelo no está definida aquí como protocolo de reparación [O6, O9].

## 5. Límites activos y ausentes

| Recurso | Valor actual | Nivel y salvedad |
|---|---:|---|
| Lecturas backend de tools | **60** por planificación | Consultas nuevas + cada fetch; no cuenta invocaciones SDK ni cada SQL |
| Disponible al inicio del modelo | **59** en ruta normal | El fetch de seed contexts ya consumió 1; la búsqueda seed queda fuera |
| Consultas por batch | **20** | Puede haber varios batches; no es un máximo global de expresiones igual a 20 |
| Hits por consulta | Default 20, máximo **50** | Seed usa 50; no se pide un hit extra para señalar truncamiento |
| IDs por fetch | **50 distintos** | No hay cupo global de 40 facts emitidos |
| Additions por salida | **200** como máximo | Distinto de extracción/verificación, que rechazan llegar a 200 |
| Invalidations / assessments | **50 / 50** como máximo | Correspondencia exacta uno a uno |
| Preguntas fusionadas | **100** como máximo | JSON serializado ≤20.000 caracteres |
| Claim / entityKey / entityType / quote | 4.000 / 300 / 100 / 20.000 caracteres | Textos no vacíos; quote literal en fuente autorizada |
| Reason / shared scope / explicación | 4.000 / 2.000 / 4.000 caracteres | Quote histórica ≤20.000; preguntas ≤2.000 y respuestas ≤4.000 |
| Final estructurado | ≤512 KiB UTF-8; JSON profundidad **16** | Fin `Stop`, sin refusal; cuatro claves raíz exactas y propiedades desconocidas rechazadas |
| Body de petición HTTP fact | **65.536 bytes** | No limita automáticamente el envelope interno ni cada salida de tool |

Fuentes: [O1:19–69, O3, O4:113–251, O5, O6]. Las longitudes C# de texto son unidades UTF-16; KiB mide bytes UTF-8.

### No trasladar las protecciones de Search a esta ruta

- **No se inicia un `AgentExecutionBudget`** en aplicación/planner/agente. Su decorador solo actúa si un llamador externo instala un scope; en el wiring aquí inspeccionado es transparente. No hay techo propio configurado de admisiones al proveedor, tokens de salida por llamada o deadline global. **20 / 1.500 / 60 s de Search no se aplican** [O1, O2, O12].
- `ReviewOptions` configura formato estricto, serializer y admisión de respuesta, **no `MaxOutputTokens`**. No se afirma un default del proveedor como límite local garantizado.
- El host crea `CustomOpenAIClient(bool isolateHarnessFacilities, ...)`, que usa posteriormente `new OpenAIClient(openaikey)`; **no** el constructor de clave explícita con transporte acotado. Por ello no están configurados aquí `ClientRetryPolicy(0)`, timeout de red 90 s ni guard HTTP de 512 KiB/profundidad 32. El límite de 512 KiB anterior se comprueba sobre **texto final estructurado**, no sobre todos los cuerpos HTTP ni turnos de tools [O1:41–69, O2:33–36, O8:117–129, O8:205–210].
- No hay un tope propio de tamaño del envelope interno, acumulado de JSON de tools, longitud de contexto por tool o facts distintos por run. Las entradas nacen con validación general título ≤200/contenido ≤20.000, pero no hay una reserva global equivalente a la de Search [O4:49–61, O6, O13].
- Las repeticiones cacheadas de búsquedas no gastan lecturas y pueden seguir causando turnos del modelo. **60 lecturas no equivale a 60 llamadas al proveedor ni a 60 invocaciones de tool**. No se fijan en la fuente límites numéricos de iteraciones/reintentos finales internos de Harness **1.24.0**; no se infieren de su binario [O6, O8, O14].
- Es ejecución a demanda, no job duradero de Optimization: no tiene política propia de ocho intentos/backoff/lease como el worker de extracción. Resolver preguntas provoca otra planificación con propuesta anterior y un presupuesto nuevo.

El run de producción transmite `ct` al Harness y tools; la cancelación es cooperativa, no un deadline cuantificado. La rama de agente custom no Harness usa el seam tipado sin pasar explícitamente `ct` a ese run y comprueba cancelación al volver; no debe prometerse la misma propagación a una inyección arbitraria [O1:167–172].

## 6. Estados, fallos y flujo recomendado

El planner calcula `NeedsClarification` si queda alguna pregunta sin contestar, `NoChangesProposed` si no hay additions/invalidations, o `DraftForReview` en otro caso. Añade prefijo `IncompleteHistory:` si falta extracción histórica o la extracción actual no está completada. El DTO separa `State` persistido, `Status` informativo y `Ready` calculado por servidor; una propuesta Pending con revisión cambiada muestra `Stale:Regenerate` [O4:71–75, O3].

No existe un resultado `Partial` de Optimization equivalente a Search: recall limitado se comunica con cobertura y warnings; una salida final truncada, un assessment inválido o presupuesto excedido que impida resultado válido no autoriza guardar una propuesta parcial. Los límites de hits pueden ocultar evidencia **sin un flag por query**; siempre se advierte que el matching léxico no es exhaustivo. `NoChangesProposed` no prueba ausencia de contradicciones [O1, O3, O4, O9].

| Fallo | Tratamiento |
|---|---|
| Contexto histórico no disponible | Se excluye del fetch; no habilita invalidation; el prompt pide preservar fact y aclarar si es necesario |
| Quote, índice causal o assessment no vinculados | Conflicto; no se prepara esa propuesta |
| Respuesta humana fabricada/cambiada | Rechazo por evidencia no autorizada |
| Revisión, fuente o generación cambiada | Conflicto; refrescar y regenerar |
| Salida inválida/refusal/longitud | Rechazo estructurado, no conservar prefijo de cambios |
| Excepción no prevista de planificación | La aplicación la convierte a `TimelineFactProviderException("Unavailable")`; endpoint 502 |
| Permisos / ausencia / argumentos / conflicto | 403 / 404 / 400 / 409 respectivamente |

**Flujo recomendado:** generar primero candidatos vigentes; comparar seeds y recuperar el contexto histórico que falte; proponer additions respaldadas y solo invalidaciones causales del mismo ámbito; preservar hechos compatibles y preguntar ante ambigüedad real; revisar propuesta y cobertura; aportar respuestas reales y volver a planificar si procede; aceptar exclusivamente el ID actual cuando no queden preguntas ni revisión obsoleta. Nunca tratar «más nuevo» o «misma entidad» como sustitución automática.

## 7. Salvedades de confianza y observabilidad

- El código valida vínculos y literalidad, no la verdad del juicio de contradicción. El assessment es transitorio del planner y no una evaluación independiente por el SemanticVerifier.
- Los datos de entrada, contexto histórico, propuestas anteriores y respuestas son no confiables como instrucciones. El aislamiento del Harness deshabilita web search, file memory, skills, listas de tareas (`TodoProvider`) y modo; acceso a archivos y agentes background no se configuran. Eso limita capacidades, no prueba resistencia universal a prompt injection [O8:156–184].
- A diferencia de la proyección model-visible de Search, las tools de revisión serializan facts con fingerprints/procedencia y contextos completos. No hay anonimización general del texto autorizado [O6, O9].
- El ciclo de extracción guarda respuestas con **cero llamadas** y crea sucesor solo por resume/regeneración explícitos. NeedsClarification persiste feedback/preguntas atómicamente; Rejected sin preguntas termina Failed/NeedsCorrection, sin Retry semántico ni reparación automática. Detalle y edición muestran controles con dirty/stale/save/review/clarification busy, drafts y refrescos GET seguros. Esto no sustituye las preguntas de reconciliación ni cambia las tools/presupuesto del planner [O16].
- `20261026000100_ExtractionGenerationFeedback` añade **una columna nullable text**, `ExtractionFeedbackJson`, ligada al predecesor exacto y no probatoria. Está **PREPARADA, NO APLICADA**; DB persistente exige aplicarla con autorización humana. No hubo reinicio ni despliegue [O16].
- Captura de contenido de telemetría tiene default `true`; la política del host puede exportar prompts, mensajes y herramientas. Contabilizar uso no es imponer presupuesto monetario ni probar coste o rendimiento de producción [O15].

## 8. Verificación y límites de este análisis

T2 independiente combinado (`mvw71169-g-e85p`): **1.386 backend**, **38 frontend enfocados / 95 completos**, cero fallos/omitidos; enfocado backend previo **225** válido y repetición del padre **38** frontend verde. Build Vite aislado **37 módulos**, `envDir:false`, salida temporal del SO. React de producción en Chrome: **33 aserciones** detalle/edición, **66 peticiones falsas interceptadas (56 GET/10 escrituras)**, cero destinos inesperados/errores de página. Es evidencia determinista/de interacción del ciclo humano y regresión, **no exactitud del planner, extractor o verificador**; sin backend/modelo/PostgreSQL reales.

La primera suite completa obtuvo **1.385 aprobados/1 fallo** por comparar el target histórico con el snapshot actual en `TimelineFactModelTests.Npgsql_model_has_real_vector_mapping_and_migration_snapshot_matches_without_connecting`. Se seleccionó el último target registrado de `IMigrationsAssembly`, conservando asserts; pasaron **36** tests independientes de migración/modelo y los **1.386** completos finales. Targets históricos intactos. ASSESS nativo no disponible por ámbito untracked no declarado; fallback independiente de alto riesgo completado. Solo warnings Node `MODULE_TYPELESS_PACKAGE_JSON`/Git LF/CRLF. RED histórico y GREEN enfocado 225 del escritor no son un RED reejecutado independientemente.

T3 solo inspeccionó código y estructura/referencias: documentación pasiva sin RED/GREEN significativo ni nuevas suites/cambios en Optimization. Evidencia recibida: `odd/tasks/extraction-verification-feedback.md`. No hubo aplicación de migración, reinicio, despliegue ni garantía de calidad de DB completa; el navegador con APIs falsas no acredita un journey autenticado integral, juicio semántico ni defaults internos de SDK.

## 9. Referencias de fuente

| Ref. | Ruta desde la raíz | Evidencia |
|---|---|---|
| O1 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactOptimizationAgent.cs:13–176` | Prompt, esquema, gates, tres registros y ejecución |
| O2 | `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactHosting.cs:19–40` | Inyección de Harness aislado y separación respecto al provider de extracción |
| O3 | `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactApplication.cs:96–135`; `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactApplication.cs:225–259` | Precondiciones, preparación, aceptación, DTO y warnings |
| O4 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactReviewPlanner.cs:28–278` | Seeds, evidencia, validación, estados y ausencia de guardado |
| O5 | `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactEndpoints.cs:15–92` | Body acotado, endpoints, aceptación ID-only y traducción de errores |
| O6 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Tools/TimelineFactReviewTools.cs:7–108` | Firmas, caché, 60 lecturas y contextos entregados |
| O7 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactService.cs:27–119`; `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactService.cs:140–164`; `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactService.cs:218–261` | Persistencia y aceptación transaccionales, autorización y causas |
| O8 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/CustomOpenAIClient.cs:20–35`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/CustomOpenAIClient.cs:117–210` | Diferencias de constructor, transporte no acotado en esta ruta y capacidades |
| O9 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactSearch.cs:7–75` | Scope cronológico, fetch, límites, proyección y autorización |
| O10 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactFullTextSearch.cs:12–58` | Léxico PostgreSQL y aproximación InMemory |
| O11 | `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFacts.cs:11–40`; `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFactExtractionInputs.cs:112–169` | Fingerprints, elegibilidad y generación durable |
| O12 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/AgentExecutionBudget.cs:95–118`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/AgentExecutionBudget.cs:157–179`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/LocalCustomHarnessAgent.cs:69–86` | Guard ambiental opt-in y run estructurado |
| O13 | `dotnet/src/ProjectTimeline.BusinessLogic/Services.cs:70–86` | Límites generales de entrada |
| O14 | `dotnet/src/ProjectTimeline.BusinessLogic/ProjectTimeline.BusinessLogic.csproj:6–8`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/AgentModels/TimelineFactReviewResult.cs:3–16` | Versiones, salida y assessment no independiente |
| O15 | `dotnet/src/ProjectTimeline.UI.WebApi/LogfireTelemetry.cs:63–65`; `dotnet/src/ProjectTimeline.UI.WebApi/LogfireAgentPrivacyProcessor.cs:36–48`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/AgentTelemetry.cs:55–84` | Captura y contabilidad, sin tope de gasto |
| O16 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactOpenAIProvider.cs:15–66`; `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFactGenerationFeedback.cs:7–163`; `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/App.jsx:411–525`; `dotnet/src/ProjectTimeline.Database/Migrations/20261026000100_ExtractionGenerationFeedback.cs:8–16` | Nuevo ciclo humano y persistencia, fuera del contrato de tools del planner |

**Conclusión:** Optimization prepara decisiones revisables y causalmente vinculadas, no modifica fuentes ni activa facts por sí mismo. Su presupuesto es de lecturas backend y su comparación sigue siendo juicio del modelo; no hereda los límites ni las fases single-pass de Search.

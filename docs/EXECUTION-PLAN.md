# GameHours engineering execution plan

Este archivo es la **fuente canónica de trabajo operativo** para GameHours.

Su propósito es que cada tanda tenga instrucciones suficientemente precisas para implementarse y revisarse sin depender de contexto perdido en una conversación. La skill `.commandcode/skills/gamehours-workflow/SKILL.md` define el método permanente; este archivo define el estado dinámico, el alcance autorizado y los gates pendientes.

No usar este documento como un backlog genérico. Cada tanda activa debe incluir **causa, objetivo, límites, implementación esperada, tests, validación, criterios de aceptación y exclusiones explícitas**.

---

## 1. Protocolo permanente de trabajo

### 1.1 Estados permitidos

- `READY_FOR_IMPLEMENTATION`: instrucciones cerradas; se puede implementar.
- `IMPLEMENTING`: cambios en curso.
- `REVIEW_REQUIRED`: implementación terminada; falta revisión independiente de diff/tests/CI.
- `CHANGES_REQUESTED`: la revisión encontró defectos concretos.
- `AUTOMATED_VERIFIED`: build/tests/CI correctos para el SHA exacto; puede faltar validación real.
- `MANUAL_VALIDATION_REQUIRED`: falta comportamiento real en Windows/hardware.
- `VERIFIED`: existe evidencia suficiente para cerrar la tanda.
- `BLOCKED`: una dependencia externa impide continuar.

Nunca marcar `VERIFIED` sólo porque compile o porque un agente afirme que funciona.

### 1.2 Flujo obligatorio

1. Leer `AGENTS.md`, esta planificación y la skill antes de modificar código.
2. Comprobar rama, working tree, HEAD y estado remoto reales.
3. Investigar antes de decisiones técnicas relevantes; priorizar documentación oficial y patrones ya existentes en GameHours.
4. Implementar sólo la tanda autorizada y resolver causa raíz, no síntomas.
5. Añadir o adaptar tests sin debilitarlos para conseguir verde.
6. Validar por capas: tests focalizados -> suite aplicable -> build Release -> CI Windows del SHA exacto -> publish/package cuando corresponda.
7. Hacer una segunda revisión completa del diff para detectar alcance accidental, duplicación, warnings, logs/debug, deuda técnica o documentación incoherente.
8. El implementador termina en `REVIEW_REQUIRED`; no autoaprueba su propio trabajo.
9. ChatGPT revisa el diff y la evidencia y decide `CHANGES_REQUESTED`, `AUTOMATED_VERIFIED`, `MANUAL_VALIDATION_REQUIRED` o `VERIFIED`.

### 1.3 Evidencia mínima

Para código que afecte a runtime o persistencia:

- SHA exacto;
- CI verde para ese SHA;
- build Release sin warnings/errores;
- todos los tests descubiertos verdes;
- explicación de cambios inesperados en el número de tests;
- prueba manual cuando el comportamiento dependa de WPF real, procesos reales, input/foco del SO, suspensión, filesystem instalado o empaquetado.

Compilar un target Windows desde otro SO no sustituye ejecutar el comportamiento real en Windows.

### 1.4 Reglas de alcance

- Reutilizar antes de crear abstracciones o dependencias.
- No cambiar convenciones de dominio para hacer un test más fácil.
- Si un test revela un defecto real necesario para la tanda, corregir la causa con el cambio mínimo y añadir cobertura.
- Si aparece un problema no necesario para cerrar la tanda, documentarlo y no ampliar el diff.
- No iniciar optimización de memoria sin mediciones.
- No añadir UI automation, polling, timers o frameworks nuevos salvo evidencia clara de que son la solución más simple y adecuada.

### 1.5 Relación con Command Code

`.commandcode/skills/gamehours-workflow/SKILL.md` es la disciplina permanente para Command Code. Debe:

- proteger el working tree;
- leer `AGENTS.md` y este plan;
- investigar antes de decidir;
- buscar causa raíz;
- mantener alcance controlado;
- medir antes de optimizar;
- validar por capas;
- revisar el diff una segunda vez;
- distinguir implementado / compilado / probado / CI / manual;
- no hardcodear ramas, SHAs, número de tests ni estados temporales.

Este documento, no la skill, decide qué tanda está autorizada.

---

# 2. Baseline confirmado de la foundation

**Rama:** `feat/desktop-foundation`

**HEAD de rama antes de abrir la tanda 3:** `250ae2d53bcd7355d0cf324cb7d342485f9b2153`

**HEAD funcional de la tanda 2 revisado:** `b2951c047f9ffe417317ef634cc9f8983080ddea`

**SHA base histórico de la tanda 2:** `de1ac9a247d07ef02dca3d0d9037b74e9101de55`

### Tanda 2 — integridad de `session_activity` + reloj visual minimizado

**Estado:** `AUTOMATED_VERIFIED`

Evidencia ya revisada:

- CI #604 (`32722661949`) verde sobre el HEAD funcional.
- Restore ✅.
- Build Release ✅, 0 warnings / 0 errors.
- `GameHours.Tests`: 106/106.
- `GameHours.Windows.Tests`: 80/80.
- Total descubierto/pasado: 186/186.
- Publish desktop smoke ✅.
- Package Velopack omitido correctamente mientras la PR sigue draft.
- `SqliteSessionActivityRepository.UpsertAsync()` protege la identidad autoritativa de la sesión sin schema nuevo ni FK incompatible con sesiones abiertas.
- Los read models mantienen defensa frente a filas históricas/corruptas.
- Los tests defensivos siembran telemetría válida y corrompen sólo `game_id`; `SqliteTime` sigue `internal` y no existe `InternalsVisibleTo` añadido para esos tests.
- El reloj visual WPF se detiene también con `WindowState.Minimized` y se reactiva al restaurar sin cambiar el tracking autoritativo.
- CI #605 (`32723216942`) también quedó verde sobre `250ae2d53bcd7355d0cf324cb7d342485f9b2153`, que sólo sincronizó documentación.

La tanda 2 **no** se marca `VERIFIED` todavía porque el gate real de Windows sigue pendiente.

---

# 3. Tanda 3 — automatizar telemetría de atención y coherencia del detalle de sesión

**Estado:** `AUTOMATED_VERIFIED`

**Prioridad:** alta.

**SHA base de la tanda:** `250ae2d53bcd7355d0cf324cb7d342485f9b2153`

**HEAD funcional revisado:** `a4e2d5495f967756c14d00b558ef941944c0f809`

## 3.1 Motivo

El gate manual de la foundation contiene escenarios que sí requieren Windows real, pero también contiene lógica determinista que podemos cubrir automáticamente antes de pedir una pasada humana.

No queremos convertir al usuario en tester repetitivo de comportamientos que una suite puede verificar con mayor precisión. Tampoco queremos fingir que una simulación sustituye al foco/input/WPF reales del sistema operativo.

Esta tanda automatiza **sólo** lo razonablemente determinista de:

- sesión y telemetría de atención;
- detalle de sesión e identidad de navegación.

La validación manual queda diferida, no eliminada.

## 3.2 Resultado — telemetría de atención determinista

Se revisó primero la implementación existente. `SessionActivityPolicy` ya era la autoridad pura para decidir cuánto de cada intervalo observado cuenta como `focused` y `active`; no se creó una segunda máquina de estados.

Cobertura añadida:

- frontera AFK de 2 minutos justo antes, exactamente en el límite y justo después;
- la semántica actual queda fijada: `active` sólo cuando `idleDuration < idleThreshold`; exactamente en el umbral ya no cuenta como activo;
- secuencia `foreground -> background -> foreground`, donde sólo los intervalos enfocados acumulan atención y no se reconstruye el intervalo perdido;
- recuperación de input después de AFK: sólo vuelven a contar como activos los intervalos posteriores, sin backfill;
- elapsed cero o negativo no puede producir duración negativa ni fabricada.

Se conservaron los tests previos de:

- AFK desactivado: foco observable, activo estimado deliberadamente no calculado;
- estado no enfocado;
- gaps de muestreo excesivos tratados como desconocidos;
- idle negativo tratado conservadoramente.

El tiempo ejecutado no se redefinió ni se hizo depender de foco/AFK.

### Política AFK configurada vs aplicada

Se inspeccionó `DesktopHost` y se confirmó el diseño actual:

- `StartAsync()` captura el timeout configurado como política aplicada del tracker y construye el `GameSessionEngine` con ese `IdleThreshold`;
- si se cambia el timeout con una partida activa, `ApplyPreferencesAsync()` guarda la nueva configuración pero difiere el reinicio del tracker hasta que no queden juegos activos;
- el valor aplicado se expone separadamente mediante `AppliedAfkTimeoutMinutes` / diagnósticos.

No se añadió una abstracción artificial sólo para simular este lifecycle. La decisión completa configurado-vs-aplicado depende del host/tracker real y permanece en el gate manual de Windows §4.2. La parte determinista de `DesktopPreferences` ya tiene cobertura de normalización, persistencia y AFK desactivado.

## 3.3 Resultado — coherencia del detalle de sesión

Se reforzó `DesktopSessionDetailServiceTests` para fijar, en una sesión finalizada con telemetría conocida:

- `SessionId` autoritativo;
- título del juego resuelto por el `GameId` de la sesión;
- inicio y fin;
- duración ejecutada;
- capture method;
- confidence;
- motivo de cierre;
- presencia de telemetría;
- focused, active, AFK, unfocused/unknown y umbral aplicado.

Se mantienen las defensas frente a telemetría histórica/corrupta cuyo `game_id` no coincide con la sesión.

Para navegación, se extrajo únicamente la resolución pura de identidad ya existente a `SessionDetailNavigation.TryResolveSessionId(...)`. `TryOpenFromVisual(...)` reutiliza ahora esa resolución, sin cambiar la arquitectura de navegación ni introducir UI automation.

Tests Windows fijan que:

- una fila registrada abre por el `SessionId` exacto asociado a esa fila;
- volver a registrar la misma fila sustituye la identidad anterior;
- `Guid.Empty` elimina/no inventa navegación;
- una fila de sesión del Calendario resuelve su propio `SessionId`;
- una fila de logro del Calendario no inventa un `SessionId`.

Esto cubre el contrato compartido por Actividad y detalle del juego, que registran sus filas mediante `SessionDetailNavigation.Register`, y el contrato específico del Calendario, que transporta `SessionId` directamente. La apertura visual/modal real sigue reservada al gate manual.

## 3.4 Revisión y validación final — 2026-08-24

**CI:** #610 (`32737872055`) — `success` sobre `a4e2d5495f967756c14d00b558ef941944c0f809`.

- Runner: Windows Server 2025 / .NET SDK 8.0.424.
- Restore ✅.
- Build Release ✅ — **0 warnings / 0 errors**.
- `GameHours.Tests` ✅ — **113/113**.
- `GameHours.Windows.Tests` ✅ — **85/85**.
- Total descubierto/pasado: **198/198**.
- Publish desktop smoke ✅.
- Package Velopack smoke omitido correctamente mientras la PR continúa draft.

Cambio de tests respecto a la tanda 2: +7 casos Core de política/intervalos y +5 casos Windows de identidad de navegación; no se eliminaron ni omitieron tests existentes.

Segunda pasada del diff desde `250ae2d53bcd7355d0cf324cb7d342485f9b2153`:

- cambios funcionales limitados a la extracción pura de identidad en `SessionDetailNavigation`;
- tests focalizados en política AFK, detalle e identidad;
- plan operativo actualizado;
- sin migraciones, dependencias, polling, timers nuevos, cambios de memoria, packaging, tracking autoritativo, logs temporales ni test skips.

**Resultado:** Tanda 3 `AUTOMATED_VERIFIED`. No se marca `VERIFIED` porque foco/input reales, lifecycle configurado-vs-aplicado y apertura WPF siguen necesitando Windows real.

---

# 4. Gate manual acumulado de la foundation

**Estado:** `MANUAL_VALIDATION_REQUIRED`

**Ejecución:** diferida intencionadamente hasta terminar las tandas automatizables previas, para hacer una única pasada humana más pequeña y con mayor cobertura previa.

La automatización **no elimina** estos checks reales.

## 4.1 WPF / interacción real

- inicio limpio;
- interacción inmediata tras mostrar la ventana;
- navegación Biblioteca / Actividad / Calendario / Estadísticas / Pendientes / Ajustes;
- apertura de Diagnóstico;
- ocultar/restaurar desde tray;
- minimizar/restaurar;
- cierre real desde la acción de salir;
- confirmar visualmente que el reloj se comporta correctamente al minimizar/restaurar.

## 4.2 Foco e input reales

- sesión real con AFK desactivado;
- Alt+Tab real y recuperación de foco;
- AFK real con umbral corto y reanudación de input;
- comprobar política configurada vs aplicada durante una partida real.

## 4.3 Detalle real

Abrir la misma sesión desde Actividad, Calendario y detalle del juego y confirmar que la UI muestra la sesión y métricas correctas.

## 4.4 Suspensión/reanudación real

- juego activo;
- suspender Windows;
- reanudar;
- no inventar tiempo durante la suspensión;
- segmentación/recovery conforme al timeline.

## 4.5 Pendientes/detección real

- candidato automático razonable;
- asociación a juego existente;
- alta manual de `.exe`;
- ignorar candidato;
- launcher/helper/anti-cheat/updater/crash reporter;
- una decisión no debe reaparecer como pendiente.

## 4.6 Portabilidad real

Con backup desechable:

- backup SQLite;
- restore;
- safety backup;
- export portable JSON;
- import idempotente;
- conflicto seguro sin tocar datos de producción.

## 4.7 Runtime impact

Medir durante el mismo intervalo:

| Estado | Duración | CPU | Private memory | Working set | Threads | Reconciliations delta |
| --- | --- | --- | --- | --- | --- | --- |
| Idle, ventana visible |  |  |  |  |  |  |
| Idle, tray |  |  |  |  |  |  |
| Juego activo y enfocado |  |  |  |  |  |  |
| Juego activo y sin foco |  |  |  |  |  |  |

No concluir que consume demasiado sólo por Working Set. Cualquier optimización de memoria parte de medición de Private Memory, GC heap/allocation rate y objetos retenidos.

## 4.8 Velopack antes de merge

- package smoke del HEAD exacto;
- instalación real Windows;
- inicio/cierre instalado;
- ruta de datos local;
- al menos un ciclo update/recovery cuando la infraestructura esté preparada.

---

# 5. Siguientes tandas automatizables previstas

**No autorizadas todavía.** Servirán para reducir más el gate manual después de decidir si se ejecuta ahora una pasada real o se abre otra tanda automatizable.

### Tanda 4 candidata

- lógica determinista de suspend/resume y recuperación;
- decisiones persistentes de `Pendientes`/clasificación sin depender del proceso real.

### Tanda 5 candidata

- backup/restore/export/import idempotente y conflictos seguros;
- pequeño harness/script de medición de runtime si puede reutilizar infraestructura existente sin añadir complejidad.

Cada una debe abrirse sólo de forma explícita. No agruparlas en un único diff grande.

---

# 6. Backlog posterior al cierre de la foundation

## 6.1 Supply chain

En PR separada después de cerrar la foundation:

- `packages.lock.json` / locked restore;
- Dependabot NuGet y GitHub Actions;
- CodeQL si encaja;
- secret scanning / push protection;
- Actions fijadas por SHA completo.

## 6.2 Optimización de memoria

No tocar todavía parámetros agresivos del GC.

Orden previsto:

1. medir Private Memory, Working Set, GC Heap Size y Allocation Rate;
2. capturar `gcdump` en idle y después de vistas pesadas;
3. comprobar árboles WPF retenidos y lifecycle de vistas;
4. lazy/disposable sólo donde aporte beneficio;
5. virtualizar listas largas;
6. limitar cachés de iconos/artwork;
7. mover agregaciones grandes a SQLite cuando corresponda;
8. auditar timers/watchers/event handlers;
9. volver a medir;
10. sólo entonces experimentar con GC si los datos lo justifican.

Evitar `GC.Collect()` periódico, `EmptyWorkingSet` como maquillaje, Server GC sin evidencia y cualquier optimización que complique el producto sin medición.

## 6.3 Beta pública

Gate posterior mínimo:

- firma de código;
- origen HTTPS de updates de solo lectura y sin credenciales embebidas;
- instalación limpia;
- actualización desde versión anterior;
- rollback/recovery de actualización;
- documentación de instalación/desinstalación y datos;
- SmartScreen evaluado con binario firmado.

---

# 7. Historial

## 2026-08-24 — creación del contrato operativo

- Se establece este archivo como fuente canónica dinámica y la skill como método permanente.
- Baseline histórico: `de1ac9a247d07ef02dca3d0d9037b74e9101de55`, CI #587, 180/180.

## 2026-08-24 — tanda 2 implementada y revisada

- Integridad autoritativa de `session_activity` y exclusión de `WindowState.Minimized` del reloj visual.
- Primer HEAD revisado `9a07b21f41a2a638b114aab151fb47abbc8dfe05`; CI #592 reveló dos tests Windows acoplados a `SqliteTime` interno.
- Se mantuvo `SqliteTime` interno y se corrigieron los tests sembrando datos válidos y corrompiendo sólo `game_id`.
- HEAD funcional final `b2951c047f9ffe417317ef634cc9f8983080ddea`; CI #604 verde, 186/186, publish correcto.
- HEAD documental posterior `250ae2d53bcd7355d0cf324cb7d342485f9b2153`; CI #605 verde.
- Tanda 2 queda `AUTOMATED_VERIFIED`; manual real sigue pendiente.

## 2026-08-24 — estrategia de validación manual diferida

- Se decide no bloquear el progreso por una pasada manual inmediata.
- Se mantiene intacta la obligación de validar WPF, input/foco, suspensión y packaging en Windows real antes de cerrar la foundation.
- Para reducir trabajo humano repetitivo, se autoriza primero una secuencia de tandas pequeñas que automaticen sólo los contratos deterministas.
- Tanda 3 abierta para telemetría de atención + coherencia del detalle de sesión; suspensión/Pendientes y portabilidad quedan como tandas posteriores separadas.

## 2026-08-24 — cierre automatizado de la tanda 3

- No había implementación de la tanda 3 subida por el agente; la rama remota seguía en el commit documental que la abrió.
- ChatGPT completó la cobertura determinista pendiente sin introducir una máquina de estados paralela.
- Se añadieron casos de frontera AFK, transición de foco, recuperación tras AFK e invariantes de elapsed.
- Se fijaron todos los metadatos autoritativos relevantes del detalle de sesión.
- Se extrajo la resolución pura de `SessionId` de la navegación existente y se cubrieron filas registradas y Calendario sin automatizar ventanas WPF.
- HEAD funcional `a4e2d5495f967756c14d00b558ef941944c0f809`; CI #610 verde, 0 warnings/0 errors, 113/113 Core + 85/85 Windows = 198/198, publish correcto.
- El lifecycle configurado-vs-aplicado de AFK se mantiene para validación real: no se añadió una abstracción falsa sólo para convertirlo en test unitario.
- Tanda 3 queda `AUTOMATED_VERIFIED`; la foundation sigue pendiente del gate manual real.

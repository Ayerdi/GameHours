# GameHours engineering execution plan

Este archivo es la **fuente canónica de trabajo operativo** para GameHours.

Su propósito es que cada tanda de trabajo tenga instrucciones suficientemente precisas para poder implementarse sin depender de contexto perdido en una conversación. La implementación puede realizarla el propietario del proyecto por su cuenta; después ChatGPT revisará el diff, la arquitectura, los tests, CI y, cuando corresponda, el comportamiento real. Tras esa revisión, este mismo archivo se actualizará dejando claro qué quedó verificado, qué debe corregirse y cuál es la siguiente tanda autorizada.

No usar este documento como un simple backlog. Cada tarea activa debe contener **causa, objetivo, límites, pasos de implementación, tests, validación, criterios de aceptación y cosas que explícitamente no deben hacerse**.

---

## 1. Protocolo permanente de trabajo

### 1.1 Estados permitidos

Cada tanda debe estar en uno de estos estados:

- `READY_FOR_IMPLEMENTATION`: las instrucciones están cerradas y se puede empezar a implementar.
- `IMPLEMENTING`: el propietario está realizando los cambios.
- `REVIEW_REQUIRED`: implementación terminada; ChatGPT debe revisar código, tests y CI.
- `CHANGES_REQUESTED`: la revisión encontró problemas concretos que deben corregirse.
- `AUTOMATED_VERIFIED`: build/tests/CI correctos para el SHA exacto, pero todavía puede faltar validación manual.
- `MANUAL_VALIDATION_REQUIRED`: falta probar comportamiento real en Windows/hardware.
- `VERIFIED`: la tanda está cerrada y existe evidencia suficiente.
- `BLOCKED`: hay una dependencia externa que impide continuar.

No marcar una tarea `VERIFIED` simplemente porque compile.

### 1.2 Flujo de trabajo obligatorio

1. ChatGPT revisa el estado real del repositorio y define una tanda concreta en este archivo.
2. El propietario implementa **sólo esa tanda**, evitando funcionalidades nuevas o refactors no relacionados.
3. Al terminar, el propietario comunica que está lista para revisión y, si es útil, proporciona resultados locales o capturas.
4. ChatGPT revisa:
   - diff completo desde el SHA base indicado aquí;
   - arquitectura y causa raíz;
   - duplicación y complejidad introducida;
   - invariantes de dominio/persistencia;
   - tests añadidos o modificados;
   - CI del SHA exacto;
   - documentación afectada;
   - regresiones plausibles.
5. Si hay problemas, ChatGPT cambia la tanda a `CHANGES_REQUESTED` y actualiza este archivo con instrucciones precisas.
6. Si pasa la revisión automatizada, se marca `AUTOMATED_VERIFIED` o `MANUAL_VALIDATION_REQUIRED` según corresponda.
7. Sólo después de la validación necesaria se marca `VERIFIED` y se prepara la siguiente tanda.

### 1.3 Evidencia mínima para afirmar que algo funciona

Para código que afecte a runtime o persistencia:

- SHA exacto de la rama;
- CI verde para ese SHA;
- build Release sin warnings/errores;
- todos los tests descubiertos verdes;
- explicación de cualquier cambio inesperado en el número de tests;
- prueba manual cuando el comportamiento depende de Windows, WPF, procesos, suspensión, input, filesystem o empaquetado instalado.

Para cambios puramente documentales no es necesaria prueba manual, pero sí revisar que la documentación no contradiga al código/CI actual.

### 1.4 Reglas para modificar este archivo

- ChatGPT es responsable de mantener actualizado este documento durante el ciclo de revisión.
- El propietario puede marcar trabajo realizado o añadir notas de implementación, pero no debe cambiar unilateralmente criterios de aceptación para hacer pasar una tarea.
- Si durante una implementación aparece un problema nuevo directamente relacionado, documentarlo bajo `Hallazgos durante la implementación` antes de ampliar el alcance.
- Si el problema nuevo no es necesario para terminar la tanda, anotarlo en `Backlog posterior` y no resolverlo todavía.
- Cada tanda debe indicar un SHA base para que la revisión posterior pueda comparar exactamente qué cambió.

### 1.5 Command Code

`.commandcode/skills/gamehours-workflow/SKILL.md` es la capa permanente de disciplina para Command Code.

La relación entre ambos documentos es intencionada:

- la **skill** define el método estable: proteger el working tree, leer contexto, investigar antes de decidir, resolver causa raíz, controlar alcance, validar por capas, hacer segunda pasada y no afirmar nada sin evidencia;
- este **EXECUTION-PLAN** contiene únicamente el estado y trabajo dinámico de la tanda actual.

La skill no debe hardcodear ramas, SHAs, números de tests ni estados temporales. Tampoco debe sustituir este plan, inventar convenciones ausentes del repositorio ni autoaprobar una tanda.

---

# 2. Tanda activa — endurecer integridad de telemetría y eliminar wake-up visual innecesario

**Estado:** `CHANGES_REQUESTED`

**Prioridad:** alta, antes de continuar con pruebas manuales extensas de la foundation.

**Rama:** `feat/desktop-foundation`

**SHA base revisado:** `de1ac9a247d07ef02dca3d0d9037b74e9101de55`

**Baseline automatizado conocido:** CI #587 verde; 101 tests Core + 79 Windows = 180/180; build Release 0 warnings / 0 errors; publish desktop smoke correcto. Velopack package smoke sigue omitido mientras la PR esté en draft.

## 2.0 Revisión ChatGPT — 2026-08-24

**HEAD funcional revisado:** `9a07b21f41a2a638b114aab151fb47abbc8dfe05`

**CI revisada:** #592 (`32717144644`) — `failure` en el paso **Build**. Restore pasó; tests y publish quedaron omitidos porque la solución no compiló.

### Lo que está aprobado conceptualmente

- **Tarea A:** la dirección de implementación es correcta. `SqliteSessionActivityRepository.UpsertAsync()` usa una única operación `INSERT ... SELECT ... WHERE EXISTS(...)` contra `open_sessions` o `sessions`, mantiene el `ON CONFLICT` dentro de la misma sentencia y rechaza `affected == 0` con `InvalidOperationException`. No se añadió schema v6, FK nueva, dependencia ni cambio de lifecycle.
- Los tests Core cubren semánticamente los casos exigidos: sesión activa válida, sesión finalizada válida, `SessionId` sin identidad autoritativa, juego incorrecto para sesión finalizada, juego incorrecto para sesión activa y protección del `ON CONFLICT` frente a cambio de juego.
- **Tarea B:** la dirección de implementación también es correcta. `MainWindow` escucha `StateChanged`, `ShouldRunSessionClock` recibe `WindowState` y excluye `Minimized`; el test parametrizado contiene los cinco casos mínimos exigidos.
- Los filtros defensivos de los read models se mantienen. Es correcto conservar pruebas capaces de simular una fila histórica/corrupta que ya no puede producirse a través del repositorio normal.
- **Skill de Command Code:** resuelta por ChatGPT y mantenida como protocolo permanente. Se conserva porque es útil para el flujo local, pero fue rediseñada para no hardcodear ramas/SHAs/estado de `main`, no inventar trailers de coautor y tomar siempre el trabajo dinámico de este plan. Exige el mismo rigor de investigación, alcance, validación y segunda pasada en cualquier futura tanda.

### Cambio obligatorio antes de una nueva revisión

#### Reparar los tests defensivos sin hacer público `SqliteTime`

CI #592 falla con:

```text
DesktopSessionDetailServiceTests.cs(...): CS0122: 'SqliteTime' is inaccessible due to its protection level
DesktopStatisticsActivityTests.cs(...): CS0122: 'SqliteTime' is inaccessible due to its protection level
```

`SqliteTime` es correctamente `internal` a `GameHours.Storage`. **No cambiar su visibilidad y no añadir `InternalsVisibleTo` sólo para resolver estos tests.** Eso ampliaría innecesariamente la superficie de la capa Storage.

La solución preferida es más simple y menos acoplada al formato interno de timestamps:

1. en cada test defensivo, crear primero una fila de telemetría **válida** mediante `SqliteSessionActivityRepository.UpsertAsync()` usando el `SessionId` y `GameId` autoritativos;
2. después abrir una conexión SQLite y ejecutar únicamente un `UPDATE` directo que corrompa el campo cuya defensa queremos probar:

```sql
UPDATE session_activity
SET game_id = $wrong_game_id
WHERE session_id = $session_id;
```

3. comprobar que el `UPDATE` afectó exactamente una fila;
4. cargar `DesktopSessionDetailService` / `DesktopStatisticsService` y mantener las aserciones defensivas actuales.

Esto es preferible a reconstruir manualmente un `INSERT` completo porque:

- no duplica la serialización privada de `SqliteTime`;
- no acopla el test a todas las columnas actuales de `session_activity`;
- simula con precisión el caso relevante: una fila originalmente válida cuyo `game_id` quedó corrupto;
- hace que una futura evolución no relacionada del esquema rompa menos estos tests.

Aplicar este patrón a:

- `DesktopSessionDetailServiceTests.Load_MismatchedActivityGame_DoesNotAttributeTelemetryToSession`;
- `DesktopStatisticsActivityTests.ReadModels_IgnoreFinalizedActivityThatDoesNotMatchItsAuthoritativeSession`.

No eliminar ni debilitar esas pruebas.

### Gate para volver a `REVIEW_REQUIRED`

Antes de volver a pedir revisión:

1. aplicar la corrección anterior;
2. no modificar de nuevo la skill salvo que aparezca un defecto concreto en ella;
3. ejecutar build/test local en Windows si está disponible;
4. pushear el SHA final;
5. esperar a que la CI del SHA final complete;
6. **CI debe pasar Restore + Build + Test + Publish**; el package Velopack puede seguir omitido mientras la PR sea draft;
7. informar del número real de tests descubiertos/pasados; no asumir un total por adelantado.

No realizar todavía pruebas manuales de la foundation ni empezar optimización de memoria. La tanda sigue abierta hasta que este gate quede verde.

## 2.1 Objetivo general

Cerrar dos defectos de robustez detectados durante la revisión del último hardening sin introducir arquitectura nueva:

1. impedir que `session_activity` pueda persistirse para una sesión inexistente o con un `game_id` distinto del propietario real de la sesión;
2. evitar que el `DispatcherTimer` del reloj de sesión despierte cada segundo cuando la ventana está minimizada y el usuario no puede ver ese reloj.

Los filtros defensivos ya añadidos a los read models deben permanecer. La intención es tener **integridad en escritura + defensa en lectura**.

---

## 2.2 Tarea A — integridad autoritativa de `session_activity`

### Problema actual

`session_activity` contiene `session_id` y `game_id`, pero el esquema sólo referencia `games(id)` desde `game_id`. El `session_id` no puede tener una foreign key directa a `sessions(id)` porque la telemetría se persiste mientras la partida todavía está abierta y en ese momento la identidad autoritativa está en `open_sessions`, no aún en `sessions`.

Hoy `SqliteSessionActivityRepository.UpsertAsync()` valida duraciones y política AFK, pero permite escribir, por ejemplo:

- `session_id` de una sesión del juego A;
- `game_id` del juego B.

Los read models nuevos ignoran correctamente ese dato inconsistente, pero la capa de persistencia sigue aceptándolo. Esto resuelve el síntoma en presentación, no la causa.

### Invariante requerida

Una fila de `session_activity` sólo puede escribirse cuando existe una identidad autoritativa que cumpla una de estas condiciones:

```text
open_sessions.session_id == metrics.SessionId
AND open_sessions.game_id == metrics.GameId
```

**o**

```text
sessions.id == metrics.SessionId
AND sessions.game_id == metrics.GameId
```

Debe ser imposible insertar o actualizar una fila con un juego diferente.

### Restricciones importantes

- **NO crear schema v6** sólo para esto.
- **NO añadir una FK directa `session_activity.session_id -> sessions.id`**, porque rompería las muestras/checkpoints de una sesión activa.
- **NO eliminar los filtros defensivos** introducidos recientemente en `DesktopHost`, `DesktopGameInsightService`, `DesktopStatisticsService` y `DesktopSessionDetailService`.
- **NO hacer dos consultas no atómicas** del tipo `SELECT` y después `INSERT` si puede evitarse; entre ambas existe una ventana de carrera innecesaria.
- **NO añadir una dependencia nueva**.
- **NO modificar el lifecycle del tracker** salvo que una prueba demuestre que la solución elegida lo requiere.

### Implementación preferida

Resolver la autorización de escritura dentro de la misma operación SQL de `SqliteSessionActivityRepository.UpsertAsync()`.

Una forma razonable es transformar el `VALUES (...)` actual en un `INSERT ... SELECT ... WHERE EXISTS (...)` que permita la operación sólo si existe un `open_sessions` o `sessions` compatible.

Forma conceptual, adaptar al SQL real del repositorio:

```sql
INSERT INTO session_activity (...)
SELECT
    $session_id,
    $game_id,
    ...
WHERE EXISTS (
    SELECT 1
    FROM open_sessions
    WHERE session_id = $session_id
      AND game_id = $game_id

    UNION ALL

    SELECT 1
    FROM sessions
    WHERE id = $session_id
      AND game_id = $game_id
)
ON CONFLICT(session_id) DO UPDATE SET
    ...;
```

La sentencia final debe impedir además que un `ON CONFLICT(session_id)` existente pueda ser usado para cambiar silenciosamente el `game_id` a uno incorrecto.

Después de `ExecuteNonQueryAsync`, comprobar el número de filas afectadas. Si no se escribió ninguna porque la identidad no existe o no coincide, lanzar una excepción explícita y comprensible (`InvalidOperationException` es adecuada salvo que exista una convención mejor ya usada por este repositorio).

El mensaje debe indicar que la telemetría no corresponde a una sesión abierta/finalizada autoritativa, sin exponer datos sensibles.

### Orden real que debe seguir funcionando

No cambiar estas propiedades del lifecycle:

**Durante una partida:**

```text
open_sessions upsert
→ session_activity upsert no finalizado
```

**Fin normal:**

```text
sessions insert
→ open_sessions delete
→ session_activity upsert finalizado
```

**Recuperación tras interrupción:**

```text
sessions insert recuperado
→ session_activity finalizado/normalizado
→ open_sessions delete
```

La solución debe aceptar los tres escenarios.

### Tests obligatorios

Añadir tests focalizados en `SqliteSessionActivityRepositoryTests` o ubicación equivalente:

1. **Sesión activa válida**
   - crear juego;
   - crear `open_session` compatible;
   - escribir métricas;
   - comprobar round-trip correcto.

2. **Sesión finalizada válida**
   - crear juego y `PlaySession`;
   - escribir métricas;
   - comprobar round-trip.

3. **SessionId inexistente**
   - juego existente;
   - sin `open_sessions` ni `sessions` para ese ID;
   - `UpsertAsync` debe fallar;
   - comprobar que no quedó fila persistida.

4. **GameId incorrecto en sesión finalizada**
   - sesión pertenece a juego A;
   - intentar métricas con mismo `SessionId` pero juego B;
   - debe fallar;
   - no debe persistir telemetría para B.

5. **GameId incorrecto en sesión activa**
   - `open_session` pertenece a A;
   - métricas intentan B;
   - debe fallar.

6. **No permitir corromper una fila válida mediante conflict update**
   - persistir primero métricas válidas para A;
   - intentar repetir mismo `SessionId` con B;
   - operación debe fallar;
   - volver a leer y comprobar que la fila válida original sigue perteneciendo a A y mantiene datos coherentes.

7. Mantener verdes los tests de read models que ignoran una fila histórica/malformada. Esa defensa sigue siendo intencionada para corrupción o bases de desarrollo antiguas.

### Criterio de aceptación de Tarea A

Se considera correcta cuando:

- ninguna API normal del repositorio puede crear una nueva asociación `session_activity` incoherente;
- seguimiento activo sigue pudiendo guardar métricas antes de que exista `sessions`;
- finalización normal y recuperación siguen funcionando;
- datos inválidos previos no se presentan como telemetría válida;
- no se ha creado una migración innecesaria;
- tests focalizados y suite completa pasan.

---

## 2.3 Tarea B — reloj visual sólo cuando realmente puede verse

### Problema actual

El reloj de tiempo transcurrido de la sesión usa un `DispatcherTimer` de 1 segundo. El último hardening evita que esté activo cuando:

- no hay sesión;
- la ventana se oculta (`Hide()`, tray).

Sin embargo, una ventana WPF minimizada puede seguir teniendo `IsVisible == true`. En ese estado el usuario no puede ver el reloj, por lo que no hay motivo para despertar el dispatcher una vez por segundo.

### Comportamiento requerido

El reloj de UI debe ejecutarse únicamente cuando se cumplen las tres condiciones:

```text
hay sesión activa
AND ventana visible
AND WindowState != Minimized
```

Esto sólo afecta a presentación. Nunca debe cambiar tracking, checkpoints, foco, AFK o duración autoritativa.

### Implementación sugerida

- ampliar `ShouldRunSessionClock(...)` para tener en cuenta el estado de ventana;
- llamar a `UpdateSessionTimerState()` también desde `StateChanged`;
- mantener `IsVisibleChanged`;
- no añadir polling ni otro timer;
- al restaurar desde minimizado, el reloj puede recalcularse desde `DateTimeOffset.UtcNow - startedAt`; no hay necesidad de acumular ticks perdidos.

Ejemplo de contrato puro:

```csharp
ShouldRunSessionClock(startedAt, isVisible, windowState)
```

con resultado verdadero sólo para sesión activa + visible + no minimizada.

### Tests obligatorios

Extender los tests puros del reloj para incluir como mínimo:

- sin sesión + visible + normal → false;
- sesión + no visible + normal → false;
- sesión + visible + minimizada → false;
- sesión + visible + normal → true;
- sesión + visible + maximizada → true.

Si el método usa una abstracción distinta a `WindowState`, probar el mismo contrato semántico.

### Criterio de aceptación de Tarea B

- ningún wake-up por el reloj visual cuando está idle, oculto en tray o minimizado;
- el reloj vuelve a actualizarse correctamente al restaurar la ventana;
- no se añade trabajo periódico nuevo;
- tracking no depende del timer de UI.

---

## 2.4 Revisión obligatoria antes de entregar la tanda

Antes de marcar `REVIEW_REQUIRED`, revisar el diff buscando:

- migraciones de esquema innecesarias;
- queries duplicadas;
- `catch { }` nuevo que oculte errores;
- `GC.Collect`, working-set trimming o supuestas optimizaciones de memoria no relacionadas;
- cambios en tracking no exigidos por esta tanda;
- tests debilitados/eliminados;
- nuevos warnings;
- documentación que afirme validación manual inexistente;
- hardcodes del número total de tests en docs permanentes.

### Comandos locales recomendados

No usar comandos que cierren la sesión interactiva de PowerShell.

```powershell
cd <RUTA-DEL-REPO>

git status --short
git rev-parse HEAD

dotnet restore GameHours.sln

dotnet build GameHours.sln -c Release --no-restore

if ($LASTEXITCODE -eq 0) {
    dotnet test GameHours.sln `
      -c Release `
      --no-build `
      --logger "console;verbosity=normal"
}
```

Si build o tests fallan, no continuar ocultando el error. Corregir la causa y volver a ejecutar.

### Entrega al revisor

Cuando termines, comunica al menos:

- SHA final;
- resumen de implementación;
- resultado build;
- número de tests descubiertos y pasados;
- cualquier decisión distinta de la implementación sugerida y por qué;
- cualquier hallazgo nuevo.

ChatGPT comparará el SHA final contra `de1ac9a247d07ef02dca3d0d9037b74e9101de55`, revisará el diff y actualizará este archivo.

---

# 3. Gate posterior — validación real de la foundation

**Estado:** `BLOCKED` hasta cerrar la tanda 2 y hasta disponer de tiempo para pruebas en Windows.

No ejecutar todavía como sustituto de la tanda activa.

Cuando la tanda 2 quede automatizadamente verificada, el siguiente gate será probar el SHA exacto en Windows real.

## 3.1 Smoke de aplicación

- inicio limpio;
- interacción inmediata tras mostrar la ventana;
- navegación Biblioteca / Actividad / Calendario / Estadísticas / Pendientes / Ajustes;
- apertura de Diagnóstico;
- cierre real desde la acción de salir;
- ocultar/restaurar desde tray;
- minimizar/restaurar y confirmar que el reloj de sesión no genera trabajo visible cuando no corresponde.

## 3.2 Sesión y telemetría de atención

- sesión con AFK desactivado;
- comprobar ejecutado + foco;
- activo estimado debe quedar no disponible;
- Alt+Tab: ejecutado sigue, foco se detiene;
- sesión con AFK = 2 min;
- juego en primer plano sin input >2 min;
- confirmar que activo deja de crecer y AFK aumenta;
- reanudar input y confirmar recuperación de activo;
- cambiar AFK durante partida y comprobar política configurada vs aplicada.

## 3.3 Detalle de sesión

Abrir la misma sesión desde:

- Actividad;
- Calendario;
- detalle del juego.

Verificar:

- sesión correcta, no otra por posición visual;
- inicio/fin;
- ejecutado;
- foco;
- activo estimado cuando exista;
- AFK cuando exista;
- fuera de foco/no observado;
- umbral AFK;
- captura/confianza;
- motivo de cierre.

## 3.4 Suspensión/reanudación

- juego activo;
- suspender Windows;
- reanudar;
- comprobar que no se inventa juego durante suspensión;
- comprobar segmentación/recovery conforme a las reglas de timeline.

## 3.5 Pendientes/detección

- candidato automático razonable;
- asociación a juego existente;
- alta manual de `.exe`;
- ignorar candidato;
- clasificación launcher/helper/anti-cheat/updater/crash reporter;
- comprobar que una decisión no reaparece como pendiente.

## 3.6 Portabilidad y recuperación

Con backup desechable:

- crear backup SQLite;
- restaurar backup;
- confirmar safety backup;
- export portable JSON;
- import idempotente;
- probar conflicto seguro sin tocar datos de producción.

## 3.7 Runtime impact

Medir durante el mismo intervalo en cada estado:

| Estado | Duración | CPU | Private memory | Working set | Threads | Reconciliations delta |
| --- | --- | --- | --- | --- | --- | --- |
| Idle, ventana visible |  |  |  |  |  |  |
| Idle, tray |  |  |  |  |  |  |
| Juego activo y enfocado |  |  |  |  |  |  |
| Juego activo y sin foco |  |  |  |  |  |  |

No concluir que “consume demasiado” sólo por Working Set o número de hilos. La optimización de memoria se decidirá después de medir private memory, GC heap/allocation rate y objetos retenidos.

## 3.8 Velopack

Antes de merge:

- ejecutar package smoke del HEAD exacto;
- instalar paquete real en Windows;
- inicio/cierre desde instalación;
- comprobar ruta de datos local;
- validar al menos un ciclo de update/recovery cuando la infraestructura de update esté lista.

---

# 4. Backlog posterior al squash merge de la foundation

**Estado:** no autorizado mientras la foundation siga abierta/draft salvo corrección estrictamente necesaria para el gate.

## 4.1 Supply-chain / mantenimiento

Abrir PR pequeña separada para:

- `packages.lock.json`;
- restore locked en CI;
- Dependabot para `nuget`;
- Dependabot para `github-actions`;
- CodeQL, preferiblemente default setup si encaja;
- revisar secret scanning / push protection;
- mantener Actions fijadas por SHA completo.

No mezclar esto dentro de la PR foundation salvo necesidad demostrada.

## 4.2 Optimización de memoria

No tocar todavía parámetros agresivos del GC.

Orden de investigación previsto:

1. medir Private Memory, Working Set, GC Heap Size y Allocation Rate;
2. capturar `gcdump` en idle y después de navegar por vistas pesadas;
3. comprobar árboles WPF retenidos y lifecycle de vistas;
4. hacer vistas pesadas lazy/disposable cuando aporte beneficio real;
5. virtualizar listas largas;
6. limitar cachés de iconos/artwork con política acotada/LRU;
7. mover agregaciones grandes a SQLite en lugar de materializar todo el historial;
8. auditar timers/watchers/event handlers que puedan retener objetos;
9. volver a medir;
10. sólo entonces estudiar `System.GC.ConserveMemory` como experimento controlado si el heap gestionado lo justifica.

Evitar:

- `GC.Collect()` periódico;
- `EmptyWorkingSet` como maquillaje de Task Manager;
- Server GC para una app cliente sin evidencia;
- LowLatency GC con el objetivo de ahorrar memoria;
- NativeAOT/trimming mientras WPF no sea una ruta segura para este proyecto.

## 4.3 Beta pública

No publicar beta sólo porque la foundation esté fusionada.

Gate mínimo posterior:

- firma de código;
- origen HTTPS de actualizaciones, sólo lectura y sin credenciales embebidas;
- instalación limpia;
- actualización desde versión anterior;
- rollback/recovery ante actualización fallida;
- documentación de instalación/desinstalación y ubicación de datos;
- comportamiento de SmartScreen evaluado con binario firmado.

---

# 5. Historial de revisiones de este plan

## 2026-08-24 — creación

- Se establece `docs/EXECUTION-PLAN.md` como contrato operativo entre planificación, implementación y revisión.
- Baseline de código revisado: `de1ac9a247d07ef02dca3d0d9037b74e9101de55`, CI #587 verde, 180/180 tests.
- Primera tanda activa: integridad de escritura de `session_activity` + detener timer visual también al minimizar.
- Se mantienen bloqueadas nuevas funcionalidades y optimizaciones de RAM hasta cerrar la foundation y medir en hardware.
- Los commits que crean y mantienen este archivo son puramente documentales. La implementación de la tanda 2 debe seguir revisándose contra el SHA base de código `de1ac9a247d07ef02dca3d0d9037b74e9101de55`, no contra estos commits documentales.

## 2026-08-24 — implementación de la tanda 2 (integridad `session_activity` + reloj minimizado)

- Se implementó la Tarea A (integridad autoritativa de `session_activity` vía `INSERT ... SELECT ... WHERE EXISTS` + `InvalidOperationException` en 0 filas) y la Tarea B (excluir `Minimized` en `ShouldRunSessionClock` + `StateChanged`).
- Verificado en Linux (contenedor `dotnet/sdk:8.0`): build Release 0 warnings/0 errors y `GameHours.Tests` **106/106** verdes en la parte Core (Tarea A).
- Pendiente: compilar y pasar `GameHours.Tests` completo en CI y `GameHours.Windows.Tests`/build de la solución completa en Windows (Tarea B). La tanda quedó en `REVIEW_REQUIRED` hasta esa validación.

## 2026-08-24 — revisión ChatGPT de la tanda 2

- Revisado el HEAD funcional `9a07b21f41a2a638b114aab151fb47abbc8dfe05` contra `de1ac9a247d07ef02dca3d0d9037b74e9101de55`.
- La implementación de integridad SQL y el contrato del reloj minimizado quedan **aprobados conceptualmente**.
- CI #592 falló en Build por dos usos desde Windows.Tests del helper `internal` `SqliteTime`; tests y publish no llegaron a ejecutarse.
- Se solicita mantener `SqliteTime` interno y reescribir las dos pruebas defensivas sembrando primero una fila válida por el repositorio y corrompiendo después únicamente `game_id` mediante SQL directo.
- Estado de la tanda cambiado a `CHANGES_REQUESTED`. El gate de pruebas reales continúa bloqueado hasta CI verde del siguiente SHA.

## 2026-08-24 — rediseño de la skill permanente de Command Code

- Se confirma que `.commandcode/skills/gamehours-workflow/SKILL.md` es intencionada y debe permanecer en el repositorio para que Command Code trabaje con el mismo rigor de ingeniería.
- ChatGPT la rediseñó siguiendo el modelo de skills de proyecto de Command Code: frontmatter válido, `name` coincidente con el directorio, descripción orientada a activación y contenido estable con progressive disclosure hacia `AGENTS.md` y este `EXECUTION-PLAN.md`.
- La skill ya no hardcodea ramas/SHAs/estado de `main` ni un número de tests; tampoco inventa trailers de coautor.
- La skill obliga a: proteger el working tree, leer las fuentes canónicas, investigar antes de decisiones relevantes, buscar causa raíz, mantener alcance controlado, medir rendimiento antes de optimizar, validar por capas, revisar el diff en una segunda pasada y distinguir implementado/compilado/testeado/CI/manual.
- El único cambio obligatorio que queda abierto en la tanda 2 es reparar los dos tests Windows que usan `SqliteTime` interno y recuperar CI verde.
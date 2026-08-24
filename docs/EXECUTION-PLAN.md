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

# 3. Tanda activa — automatizar telemetría de atención y coherencia del detalle de sesión

**Estado:** `READY_FOR_IMPLEMENTATION`

**Prioridad:** alta.

**SHA base de la tanda:** `250ae2d53bcd7355d0cf324cb7d342485f9b2153`

## 3.1 Motivo

El gate manual de la foundation contiene escenarios que sí requieren Windows real, pero también contiene lógica determinista que podemos cubrir automáticamente antes de pedir una pasada humana.

No queremos convertir al usuario en tester repetitivo de comportamientos que una suite puede verificar con mayor precisión. Tampoco queremos fingir que una simulación sustituye al foco/input/WPF reales del sistema operativo.

Esta tanda automatiza **sólo** lo razonablemente determinista de los antiguos bloques de validación manual de:

- sesión y telemetría de atención;
- detalle de sesión y consistencia entre read models.

La validación manual queda diferida, no eliminada.

## 3.2 Objetivo A — telemetría de atención determinista

Antes de escribir tests nuevos, localizar y entender las abstracciones ya existentes para:

- estado foreground/background;
- actividad/idle del usuario;
- política AFK configurada y política aplicada a una sesión;
- checkpoints de `session_activity`;
- acumulación de focused / active / idle;
- finalización y lectura posterior de métricas.

No crear una segunda máquina de estados si ya existe una comprobable.

### Cobertura mínima requerida

Añadir tests deterministas para los siguientes contratos **si el diseño actual los expone sin depender del SO real**:

1. **AFK desactivado**
   - el tiempo ejecutado sigue siendo autoritativo;
   - el tiempo focused puede registrarse;
   - el tiempo active estimado queda no disponible conforme al contrato actual del producto.

2. **Foreground -> background -> foreground**
   - ejecutado continúa durante toda la sesión;
   - focused sólo crece mientras corresponde;
   - al recuperar foreground no se duplica ni reconstruye tiempo perdido.

3. **Umbral AFK de 2 minutos**
   - foreground sin input por debajo del umbral no cuenta aún como AFK;
   - al superar el umbral, active deja de crecer y AFK/idle se acumula según el modelo existente;
   - al volver input, active puede reanudarse sin backfill ni doble conteo.

4. **Frontera del umbral**
   - cubrir al menos un caso exactamente en el límite o inmediatamente alrededor del límite para fijar la semántica actual (`>=` frente a `>`), sin cambiarla arbitrariamente.

5. **Cambio de configuración AFK durante una sesión**
   - investigar primero cómo distingue hoy GameHours entre política configurada y política aplicada;
   - fijar con tests el comportamiento intencionado existente;
   - si se descubre una contradicción real entre código, modelo y UI, documentarla antes de modificar producción.

6. **Invariantes de acumulación**
   - ninguna duración negativa;
   - active/focused/idle no pueden crecer dos veces por el mismo intervalo;
   - no inventar relaciones matemáticas nuevas si no están respaldadas por el modelo actual; derivar los asserts de los tipos y reglas ya existentes.

### Restricciones del objetivo A

- No llamar APIs reales de foreground/input desde tests unitarios si eso vuelve la suite flaky.
- No usar sleeps de minutos; emplear reloj/tiempo controlable si ya existe, o introducir la abstracción mínima sólo si es necesaria y mejora el diseño.
- No modificar la definición de “tiempo ejecutado”: sigue siendo la métrica autoritativa independiente de foco/AFK.
- No añadir polling nuevo.

## 3.3 Objetivo B — coherencia del detalle de sesión

Investigar primero cómo llegan `Actividad`, `Calendario` y el detalle del juego al detalle de una sesión. Reutilizar servicios/read models existentes.

### Cobertura mínima requerida

1. Crear una sesión finalizada con telemetría conocida y comprobar que el detalle recuperado contiene la misma identidad autoritativa de sesión y juego.
2. Verificar de forma determinista, cuando existan, los mismos campos usados por la UI:
   - inicio/fin;
   - ejecutado;
   - focused;
   - active estimado;
   - AFK/idle;
   - fuera de foco/no observado;
   - umbral AFK aplicado;
   - captura/confianza;
   - motivo de cierre.
3. Cuando los puntos de entrada de Actividad, Calendario y detalle del juego puedan probarse sin WPF real, comprobar que todos terminan resolviendo **la misma sesión por identidad**, no por posición visual/índice.
4. Mantener y complementar las defensas existentes frente a `session_activity` de un juego distinto o datos parciales/corruptos.
5. Evitar duplicar grandes fixtures; extraer helpers de test sólo cuando reduzcan duplicación real y sigan siendo legibles.

### Restricciones del objetivo B

- No introducir UI automation ni screenshots para esta tanda.
- No reestructurar toda la navegación WPF sólo para hacerla testeable.
- Si una ruta sólo puede comprobarse honestamente con WPF real, dejarla en el gate manual y cubrir únicamente el servicio/read model subyacente.
- No cambiar textos o diseño visual salvo que un defecto funcional directamente relacionado lo exija.

## 3.4 Investigación obligatoria antes de implementar

El agente debe:

1. leer `AGENTS.md`, este plan y la skill;
2. revisar tests existentes antes de crear otros;
3. buscar utilidades de reloj, snapshots, builders o fixtures reutilizables;
4. inspeccionar el lifecycle real de `session_activity` y los read models de detalle;
5. usar documentación oficial de Microsoft sólo cuando una decisión dependa realmente de semántica .NET/Windows no evidente;
6. comparar alternativas y escoger el cambio más simple que preserve arquitectura e invariantes.

No empezar escribiendo una abstracción nueva por defecto.

## 3.5 Qué puede cambiar en producción

La tanda puede ser tests-only si el comportamiento ya es correcto.

Modificar código de producción **sólo** cuando una prueba bien planteada demuestre un defecto o una barrera de testabilidad que corresponda al diseño correcto. En ese caso:

- explicar causa raíz;
- hacer el cambio mínimo;
- añadir test de regresión;
- no ampliar hacia funcionalidades nuevas.

## 3.6 Validación obligatoria del agente

En Linux/local:

- ejecutar todos los tests que realmente sean compatibles con ese entorno;
- ejecutar build de proyectos compatibles cuando aporte evidencia;
- no afirmar que WPF/Windows fue validado localmente si no lo fue.

Después de push:

- esperar CI Windows del **SHA exacto**;
- exigir Restore + Build + Test + Publish desktop smoke verdes;
- informar del número real de tests descubiertos/pasados;
- si CI falla, corregir causa raíz y volver a validar el nuevo SHA;
- Velopack puede seguir omitido mientras la PR esté draft.

Antes de entregar:

- revisar diff completo desde `250ae2d53bcd7355d0cf324cb7d342485f9b2153`;
- comprobar que no hay cambios de memoria, packaging, nuevas features, migraciones, dependencias, logs temporales, test skips ni warnings nuevos;
- comprobar que documentación y código no afirman validación manual inexistente.

## 3.7 Criterios de aceptación

La tanda queda lista para revisión cuando:

- la lógica determinista relevante de telemetría de atención está cubierta por tests claros;
- el detalle de sesión tiene cobertura de identidad y métricas coherentes en los servicios/read models razonablemente automatizables;
- no se ha sustituido la necesidad de Windows real con mocks engañosos;
- todos los tests existentes siguen verdes;
- CI Windows pasa en el SHA final;
- cualquier cambio de producción está justificado por un defecto demostrado;
- el diff sigue siendo localizado y mantenible.

Al terminar, cambiar esta tanda únicamente a `REVIEW_REQUIRED` y comunicar SHA + evidencia. No marcarla `AUTOMATED_VERIFIED` ni `VERIFIED` por cuenta propia.

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

**No autorizadas todavía.** Servirán para reducir más el gate manual después de revisar la tanda 3.

### Tanda 4 candidata

- lógica determinista de suspend/resume y recuperación;
- decisiones persistentes de `Pendientes`/clasificación sin depender del proceso real.

### Tanda 5 candidata

- backup/restore/export/import idempotente y conflictos seguros;
- pequeño harness/script de medición de runtime si puede reutilizar infraestructura existente sin añadir complejidad.

Cada una debe abrirse sólo después de revisar la anterior. No agruparlas en un único diff grande.

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
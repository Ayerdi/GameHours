---
name: gamehours-workflow
description: "Guiar el trabajo en el repo GameHours siguiendo el fichero EXECUTION-PLAN.md, que cambia con cada tanda. Leo SIEMPRE el EXECUTION-PLAN.md que haya en la rama correcta como fuente operativa, ejecuto lo que ese plan pida en ese momento, y al terminar hago una segunda pasada verificando que cada punto del plan se cumplió de verdad. Invócala antes de tocar cualquier código de GameHours."
---

# GameHours workflow — leer y ejecutar el EXECUTION-PLAN.md vigente

Este repositorio opera por contrato: `docs/EXECUTION-PLAN.md` es la **fuente canónica** del trabajo operativo y **lo vas a ir cambiando en cada tanda**. Por eso esta skill NO asume qué tareas, comandos, tests ni criterios contiene el plan: lo lee de cero en cada ejecución, ejecuta lo que encuentre y verifica que se cumplió. Las únicas constantes son esta mecánica de trabajo y la rama correcta.

Cumple estos pasos en orden. No saltes ninguno.

---

## Paso 0 — Garantizar rama y código real

El código de la app vive en `feat/desktop-foundation`. `main` es una plantilla sin personalizar: nunca trabajes sobre `main`.

0.1 Verifica rama y que exista la solución:

```bash
cd <RUTA-DEL-REPO>
git rev-parse --abbrev-ref HEAD
ls GameHours.sln
```

0.2 Si estás en `main` o falta el código real, cambia a la rama correcta (o usá un worktree para no tocar `main`):

```bash
git checkout feat/desktop-foundation
# o
git worktree add ../GameHours-foundation feat/desktop-foundation && cd ../GameHours-foundation
```

0.3 Anota el SHA base: `git rev-parse HEAD`. Es la referencia para contrastar el diff al final.

---

## Paso 1 — Leer el plan vigente (fuente de verdad de ESTA tanda)

Abre `docs/EXECUTION-PLAN.md` SIEMPRE (incluso si lo leíste antes en otra sesión: puede haber cambiado). No te fíes de nada que conozcas de tandas pasadas.

Del plan vigente extrae y anota, si existen, los siguientes bloques — pero **solo los que el plan realmente contenga**:

- Estado global y de la tanda activa.
- SHA base.
- Tareas a implementar y su alcance.
- Restricciones y prohibiciones explícitas (qué NO hacer).
- Implementación preferida y tests obligatorios.
- Criterios de aceptación.
- Comandos de build/test que el plan indique.
- Gate posterior y backlog: **no tocar** salvo instrucción expresa del plan o del propietario.

> Si el plan no menciona alguna de estas cosas, no la inventes: te atiene a lo que el plan diga literalmente.

---

## Paso 2 — Respetar el protocolo y el rol

- Si el plan define estados o un flujo de trabajo (p. ej. `READY_FOR_IMPLEMENTATION`, `REVIEW_REQUIRED`, etc.), respétalos y repórtate a ellos.
- Rol por defecto de esta skill: **implementar la tanda activa** que el plan tenga lista para implementar. Si el plan pide otro rol (revisar en vez de implementar), haz lo que el plan indique.
- No cambies unilateralmente los criterios de aceptación para hacer pasar una tarea, ni amplíes el alcance: cualquier problema nuevo no solicitado se documenta, no se implementa de sopetón.

---

## Paso 3 — Ejecutar lo que pida el plan vigente

- Ejecuta exactamente lo que el plan de esta tanda pide, respetando sus restricciones, implementación preferida y tests obligatorios.
- NO añadas funcionalidades, refactors ni optimizaciones fuera del alcance de la tanda. Si algo del propio plan así lo pide, aplícalo.
- Si el plan da comandos de build/test concretos, úsalos; si no los da, usa una verificación razonable del proyecto (p. ej. `dotnet build` + `dotnet test` en este proyecto WPF/.NET).

---

## Paso 4 — Verificar (build, tests, y lo que el plan exija)

- Ejecuta los comandos de build/tests que el plan indique (o la verificación razonable del proyecto). No ocultes errores: si algo falla, corrígelo y vuelve a ejecutar.
- Compara el SHA final contra el SHA base y anota el resultado.

---

## Paso 5 — SEGUNDA PASADA: verificar que el plan se cumplió

Paso obligatorio y no negociable. Contraste lo que se hizo contra el plan VIGENTE:

5.1 Recorre `docs/EXECUTION-PLAN.md` punto por punto (los del presente plan, no los de tandas pasadas) y comprueba cada tarea, test y criterio de aceptación de forma literal.

5.2 Confirma que no se incumplieron las restricciones/prohibiciones que el plan listaba (p. ej. migraciones innecesarias, queries duplicadas, `catch{}` que oculten errores, optimizaciones fuera de alcance, tests debilitados, warnings nuevos, docs que afirmen validación inexistente).

5.3 Si algo no se cumplió o queda sin verificar, corrígelo en esta misma pasada y re-verifica (vuelve al paso 4).

---

## Paso 6 — Cerrar la tanda (commit + push obligatorios), actualizar el plan y reportar

6.1 Actualiza `docs/EXECUTION-PLAN.md` solo si el propio plan lo contempla (p. ej. cambiar el estado de la tanda, registrar hallazgos, añadir al historial). No inventes secciones ni estados que el plan no defina. Si el plan reserva la edición del fichero a otra persona/rol, respétalo.

6.2 **COMPROMETE Y PUSHEA los cambios antes de dar la tanda por terminada.** Es un paso obligatorio y no negociable: una tanda solo se considera cerrada cuando su SHA está disponible en el remoto, no cuando está únicamente en el working tree local (un trabajo local sin pushear no cuenta como hecho).

- Revisa que no quede nada sin commitear: `git status --short`.
- Comitea todo el trabajo de la tanda con un mensaje que resuma alcance, tests y docs (incluye el trailer de coautor exigido por el proyecto).
- Pushea a la rama correcta: `git push origin <rama>`.
- Verifica que el remoto recibió tu SHA y que el contenido está ahí: `git fetch origin` + `git rev-parse origin/<rama>` y confirma que apunta a tu commit, y opcionalmente inspecciona los archivos clave con `git show origin/<rama>:<ruta>`.
- Si no se puede pushear por permisos/red/dependencia, NO marques la tanda como cerrada: déjala en el estado del plan que corresponda (p. ej. `REVIEW_REQUIRED` o `CHANGES_REQUESTED`) y reporta explícitamente que el push queda pendiente.

6.3 Reporta al propietario, como mínimo:

- SHA final **ya pusheado en el remoto** (y su confirmación).
- Resumen de lo ejecutado.
- Resultado de build y tests.
- Número de tests descubiertos y pasados.
- Decisiones distintas de lo propuesto (si las hubo) y por qué.
- Resultado de la segunda pasada (paso 5).
- Cualquier hallazgo nuevo.
- Confirmación explícita de que `git commit` + `git push` se realizaron y de que `origin/<rama>` apunta al commit final.
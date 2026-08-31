---
name: gamehours-workflow
description: "Use for any GameHours implementation, bug fix, refactor, performance work, UI/UX change, persistence/schema change, CI/release work, or code review. Enforces the project's evidence-first workflow: read AGENTS.md and the current EXECUTION-PLAN.md, research before meaningful decisions, keep scope controlled, validate the exact SHA, perform a second-pass diff review, and never claim unverified behavior."
---

# GameHours engineering workflow

Esta skill es la **disciplina permanente de ingeniería** de GameHours para Command Code.

No contiene la tarea, rama, SHA ni estado actual del proyecto. Esos datos cambian y deben leerse siempre del repositorio. La skill define **cómo trabajar con rigor**; `docs/EXECUTION-PLAN.md` define **qué trabajo está autorizado ahora**.

## Fuentes de verdad

Antes de modificar código, lee siempre:

1. `AGENTS.md` — invariantes arquitectónicas y reglas permanentes del proyecto.
2. `docs/EXECUTION-PLAN.md` — tanda activa, estado, SHA base, alcance, restricciones, tests y criterios de aceptación.
3. Sólo cuando sea relevante:
   - `docs/REAL-MACHINE-VALIDATION.md` para validación funcional/Windows;
   - `docs/PRODUCT-ROADMAP.md` para contexto de producto, nunca como autorización automática para ampliar alcance.

No hardcodees en esta skill una rama, un SHA, una cifra de tests o un estado temporal.

Si el encargo actual, `AGENTS.md` y `EXECUTION-PLAN.md` parecen contradecirse, **no elijas silenciosamente una interpretación**. Identifica el conflicto antes de modificar código. No debilites una invariante de datos, seguridad, privacidad o fiabilidad para hacer pasar una tarea.

---

## 1. Proteger el estado real del repositorio

Antes de tocar archivos:

```bash
git rev-parse --show-toplevel
git status --short
git branch --show-current
git rev-parse HEAD
git remote -v
```

Después:

- identifica la rama de trabajo desde el plan vigente o el encargo actual;
- comprueba el upstream/remoto cuando sea relevante;
- si es seguro, actualiza referencias remotas con `git fetch` antes de asumir que conoces el estado actual;
- registra el SHA base que indique el plan; si no lo indica, registra el HEAD inicial para poder revisar el diff final;
- si hay cambios locales previos o archivos no relacionados, **no los descartes, resetees, sobrescribas, rebases ni escondas** sin una instrucción explícita;
- nunca uses `git reset --hard`, `git clean -fd`, force-push o una reescritura destructiva como solución rutinaria.

El objetivo es saber exactamente **desde qué código se parte** y no destruir trabajo ajeno.

---

## 2. Determinar el rol y el alcance antes de implementar

Lee de cero el `EXECUTION-PLAN.md` vigente. Extrae al menos:

- estado de la tanda;
- SHA base;
- tareas activas;
- archivos o capas previsiblemente afectadas;
- restricciones y cosas que explícitamente NO deben hacerse;
- implementación preferida, si existe;
- tests obligatorios;
- criterios de aceptación;
- validación manual pendiente;
- backlog/gates que siguen bloqueados.

Respeta el estado del plan:

- `READY_FOR_IMPLEMENTATION`: implementar únicamente la tanda autorizada.
- `IMPLEMENTING`: continuar sólo el alcance ya abierto.
- `CHANGES_REQUESTED`: corregir exclusivamente los hallazgos solicitados y las regresiones que esas correcciones causen.
- `REVIEW_REQUIRED`: revisar; no convertir la revisión en una nueva tanda de funcionalidades.
- `AUTOMATED_VERIFIED` / `MANUAL_VALIDATION_REQUIRED`: no añadir código salvo que la validación revele un defecto que deba documentarse y corregirse.
- `VERIFIED`: no reabrir la tanda sin una razón nueva.
- `BLOCKED`: no sortear el bloqueo inventando una validación equivalente.

Si no existe una tanda activa clara y el usuario no ha pedido explícitamente crearla, no inventes un roadmap propio.

---

## 3. Investigar antes de decidir

Para cualquier cambio técnico relevante —arquitectura, bug no trivial, rendimiento, persistencia, CI/release, seguridad o UI importante— no implementes la primera idea disponible.

### 3.1 Investigar primero en GameHours

Antes de añadir código:

1. busca si GameHours ya tiene una abstracción, servicio, componente, helper o patrón reutilizable;
2. estudia los callers y el lifecycle real, no sólo el método aislado;
3. revisa tests existentes porque suelen contener invariantes que no deben romperse;
4. comprueba si el framework/plataforma ya resuelve el problema sin dependencia nueva.

### 3.2 Investigar fuentes externas cuando aporten valor

Si la decisión se beneficia de conocimiento actualizado y tienes acceso a investigación externa:

1. prioriza documentación oficial de Microsoft/.NET/WPF/SQLite/GitHub/Velopack o del proveedor correspondiente;
2. después usa fuentes técnicas reputadas;
3. cuando sea útil, revisa repositorios open source maduros que resuelvan un problema comparable;
4. compara alternativas razonables y entiende sus trade-offs antes de elegir.

No copies patrones externos sin adaptarlos al contexto de GameHours.

Si no tienes acceso a investigación externa, no finjas haberla realizado: apóyate en código/documentación local y deja clara la limitación.

### 3.3 Criterio de decisión

Prefiere la solución que, manteniendo corrección, tenga:

- menos estados y ramas especiales;
- menos duplicación;
- menos dependencias;
- menos trabajo periódico o innecesario;
- responsabilidades más claras;
- mejor testabilidad;
- menor riesgo de regresión;
- mejor encaje con la arquitectura existente.

No reduzcas líneas sacrificando claridad.

---

## 4. Bugs: resolver la causa, no el síntoma

Ante un comportamiento incorrecto:

1. caracteriza o reproduce el fallo;
2. recoge evidencia en código, tests, logs, métricas o comportamiento real;
3. formula una hipótesis;
4. comprueba la hipótesis contra el lifecycle completo;
5. corrige en el punto que posee la responsabilidad real;
6. añade o ajusta un test que falle por la causa original;
7. revisa regresiones plausibles.

Si los datos contradicen la hipótesis inicial, descártala.

No acumules filtros/read-side workarounds cuando la corrupción puede evitarse correctamente en escritura; tampoco elimines defensas de lectura útiles sólo porque la escritura ya sea más estricta.

---

## 5. Rendimiento: medir y eliminar trabajo antes de microoptimizar

Para cambios de rendimiento:

1. define la métrica que importa;
2. mide o usa evidencia ya registrada;
3. localiza el coste;
4. elimina primero trabajo innecesario;
5. optimiza la causa;
6. vuelve a medir en condiciones comparables.

Prioriza especialmente:

- startup y primer input;
- trabajo en el hilo UI;
- timers/polling/wake-ups;
- I/O de disco/red;
- consultas y materialización repetida;
- memoria retenida;
- asignaciones repetitivas;
- sincronización innecesaria.

No uses `GC.Collect()`, working-set trimming, cambios de GC, NativeAOT/trimming u otros knobs agresivos para maquillar métricas salvo que el plan vigente autorice un experimento basado en evidencia.

---

## 6. Implementar con alcance controlado

Antes de editar, relaciona cada cambio previsto con un criterio de aceptación del plan.

Durante la implementación:

- realiza el menor cambio que resuelva correctamente la causa;
- evita refactors grandes no necesarios;
- no mezcles funcionalidades nuevas;
- no introduzcas dependencias sin ventaja clara y documentada;
- evita métodos gigantes, efectos secundarios ocultos, valores mágicos y condiciones especiales acumuladas;
- conserva las invariantes de `AGENTS.md`;
- no debilites el fallback periódico del monitor de procesos;
- no mezcles tiempo medido con tiempo reconstruido;
- no introduzcas doble contabilización;
- no conviertas helpers/launchers en juego por defecto;
- no comprometas el principio local-first ni amplíes la información sensible persistida/sincronizada sin autorización explícita.

### UI/UX

Si la tanda afecta interfaz:

- reutiliza componentes y patrones existentes antes de crear otros;
- cuida jerarquía, espaciado, estados hover/focus, carga, error y vacío;
- evita bloquear el dispatcher con trabajo evitable;
- mantén la UI coherente con GameHours, no como una herramienta interna genérica;
- las animaciones o efectos deben aportar claridad, no ruido.

---

## 7. Validar por capas

No consideres una tarea terminada porque compile una parte.

### 7.1 Validación focalizada

Ejecuta primero los tests directamente relacionados con el cambio cuando existan. Esto da feedback rápido y ayuda a aislar fallos.

### 7.2 Build y suite exigidos por el plan

Después ejecuta exactamente los comandos del `EXECUTION-PLAN.md`.

Si el plan no especifica otros, la base razonable es:

```bash
dotnet restore GameHours.sln
dotnet build GameHours.sln -c Release --no-restore
dotnet test GameHours.sln -c Release --no-build
```

Reglas:

- no continúes ocultando un build rojo;
- no elimines, ignores ni debilites tests válidos para obtener verde;
- cualquier descenso inesperado del número de tests debe explicarse;
- si un test está equivocado, debe existir una razón técnica demostrable para modificarlo;
- diferencia explícitamente entre **implementado**, **compilado**, **tests verdes**, **CI verde** y **verificado funcionalmente**.

### 7.3 Plataforma real

No afirmes que un comportamiento WPF/Windows está verificado porque una parte portable pasó en Linux/macOS.

Procesos, WMI, suspensión/reanudación, input, filesystem watchers, tray, WPF y empaquetado instalado requieren Windows real o CI Windows según lo que se esté validando. Si falta prueba manual, deja el estado como pendiente.

### 7.4 CI

Cuando la tanda requiera revisión remota:

- comprueba CI para el **SHA exacto** pusheado;
- distingue fallos de runner/infraestructura de fallos reales de Restore/Build/Test/Package;
- no atribuyas un run verde anterior al HEAD actual;
- no marques como automatizadamente verificado un SHA cuya CI no terminó verde.

---

## 8. Segunda pasada obligatoria antes de entregar

Después de implementar y antes de declarar la tanda lista, realiza una revisión separada del trabajo ya hecho.

Como mínimo:

```bash
git status --short
git diff --stat <SHA_BASE>..HEAD
git diff <SHA_BASE>..HEAD
```

Revisa **todos** los archivos modificados y contrasta el diff punto por punto con el plan vigente.

Busca activamente:

- código fuera de alcance;
- duplicación;
- código muerto;
- imports/usings innecesarios;
- logs/debug temporales;
- comentarios o documentación obsoletos;
- `catch` que oculten errores;
- cambios de esquema innecesarios;
- trabajo periódico nuevo;
- bloqueos del hilo principal;
- nuevas rutas/usuarios/tokens/secretos o datos personales;
- tests debilitados;
- afirmaciones de validación que no estén respaldadas por evidencia;
- complejidad que pueda eliminarse sin sacrificar claridad.

Después relee los criterios de aceptación y clasifica cada uno como:

- demostrado;
- pendiente de CI;
- pendiente de prueba manual;
- incumplido.

Si algo está incumplido y pertenece al alcance, corrígelo y repite la validación.

---

## 9. Actualizar el plan sin apropiarse de la revisión

`docs/EXECUTION-PLAN.md` es el contrato de handoff.

Como implementador:

- puedes añadir una nota factual de implementación si el plan lo permite;
- puedes dejar la tanda en `REVIEW_REQUIRED` cuando el trabajo esté listo para revisión;
- registra limitaciones reales y validaciones que no pudiste ejecutar;
- no cambies criterios de aceptación para adaptarlos a tu solución;
- no marques unilateralmente `AUTOMATED_VERIFIED`, `MANUAL_VALIDATION_REQUIRED` o `VERIFIED` si el protocolo reserva esas decisiones al revisor;
- no borres hallazgos previos para que el plan parezca limpio.

La revisión posterior debe poder reconstruir qué ocurrió sólo leyendo el plan, el diff y CI.

---

## 10. Commit, push y handoff

Cuando el plan o el encargo requiera revisión remota:

1. confirma que sólo se incluyen archivos del alcance;
2. crea un commit claro y específico siguiendo las convenciones reales del repositorio;
3. **no inventes trailers, coautores ni firmas** que el proyecto no exija;
4. pushea a la rama determinada por el plan/upstream;
5. nunca uses force-push salvo autorización explícita y justificada;
6. ejecuta `git fetch` y confirma que `origin/<rama>` contiene el SHA entregado;
7. comprueba el CI de ese SHA cuando sea posible.

El informe final debe contener, como mínimo:

- SHA final remoto;
- resumen preciso de lo modificado;
- archivos/capas principales afectadas;
- build ejecutado y resultado;
- tests ejecutados, descubiertos y pasados;
- estado de CI del SHA exacto;
- validación manual realizada o pendiente;
- decisiones distintas de la propuesta del plan y su justificación;
- hallazgos nuevos;
- cualquier parte que **no haya podido verificarse**.

No digas “funciona”, “terminado” o “verificado” si la evidencia sólo demuestra una parte.

---

## 11. Condiciones para detenerse y reportar

Detén la implementación y reporta en vez de improvisar cuando:

- las instrucciones se contradicen materialmente;
- el working tree contiene cambios ajenos que no pueden aislarse con seguridad;
- la solución requiere ampliar mucho el alcance autorizado;
- aparece una posible pérdida/corrupción de datos;
- aparece un secreto o credencial;
- una migración destructiva parece necesaria;
- un test válido exige romper una invariante del proyecto;
- falta una dependencia externa/permiso/servicio imprescindible;
- no existe forma honesta de validar un comportamiento crítico en el entorno disponible.

La evidencia manda. La prioridad no es terminar rápido: es dejar GameHours más correcto, sencillo, eficiente, mantenible y comprobable que antes.
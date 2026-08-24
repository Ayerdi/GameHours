# GameHours — roadmap de descubrimiento y protección de partidas guardadas

**Estado:** exploratorio / no autorizado para implementación todavía.

Este documento captura una dirección de producto y un plan de investigación. **No abre una tanda de implementación** y no sustituye a `docs/EXECUTION-PLAN.md`, que sigue siendo la fuente canónica del trabajo autorizado.

## 1. Visión

GameHours ya conoce el ciclo real de una partida: qué juego se está ejecutando, cuándo empieza y termina una sesión y qué identidad local tiene ese juego. Esa información puede convertirse en una ventaja útil para proteger partidas guardadas sin transformar GameHours en un launcher ni en un monitor general del disco.

La dirección propuesta es:

> GameHours descubre de forma local y explicable dónde guarda datos cada juego, muestra esa información al usuario y, de forma opcional, puede proteger esos datos al finalizar una sesión.

La feature debe mantener las mismas propiedades que el tracking actual:

- local-first;
- evidencia antes que suposiciones;
- procedencia y confianza visibles;
- sin trabajo global periódico innecesario;
- sin depender obligatoriamente de otra aplicación;
- sin hacer escrituras destructivas durante la fase de descubrimiento;
- restauración siempre conservadora y explícita.

## 2. Decisión arquitectónica principal

**Descubrimiento y backup son responsabilidades distintas.**

GameHours no debería necesitar que Ludusavi esté instalado para saber dónde están las partidas guardadas. El descubrimiento debe pertenecer a GameHours y poder combinar distintas fuentes de evidencia.

Ludusavi puede ser posteriormente un backend opcional para ejecutar backups/restores avanzados, porque ya resuelve retención, formatos de backup, Registry, Proton, cloud y numerosos casos límite.

Dirección conceptual:

```text
                 fuentes conocidas
                 /      |       \
                v       v        v
        Ludusavi     stores    convenciones
        Manifest               de motores
                \       |        /
                 \      |       /
                  v     v      v
             Save location discovery
                      |
                      v
               ubicaciones + evidencia
               + fuente + confianza
                      |
               ┌──────┴──────┐
               v             v
          UI / preview    backup backend
                         /             \
                        v               v
                 GameHours simple   Ludusavi opcional
```

Las interfaces concretas son deliberadamente una decisión posterior. Si se implementa, una frontera tipo `ISaveLocationProvider` puede ser razonable, pero este roadmap **no obliga** a introducir esa abstracción si una composición más simple resulta suficiente durante los experimentos.

## 3. Fuente primaria: Ludusavi Manifest

Ludusavi usa `mtkennerly/ludusavi-manifest` para describir qué datos pertenecen a cada juego. Ludusavi declara soporte para más de 19.000 juegos y el manifest se alimenta principalmente de PCGamingWiki.

El propio proyecto del manifest declara que su formato pretende ser genérico para que pueda ser implementado por otras herramientas de backup. El repositorio está publicado bajo licencia MIT.

El manifest puede describir:

- rutas de archivos y directorios;
- datos del Registro de Windows;
- identificadores de Steam;
- directorios de instalación;
- restricciones por sistema operativo/store;
- tags como `save` y `config`;
- globs;
- placeholders como `<winAppData>`, `<winLocalAppData>`, `<winLocalAppDataLow>`, `<winDocuments>`, `<root>`, `<base>`, `<storeGameId>` o `<storeUserId>`.

También contempla manifests secundarios `.ludusavi.yaml` incluidos directamente dentro del directorio de un juego.

### Política propuesta para GameHours

- El manifest se considera **evidencia conocida**, no código de Ludusavi embebido.
- El parser y la expansión de paths serían implementación propia de GameHours.
- Debe existir caché local para que el descubrimiento continúe sin Internet.
- Una actualización online del manifest debe ser opcional, pequeña y condicional; upstream recomienda `ETag` / `If-None-Match`.
- No se debe consultar la red durante una sesión para poder detectar o proteger saves ya conocidos.
- Antes de redistribuir una copia del dataset dentro de instaladores públicos, revisar explícitamente obligaciones de licencia/atribución del manifest y de sus fuentes de datos. No asumir que una decisión de distribución está resuelta sólo porque el código/repositorio del manifest use MIT.

Referencias:

- https://github.com/mtkennerly/ludusavi
- https://github.com/mtkennerly/ludusavi-manifest
- https://github.com/mtkennerly/ludusavi-manifest/blob/master/data/manifest.yaml
- https://www.pcgamingwiki.com/

## 4. Fuentes complementarias de descubrimiento

El manifest debe ser la señal de mayor valor, pero no la única. GameHours puede cubrir juegos ausentes o configuraciones no documentadas combinando otras evidencias.

### 4.1 Metadata de stores ya conocida

Reutilizar lo que GameHours ya sabe de Steam/Epic/GOG:

- identificador de store;
- instalación exacta;
- directorio del juego;
- ejecutable aprendido;
- identidad del juego.

Esto permite resolver placeholders del manifest y reducir ambigüedad sin un nuevo escaneo global.

### 4.2 Convenciones de motores

Investigar de forma separada y con documentación oficial las convenciones de persistencia de motores que podamos identificar de forma fiable, inicialmente:

- Unity (`Application.persistentDataPath`, normalmente LocalLow en Windows);
- Unreal Engine (`Saved` / `SaveGames` y variantes del proyecto);
- Godot (`user://` y directorios de datos de usuario).

Estas reglas nunca deben elevarse a autoridad por sí solas. Sirven para producir **candidatos** cuando el manifest no tiene información o para corroborar una ruta ya conocida.

La investigación debe comprobar cómo obtener de forma fiable nombres de producto/empresa/proyecto; no construir rutas a partir del título visible del juego si esa relación no está demostrada.

### 4.3 Convenciones de Windows

Explorar sólo raíces plausibles y acotadas:

- `%APPDATA%`;
- `%LOCALAPPDATA%`;
- `%USERPROFILE%\AppData\LocalLow`;
- `Documents`;
- `Saved Games`;
- directorio de instalación del juego;
- ubicaciones específicas de stores cuando exista evidencia.

**No hacer un rastreo recursivo periódico de todo el perfil/disco.**

### 4.4 Aprendizaje a partir de la sesión

GameHours tiene una señal que una base estática no tiene: sabe exactamente cuándo se juega.

Experimento futuro posible:

1. resolver un conjunto pequeño de carpetas candidatas antes/durante la sesión;
2. capturar metadata barata (`existencia`, `mtime`, `tamaño`, nombres) sin leer contenido innecesariamente;
3. comparar al terminar la sesión;
4. aumentar confianza si archivos razonables se crean/modifican durante el intervalo;
5. conservar la evidencia y pedir confirmación cuando siga existiendo ambigüedad.

No convertir esto en vigilancia del filesystem completo. Si se usan watchers, deben estar limitados a directorios candidatos concretos y existir sólo cuando aporten valor. Antes de usar watchers, evaluar si una comparación puntual al inicio/fin elimina suficiente trabajo.

ETW, USN Journal, enumeración de handles de procesos u otras técnicas de trazado de I/O quedan **fuera de la primera aproximación**. Sólo se estudiarían si manifest + store + motor + cambios en roots acotados muestran una limitación real.

## 5. Modelo de evidencia y confianza

El descubrimiento debe poder explicar por qué considera una ruta válida.

Ejemplo conceptual:

```text
Ubicación: %APPDATA%\ExampleGame\saves
Tipo: files
Tags: save

Evidencia:
✓ Ludusavi Manifest
✓ Steam AppID coincide
✓ archivos presentes
✓ modificados durante una sesión reciente

Confianza: alta
```

Otro caso:

```text
Ubicación: %LOCALAPPDATA%\Example\Saved\SaveGames

Evidencia:
✓ layout compatible con Unreal
✓ archivos .sav modificados durante la sesión
? sin entrada conocida en manifest

Confianza: media
Acción: pedir confirmación antes de automatizar backups
```

La confianza debe ser una política explícita y testeable, no una colección de `if` dispersos.

## 6. Fases experimentales

### Fase 0 — spike de datos, sólo lectura

Objetivo: comprobar si el manifest nos da cobertura suficiente con complejidad razonable.

- tomar una versión fija del manifest para tests;
- resolver por Steam ID cuando exista;
- resolver por nombre/alias sólo con reglas conservadoras;
- expandir placeholders Windows necesarios;
- aplicar restricciones `os` / `store`;
- distinguir `save` de `config`;
- soportar archivos/directorios/globs de manera acotada;
- medir cobertura sobre una muestra real de juegos disponibles.

**No persistencia nueva, no backup, no restore, no watchers.**

Gate para seguir: cobertura útil y una tasa de falsos positivos suficientemente baja para justificar integrar el manifest.

### Fase 1 — discovery visible y explicable

Añadir una experiencia de sólo lectura en la ficha del juego:

```text
PARTIDAS GUARDADAS

Ubicación
%APPDATA%\ExampleGame

Fuente
PCGamingWiki / Ludusavi Manifest

Estado
✓ Encontrada

Última modificación
Hoy · 18:42

[Abrir carpeta]
```

Requisitos:

- nunca llamar “save” a una ruta heurística sin mostrar su incertidumbre;
- si no existe información, decirlo claramente;
- permitir al usuario añadir/corregir una ubicación local;
- las decisiones manuales deben tener prioridad sobre heurísticas futuras.

### Fase 2 — heurísticas de arquitectura

Sólo después de medir Fase 0/1:

- probar Unity, Unreal y Godot con juegos reales conocidos;
- generar candidatos sin automatizar backup;
- comparar contra manifest/PCGamingWiki cuando exista ground truth;
- registrar falsos positivos/falsos negativos;
- mantener toda detección acotada a roots plausibles.

Gate para seguir: las heurísticas deben aportar cobertura real fuera del manifest. Si apenas mejoran el resultado, se eliminan en vez de mantener complejidad sin valor.

### Fase 3 — aprendizaje por cambios durante la sesión

Experimento opt-in / diagnóstico inicialmente:

- snapshot barato de candidatos al comienzo;
- snapshot al terminar;
- detectar creación/modificación relevante;
- no leer archivos salvo que sea necesario;
- no monitorizar todo el sistema;
- medir coste de CPU/I/O y número de falsos candidatos.

El objetivo es aprender ubicaciones desconocidas, no registrar la actividad de archivos del usuario.

### Fase 4 — preview de protección

Antes de hacer backups automáticos:

- mostrar exactamente qué archivos/keys se protegerían;
- tamaño total;
- fuente de cada ubicación;
- conflictos/paths ausentes;
- opción manual de excluir `config` y proteger sólo `save` cuando el manifest lo permita.

No escribir todavía sobre datos live.

### Fase 5 — backend de backup

Evaluar dos niveles independientes:

**A. GameHours local simple**

Sólo si la implementación puede mantenerse pequeña y segura:

- snapshot por juego;
- versionado/retención básica;
- checksums/validación;
- nunca sobrescribir la única copia buena;
- restore con safety backup previo.

**B. Adaptador opcional de Ludusavi**

Para usuarios que quieran capacidades avanzadas. Ludusavi dispone de CLI y salida JSON (`--api`) y permite operar sobre juegos concretos.

GameHours podría:

1. detectar si `ludusavi` está disponible;
2. hacer un preview no destructivo;
3. mapear la identidad del juego de forma explícita;
4. invocar backup sólo para ese juego;
5. parsear el resultado JSON;
6. mostrar éxito, warnings y archivos procesados.

No bundlear ni exigir Ludusavi por defecto. La ausencia de Ludusavi no debe degradar el tracking ni el discovery de GameHours.

### Fase 6 — backup automático al terminar sesión

Sólo después de verificar manualmente backups/restores.

Evento natural:

```text
SessionCompleted
      |
      v
¿backup automático activado para este juego?
      |
      v
¿hay saves conocidos/cambiados?
      |
      v
encolar trabajo de backup fuera del hilo UI
      |
      v
resultado visible, sin bloquear tracking
```

Reglas mínimas:

- la sesión debe estar finalizada/persistida antes de iniciar el backup;
- no bloquear el dispatcher WPF;
- no iniciar backup por un helper/launcher;
- con varios juegos simultáneos, sólo actuar sobre el juego cuya sesión realmente terminó;
- evitar duplicados inútiles cuando no cambió ningún save si el backend puede demostrarlo de forma barata;
- fallar el backup nunca puede perder/corromper datos live ni invalidar la sesión de GameHours.

### Fase 7 — restore seguro

Restore es más peligroso que backup y debe llegar después.

- preview obligatorio;
- advertir de juego abierto;
- no restaurar mientras el juego objetivo esté activo;
- crear safety backup del estado live antes de sustituirlo;
- mostrar archivos/keys afectados;
- validar el resultado cuando sea posible;
- recovery claro si una operación parcial falla.

## 7. Casos de prueba obligatorios

La feature no se valida sólo con fixtures sintéticos.

### Automatizados

- parseo de manifest con fixture versionado y sin red;
- placeholders Windows;
- constraints por store/OS;
- tags `save` vs `config`;
- globs y directorios;
- paths relativos/rechazados y recursion/aliases acotados;
- matching por Steam ID;
- alias/nombre ambiguo no adivinado;
- cache offline;
- actualización condicional del manifest con HTTP simulado;
- decisiones manuales ganan a heurísticas;
- política de confianza;
- backup no se dispara para sesión no finalizada;
- multi-game: terminar A no respalda B;
- errores de backend no cambian tiempo/sesiones.

### Windows real

Construir una matriz pequeña pero diversa:

- juego Steam con manifest conocido;
- juego loose/no launcher;
- un juego Unity;
- un juego Unreal;
- un juego con saves en AppData/Documents;
- si es posible, un juego con Registry o store package;
- juego sin entrada conocida para probar heurística/manual.

En cada uno comprobar:

1. ruta propuesta;
2. procedencia/confianza;
3. existencia real del save;
4. si cambia durante una sesión;
5. preview correcto;
6. backup;
7. modificación deliberada de una copia de prueba;
8. restore sobre datos desechables;
9. safety backup y recuperación.

Nunca usar la única partida importante del usuario como fixture destructivo.

## 8. Rendimiento y privacidad

Presupuesto de diseño:

- cero escaneo global continuo;
- cero red necesaria durante gameplay;
- cero contenido de saves enviado a GameHours Sync;
- nombres/rutas completas siguen siendo datos locales;
- manifest cacheado y actualizado sólo cuando corresponda;
- snapshots de filesystem sólo sobre roots candidatos y sólo cuando sean necesarios;
- cualquier watcher debe tener lifecycle explícito y ser eliminado al dejar de ser útil;
- medir I/O, CPU, memoria y número de paths visitados antes/después de activar heurísticas.

Si una heurística requiere recorrer miles de directorios por sesión, se considera una señal de mal diseño y debe replantearse.

## 9. UX objetivo

La feature debe sentirse integrada en GameHours, no como una herramienta de backup genérica.

Ficha del juego, dirección conceptual:

```text
PARTIDAS GUARDADAS

✓ Ubicación encontrada
  %APPDATA%\ExampleGame\Saves
  Fuente: manifest · confianza alta

Último cambio       Hoy · 18:42
Último backup       Hoy · 18:43
Protección          Automática al cerrar

[Abrir carpeta]   [Crear backup]
```

Estados importantes:

- no encontrado;
- encontrado por fuente conocida;
- candidato heurístico pendiente de confirmar;
- ubicación manual;
- backup en curso;
- backup correcto;
- warning parcial;
- restore bloqueado porque el juego está abierto;
- conflicto o error recuperable.

La automatización debe ser opt-in por juego o mediante una preferencia global claramente reversible.

## 10. Qué no hacer

- No convertir GameHours en un clon de Ludusavi.
- No copiar el código Rust de Ludusavi.
- No hacer scraping runtime de PCGamingWiki para cada juego.
- No asumir que el título del juego determina una carpeta.
- No recorrer todo `%APPDATA%`/Documents cada segundo o cada sesión.
- No usar ETW/USN/handles como primera solución.
- No hacer restore automático.
- No subir saves, Registry, usernames o paths a backend/sync por defecto.
- No mezclar la integridad del backup con la autoridad temporal de las sesiones.
- No introducir esta feature dentro de la tanda actual de cierre de foundation.

## 11. Preguntas que los experimentos deben responder

1. ¿Qué porcentaje de nuestra biblioteca real resuelve el manifest sin heurísticas?
2. ¿Cuál es el coste real de parsear/indexar el manifest y cuál es la mejor estrategia de caché?
3. ¿Steam ID + aliases del manifest bastan para matching fiable o necesitamos otra identidad?
4. ¿Cuánto añaden realmente Unity/Unreal/Godot sobre la cobertura del manifest?
5. ¿Podemos aprender saves desconocidos mediante snapshots acotados de inicio/fin sin watchers permanentes?
6. ¿Qué metadata mínima necesitamos guardar para recordar una ubicación aprendida sin crear problemas de portabilidad?
7. ¿Merece la pena un backup simple propio o el coste de restore/retención hace preferible que GameHours delegue esa capa avanzada?
8. ¿Qué UX hace visible la confianza sin abrumar al usuario?
9. ¿Cómo tratamos juegos con varios perfiles, slots, mods o instalaciones distintas?
10. ¿Cómo distinguimos un save real de cachés/config/logs que cambian durante una sesión?

## 12. Criterio de éxito

Esta línea de producto sólo debe avanzar si demuestra simultáneamente:

- alta utilidad práctica;
- baja tasa de falsos positivos;
- coste de runtime insignificante fuera de operaciones explícitas;
- funcionamiento offline con datos cacheados;
- explicación clara de procedencia/confianza;
- seguridad fuerte de backup/restore;
- arquitectura desacoplada del tracking y de cualquier backend externo.

Si podemos conseguirlo, GameHours gana una capacidad muy coherente con su propósito: no sólo recordar **cuándo** jugaste, sino ayudar a proteger **el progreso producido durante esas sesiones**.

# Sources

Procedencia de las dependencias externas. Manten este fichero al dia: si
anades una dependencia, registra aqui su fuente oficial y su hash verificado.

| Dependencia | Version | Fuente oficial | SHA-256 | Verificado |
|-------------|---------|----------------|---------|------------|
| <nombre> | <version> | <url oficial> | <hash> | <fecha> |

Reglas:

1. Solo fuentes oficiales o de confianza demostrable.
2. Todo binario descargado en runtime se pinnea por hash y se verifica antes
   de ejecutarlo.
3. Si una fuente actualiza la dependencia, actualiza el hash y la fecha — no
   elimines la comprobacion.

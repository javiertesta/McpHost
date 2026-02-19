# apply_patch (Codex/Claude) - Referencia rapida

Este archivo documenta el formato aceptado por la herramienta `apply_patch` (parches estilo *unified diff* con un "mini-DSL" estricto).

## Concepto
- `apply_patch` recibe **un unico string** (no JSON).
- El contenido **debe** obedecer la gramatica indicada por la herramienta (cabecera/cola + uno o mas "hunks").
- No mezclar esta llamada con otras herramientas en paralelo: ejecutar `apply_patch` sola.

## Estructura obligatoria
Todo patch empieza y termina asi:

```text
*** Begin Patch
... uno o mas hunks ...
*** End Patch
```

Si falta alguna de esas lineas, el patch falla.

## Tipos de hunk (operaciones soportadas)

### 1) Agregar archivo
Forma:

```text
*** Add File: path/archivo.ext
+linea 1
+linea 2
...
```

Notas:
- Cada linea del nuevo archivo debe comenzar con `+`.
- El path es relativo al repo/cwd (o el path que corresponda en el workspace).

### 2) Borrar archivo
Forma:

```text
*** Delete File: path/archivo.ext
```

### 3) Actualizar archivo (editar contenido)
Forma:

```text
*** Update File: path/archivo.ext
@@
 linea de contexto (sin prefijo + ni -)
-linea a eliminar
+linea a agregar
 otra linea de contexto
```

Reglas de lineas:
- ` ` (espacio al inicio): contexto (debe matchear el contenido actual).
- `-`: borrar esa linea (debe existir tal cual).
- `+`: agregar esa linea.
- `@@` o `@@ <texto>`: inicio de bloque/hunk; el texto es opcional (sirve como ancla legible).

### 4) Mover/renombrar archivo
Forma (dentro de un `*** Update File: ...`):

```text
*** Update File: viejo/path.ext
*** Move to: nuevo/path.ext
@@
... (opcional) cambios de contenido ...
```

Notas:
- El move puede ir con o sin cambios de contenido.

## Ejemplos minimalistas

### Agregar un README simple
```text
*** Begin Patch
*** Add File: README.md
+# Proyecto
+Texto.
*** End Patch
```

### Cambiar una sola linea (con contexto)
```text
*** Begin Patch
*** Update File: src/app.txt
@@
-port=3000
+port=8080
*** End Patch
```

## Errores comunes (y como evitarlos)
- Olvidar `*** Begin Patch` / `*** End Patch`.
- Usar tabs o no poner el prefijo correcto (`+`, `-`, o ` `) en lineas dentro de un `*** Add File` o `*** Update File`.
- Contexto que no coincide con el archivo real (la herramienta aplica por matching; si el archivo difiere, falla o no aplica donde esperas).
- Intentar "parchear" binarios: preferir no hacerlo (esta herramienta esta orientada a texto).

## Aclaracion importante: operaciones de archivos permitidas
Esta permitido crear, copiar, mover/renombrar y eliminar archivos **siempre que el objetivo sea preservar exactamente los bytes del contenido** (y por ende el encoding) cuando corresponda.

Lineas guia para el agente:
- Al copiar/mover: hacerlo como operacion de filesystem (sin reescritura del contenido), para no introducir conversiones de encoding o normalizacion de saltos de linea.
- Al editar: mantener el encoding existente del archivo (no re-encodear UTF-16 <-> UTF-8, no cambiar CRLF/LF salvo que sea intencional y acordado).

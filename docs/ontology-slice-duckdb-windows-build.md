# How to build WeKnora on Windows with DuckDB dynamic linking

This guide fixes the Windows `go build ./...` failure caused by DuckDB's prebuilt static library and the local MSYS2 UCRT toolchain using incompatible C++ runtime symbols.

Use this when a Windows build fails with unresolved symbols similar to:

```text
undefined reference to `__emutls_v._ZSt11__once_call'
undefined reference to `__emutls_v._ZSt15__once_callable'
```

## Prerequisites

- Go installed and available on `PATH`.
- MSYS2 UCRT64 GCC installed at `C:/msys64/ucrt64/bin/gcc.exe`.
- DuckDB shared library version `v1.5.2`, which matches `github.com/duckdb/duckdb-go-bindings v0.10502.0` used by this project.

## Step 1: Download the DuckDB shared library

Run this from Git Bash or another Unix-like shell on Windows:

```bash
mkdir -p /e/tmp/duckdb-v1.5.2
curl -L --fail \
  -o /e/tmp/duckdb-v1.5.2/libduckdb-windows-amd64.zip \
  https://github.com/duckdb/duckdb/releases/download/v1.5.2/libduckdb-windows-amd64.zip
unzip -o /e/tmp/duckdb-v1.5.2/libduckdb-windows-amd64.zip -d /e/tmp/duckdb-v1.5.2
```

Expected files:

```text
/e/tmp/duckdb-v1.5.2/duckdb.dll
/e/tmp/duckdb-v1.5.2/duckdb.lib
/e/tmp/duckdb-v1.5.2/duckdb.h
/e/tmp/duckdb-v1.5.2/duckdb.hpp
```

## Step 2: Build with `duckdb_use_lib`

`duckdb_use_lib` tells `duckdb-go` to link against a shared DuckDB library instead of the bundled static archive.

```bash
PATH="/e/tmp/duckdb-v1.5.2:/c/msys64/ucrt64/bin:$PATH" \
CGO_LDFLAGS="-lduckdb -LE:/tmp/duckdb-v1.5.2" \
CC=C:/msys64/ucrt64/bin/gcc.exe \
CXX=C:/msys64/ucrt64/bin/g++.exe \
go build -tags=duckdb_use_lib ./...
```

## Step 3: Run tests with the same build tag

Use the same dynamic library path and build tag for the full Go test suite:

```bash
PATH="/e/tmp/duckdb-v1.5.2:/c/msys64/ucrt64/bin:$PATH" \
CGO_LDFLAGS="-lduckdb -LE:/tmp/duckdb-v1.5.2" \
CC=C:/msys64/ucrt64/bin/gcc.exe \
CXX=C:/msys64/ucrt64/bin/g++.exe \
go test -tags=duckdb_use_lib ./...
```

## Verification

A successful run exits with code `0`.

Observed local verification on Windows:

```text
go test -tags=duckdb_use_lib ./...  # 2641 tests passed in 80 packages
go build -tags=duckdb_use_lib ./... # exit code 0
```

## Troubleshooting

### `cannot find -lduckdb`

The linker cannot find `duckdb.lib`.

Check that `CGO_LDFLAGS` points at the directory containing `duckdb.lib`:

```bash
CGO_LDFLAGS="-lduckdb -LE:/tmp/duckdb-v1.5.2"
```

### Runtime cannot find `duckdb.dll`

The executable can link but cannot start because Windows cannot locate `duckdb.dll`.

Put the DuckDB directory first on `PATH` before running the binary or tests:

```bash
PATH="/e/tmp/duckdb-v1.5.2:$PATH"
```

### Static build still fails with `__emutls_v` symbols

That means the command is still using DuckDB's bundled Windows static archive. Confirm that `-tags=duckdb_use_lib` is present.

Do not mix `duckdb_use_lib` with `duckdb_use_static_lib`.

## Why this is needed

The project's `duckdb-go` dependency uses `github.com/duckdb/duckdb-go-bindings v0.10502.0`, which maps to DuckDB `v1.5.2`. On Windows, the default build path links against prebuilt static archives from the Go module cache.

With MSYS2 UCRT GCC 16.1.0, those static archives can reference C++ runtime thread-local symbols not provided in the expected form by the local toolchain:

```text
__emutls_v._ZSt11__once_call
__emutls_v._ZSt15__once_callable
```

Dynamic linking avoids that static archive/toolchain ABI mismatch by using the official DuckDB shared library instead.

## Related

- [Ontology Slice Core MVP design](superpowers/specs/2026-05-20-ontology-slice-core-mvp-design.md)
- [DuckDB v1.5.2 release](https://github.com/duckdb/duckdb/releases/tag/v1.5.2)
- [DuckDB Windows AMD64 shared library ZIP](https://github.com/duckdb/duckdb/releases/download/v1.5.2/libduckdb-windows-amd64.zip)

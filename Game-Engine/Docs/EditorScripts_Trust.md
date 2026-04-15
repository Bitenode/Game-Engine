# EditorScripts: trusted assemblies (SHA-256)

Hot-loaded editor extensions live under your project’s `Builds/EditorScripts/` folder (see [06 — Scripting and Extensibility](06_Scripting_And_Extensibility.md)). Optional `editor-extensions.json` can list **`trustedAssemblies`**: each entry `{ "file": "MyPack.dll", "sha256": "<hex>" }` is checked when that DLL is part of the resolved load set. If a listed file’s hash does not match, **no** hot assemblies from that manifest run (the editor falls back to AppDomain-only extensions).

## When to use this

- You ship a **prebuilt** `EditorScripts_*.dll` or companion DLL to teammates or CI and want a simple tamper check.
- You document an expected hash next to a release artifact.

This is **not** a sandbox: matching hashes still execute full-trust .NET code in the editor process.

## Computing SHA-256

**PowerShell (Windows):**

```powershell
Get-FileHash -Algorithm SHA256 -Path ".\Builds\EditorScripts\MyPack.dll" | Select-Object -ExpandProperty Hash
```

**macOS / Linux:**

```bash
shasum -a 256 "Builds/EditorScripts/MyPack.dll"
```

**Windows CMD (legacy):**

```bat
certutil -hashfile "Builds\EditorScripts\MyPack.dll" SHA256
```

Normalize the digest to **64 hex characters** for the JSON field (the manifest accepts optional spaces; the engine normalizes).

## Manifest snippet

```json
{
  "schemaVersion": 1,
  "assemblies": ["EditorScripts_20260101120000.dll", "MyPack.dll"],
  "trustedAssemblies": [
    { "file": "MyPack.dll", "sha256": "A1B2C3..." }
  ]
}
```

Only files that appear in the **resolved** load list are verified. Missing files listed only under `trustedAssemblies` but not under `assemblies` / `EditorScripts_*.dll` resolution may be ignored for hashing (see `EditorExtensionsManifest.ResolveDllPaths` in the engine).

## CI example

1. Build or copy the DLL into `Builds/EditorScripts/` in your pipeline workspace.
2. Compute SHA-256 and write `editor-extensions.json` (or patch the `sha256` field) before packaging the project template or artifact.
3. Fail the job if the hash of the produced DLL does not match the committed manifest (release discipline).

## Editor UI

**Window → New Extensions Status Tab** summarizes the last load, including manifest metadata and each `trustedAssemblies` line with whether the file was in the resolved load set (hashes are enforced during load, not re-displayed as separate pass/fail rows).

Add-on template — editor extension workflow
==========================================

1. Put your EditorExtension classes in the project's Assets/ or Packages/ folder (see Docs/06_Scripting_And_Extensibility.md).

2. Compile scripts from the Script Editor (Compile or Ctrl+B). The engine writes:
   Builds/EditorScripts/EditorScripts_<timestamp>.dll

3. By default, the editor loads EVERY EditorScripts_*.dll in that folder (sorted by filename).
   Copy "editor-extensions.json.example" to:
   Builds/EditorScripts/editor-extensions.json
   and edit the "assemblies" list if you need a fixed load order or extra prebuilt DLLs beside Roslyn output.

4. Optional: copy additional add-on DLLs into Builds/EditorScripts/ and list them in editor-extensions.json.

5. "minEngineVersion" blocks loading if the editor is older than that version (see ProjectService.EngineVersion).

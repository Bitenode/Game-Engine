Starter Character
=================

A textured, skinned learning character with baked skeletal clips.
Drop this folder into a project (or enable Standard Assets on new projects)
and import StarterCharacter.dae.

How to use
----------
1. Create or open a project that includes Standard Assets.
2. In the Hierarchy, right-click -> Import Model.
3. Choose StarterCharacter.dae
   (Assets/Standard Assets/Characters/StarterCharacter/).
   Do not import the .gltf/.glb — Assimp 4.1 in this engine cannot load them.
4. The importer builds a SkinnedMeshRenderer and an Animator with one
   state per clip. .boneanim files are written next to the model on import.

Animator states (clip names)
----------------------------
  Idle  — looping stand / breathe (default on import)
  Walk  — looping walk cycle
  Run   — looping run cycle
  Wave  — looping wave

Files
-----
  StarterCharacter.dae                 — import this (mesh, skin, clips, material)
  textures/StarterCharacter_albedo.png — 512x512 original albedo
  StarterCharacter.gltf + .bin         — source copy (not importable in this engine)

Texture
-------
  Edit textures/StarterCharacter_albedo.png and re-import the .dae to recolor.

Previews
--------
  _preview_bindpose.png  — rest pose, front
  _preview_sheet.png     — rest, walk front, walk side, wave

License: CC0 1.0 (see LICENSE.txt).

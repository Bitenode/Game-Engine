Hotbar.prefab
=============

Drop-in HUD hotbar built with the runtime Canvas UI (panel, image, button, text, progress bar).

How to use
----------
1. After Standard Assets are copied into a project, find:
     Assets/Standard Assets/Prefabs/UI/Hotbar.prefab
2. Drag it onto the Hierarchy (or Hierarchy → Instantiate Prefab).
3. Press Play. Keys 1-9 select a slot; click a slot to select it.

The prefab is a full Screen Space Overlay canvas (SortOrder 10) so it works
without an existing HUD. Five slots ship with colored placeholder icons.

Gameplay
--------
Attach nothing extra — HotbarController is already on the root.

    var hotbar = SceneQuery.FindBehaviors<HotbarController>().FirstOrDefault();
    hotbar.SetSlotIcon(0, "Assets/Icons/sword.png");
    hotbar.SetSlotCount(2, 16);
    hotbar.SetSlotCooldown(0, 0.35f);
    hotbar.SelectionChanged += index => { /* equip item */ };

See Docs/03_Components_Reference.md (HUD Hotbar).

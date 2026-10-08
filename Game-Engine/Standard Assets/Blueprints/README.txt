Visual Blueprint starter graphs
=================================

Shipped with the engine under Standard Assets/Blueprints/. After you create a
project with "Include standard assets", open files from:

  Assets/Standard Assets/Blueprints/*.blueprint

Quick start
-----------
1. Add Scripting → Visual Blueprint to a GameObject.
2. Set Blueprint Asset Path, e.g.:
     Assets/Standard Assets/Blueprints/Starter_BeginPlay.blueprint
3. Window → New Blueprint Tab to inspect or edit the graph.
4. Press Play. Enable Log Steps on the component while authoring.

Demo scene (optional)
---------------------
  Assets/Standard Assets/Scenes/Blueprint Demos.scene

Ten empty GameObjects each run one starter graph. Open the scene, press Play,
and watch the console for log lines.

Starter graphs
--------------
Starter_BeginPlay.blueprint       Begin Play → Print String
Starter_TypedBranch.blueprint     float Health → Less → Branch (Then/Else)
Starter_DelaySequence.blueprint   Delay 1s → Sequence (two print chains)
Starter_ForLoop.blueprint         For 0..3 → SetVar LastIndex → Completed
Starter_Input.blueprint           Jump action + E key (Do Once)
Starter_Function.blueprint        CallFunction Double(21) → SetVar Doubled
Starter_Transform.blueprint       GetLocation + offset → Set Location (+1 Y)
Starter_CustomEvent.blueprint     Gate + Call Custom Event + Fire EventBus
Starter_RandomBranch.blueprint    Random Branch (50% Then / Else)
Legacy_V1_Branch.blueprint        Version-1 string-map Branch (compat sample)

Project blueprints
------------------
Save your own graphs under Assets/Blueprints/ (created automatically by the
editor). See Docs/14_Visual_Blueprints.md.

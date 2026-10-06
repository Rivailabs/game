"""Release 2: controlled assets.

* :mod:`.contracts` - geometry, materials, skeleton, motion, audio and provenance contracts (plan table);
* :mod:`.budgets` / :mod:`.clips` / :mod:`.skeleton` - the Astra budgets, required archer clip set,
  ``astra_archer_v1`` skeleton and the ``forge-retarget/1`` mapping format;
* :mod:`.routes` / :mod:`.territory` - the model-route table, licence/territory gates and selection;
* :mod:`.gpu` - GPU capability records, one lease per GPU, benchmarks;
* :mod:`.adapters` - generation adapters per route (REST, local sandboxed models, manual, libraries);
* :mod:`.lane` - stages, owner gates and invalidation rules;
* :mod:`.blender_tools` + ``blender/`` - committed bpy scripts (normalize, rig, motion) and their wrappers;
* :mod:`.unity_prefab` - the Unity prefab assembly contract;
* :mod:`.generation` - the lane driver the orchestrator calls (one owner gate per dispatch);
* :mod:`.provenance` / :mod:`.review` / :mod:`.cli` - provenance, licence inventory, review UI, CLI.
"""

# Semantic Inspection

These rules apply when semantic scene, prefab, or asset commands are available in `sgg_unity_describe`.

Prefer Gateway commands over raw file reads:

```text
1. Use scene.hierarchy or prefab.hierarchy before inspecting specific objects.
2. Use scene.inspect_object or prefab.inspect_object for focused details.
3. Use max_depth, max_nodes, path_prefix, and similar output limits.
4. Use asset.search before broad filesystem grep.
5. Use asset.references before editing, deleting, or moving scripts, prefabs, scenes, or ScriptableObjects.
6. Use asset.risk_report before destructive asset operations when available.
```

Do not assume command names. Discover the current catalog first, then fetch the exact command schema with `sgg_unity_describe(project, selector="command:<id>")`.

Keep responses bounded. Prefer focused object, scene, prefab, or asset queries over broad project scans.

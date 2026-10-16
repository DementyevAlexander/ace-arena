"""Экспорт Серафины в игру: Assets/_Project/Resources/Models/Seraphina.fbx.
Запуск в Blender (открыт Seraphina.blend): exec(open(".../export_fbx.py").read()).
Сцена портится (модификаторы применяются, сцена-подиум удаляется), поэтому в конце файл перечитывается с диска."""
import bpy, bmesh, os

OUT = os.path.join(os.path.dirname(bpy.data.filepath), "..", "..", "Assets", "_Project", "Resources", "Models", "Seraphina.fbx")
OUT = os.path.normpath(OUT)
bpy.ops.wm.save_mainfile()
# пересобрать лёгкую игровую сетку (полная остаётся в сохранённом .blend)
SERAPHINA_GAME = True
exec(open(os.path.join(os.path.dirname(bpy.data.filepath), "seraphina.py")).read())
del SERAPHINA_GAME

col = bpy.data.collections["Seraphina"]
for o in list(bpy.data.collections["Stage"].objects):
    bpy.data.objects.remove(o, do_unlink=True)
# дым в игре рисует SeraphFx — кривую не везём
bpy.data.objects.remove(bpy.data.objects["Smoke"], do_unlink=True)

# лицо: в игре столько полигонов не нужно
face = bpy.data.objects["Face"]
dec = face.modifiers.new("Decimate", "DECIMATE")
dec.ratio = 0.3

# золотая кромка перьев: в Blender это градиент по атрибуту rim, в игре — отдельный материал на крайних гранях
rim_mat = bpy.data.materials.get("FeatherRim") or bpy.data.materials.new("FeatherRim")
rim_mat.diffuse_color = (0.75, 0.45, 0.14, 1)
for name in ("WingL", "WingR"):
    me = bpy.data.objects[name].data
    me.materials.append(rim_mat)
    rim_idx = len(me.materials) - 1
    rim = me.attributes["rim"].data
    for p in me.polygons:
        if sum(rim[v].value for v in p.vertices) / len(p.vertices) > 0.6:
            p.material_index = rim_idx

# применить модификаторы
dg = bpy.context.evaluated_depsgraph_get()
for o in col.objects:
    if o.type == "MESH" and o.modifiers:
        ev = o.evaluated_get(dg)
        me = bpy.data.meshes.new_from_object(ev)
        old = o.data
        o.modifiers.clear()
        o.data = me
        bpy.data.meshes.remove(old)

tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in col.objects if o.type == "MESH")

for o in bpy.context.scene.objects:
    o.select_set(o.name in col.objects)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.export_scene.fbx(
    filepath=OUT, use_selection=True, object_types={"MESH", "EMPTY"}, apply_unit_scale=True,
    apply_scale_options="FBX_SCALE_ALL", bake_space_transform=True, axis_forward="-Z", axis_up="Y",
    use_mesh_modifiers=False, mesh_smooth_type="FACE", use_custom_props=False, add_leaf_bones=False,
    bake_anim=False, path_mode="AUTO", embed_textures=False)
print("exported", OUT, "tris", tris)
bpy.ops.wm.revert_mainfile()

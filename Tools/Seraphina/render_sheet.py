"""Лист разворота как на концепте: спереди, три четверти, сбоку, сзади — один PNG.
exec(open(".../render_sheet.py").read()); render_sheet(path)"""
import bpy, math
import numpy as np

VIEWS = [("front", -90), ("three_quarter", -125), ("side", 180), ("back", 90)]


def render_sheet(path, w=520, h=640, samples=48, dist=4.0, cz=0.68, views=VIEWS):
    scn = bpy.context.scene
    cam = bpy.data.objects["Cam"]
    tgt = bpy.data.objects["StageTarget"]
    tgt.location = (0, 0, cz)
    scn.render.resolution_x, scn.render.resolution_y = w, h
    scn.render.resolution_percentage = 100
    scn.eevee.taa_render_samples = samples
    scn.render.image_settings.file_format = "PNG"
    tiles = []
    tmp = bpy.app.tempdir + "sheet_tile.png"
    for name, ang in views:
        a = math.radians(ang)
        cam.location = (math.cos(a) * dist, math.sin(a) * dist, cz + 0.32)
        scn.render.filepath = tmp
        bpy.ops.render.render(write_still=True)
        img = bpy.data.images.load(tmp, check_existing=False)
        px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)
        tiles.append(px)
        bpy.data.images.remove(img)
    sheet = np.concatenate(tiles, axis=1)
    out = bpy.data.images.new("sheet", width=w * len(tiles), height=h, alpha=True)
    out.pixels = sheet.ravel()
    out.filepath_raw = path
    out.file_format = "PNG"
    out.save()
    bpy.data.images.remove(out)
    return path

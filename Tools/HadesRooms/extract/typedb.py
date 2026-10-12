"""Builds a merged type database (obstacles + units) from Game/Obstacles/*.sjson and Game/Units/*.sjson."""
import copy
import glob
import os
import sjson


def _merge(base, over):
    out = copy.deepcopy(base)
    for k, v in over.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _merge(out[k], v)
        else:
            out[k] = copy.deepcopy(v)
    return out


def load_types(content_dir):
    raw = {}
    src = {}
    for sub, key in (('Obstacles', 'Obstacles'), ('Units', 'Units')):
        for f in sorted(glob.glob(os.path.join(content_dir, 'Game', sub, '*.sjson'))):
            d = sjson.load(f)
            for e in d.get(key, []):
                if isinstance(e, dict) and 'Name' in e:
                    raw[e['Name']] = e
                    src[e['Name']] = '%s/%s' % (sub, os.path.basename(f))
    resolved = {}

    def res(name, depth=0):
        if name in resolved:
            return resolved[name]
        e = raw.get(name)
        if e is None or depth > 30:
            return {}
        parent = e.get('InheritFrom')
        r = _merge(res(parent, depth + 1), e) if parent else copy.deepcopy(e)
        r['_source'] = src[name]
        resolved[name] = r
        return r

    for n in raw:
        res(n)
    return resolved

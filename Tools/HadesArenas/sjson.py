"""Минимальный разбор SJSON (формат Supergiant): { Key = value }, [ ... ], строки без экранирования."""
import re
TOK = re.compile(r'\s+|//[^\n]*|/\*.*?\*/|(?P<s>"[^"]*")|(?P<p>[{}\[\]=,])|(?P<w>[^\s{}\[\]=,"]+)', re.S)


def tokens(text):
    for m in TOK.finditer(text):
        if m.group('s') is not None: yield ('s', m.group('s')[1:-1])
        elif m.group('p') is not None: yield ('p', m.group('p'))
        elif m.group('w') is not None: yield ('w', m.group('w'))


def loads(text):
    toks = list(tokens(text))
    if toks[0] != ('p', '{'):
        toks = [('p', '{')] + toks + [('p', '}')]
    i = 0

    def val():
        nonlocal i
        k, v = toks[i]; i += 1
        if k == 'p' and v == '{':
            d = {}
            while True:
                k2, v2 = toks[i]
                if k2 == 'p' and v2 == '}': i += 1; return d
                if k2 == 'p' and v2 == ',': i += 1; continue
                i += 1
                assert toks[i] == ('p', '='), (v2, toks[i]); i += 1
                d[v2] = val()
        if k == 'p' and v == '[':
            a = []
            while True:
                k2, v2 = toks[i]
                if k2 == 'p' and v2 == ']': i += 1; return a
                if k2 == 'p' and v2 == ',': i += 1; continue
                a.append(val())
        if k == 's': return v
        if v == 'true': return True
        if v == 'false': return False
        if v == 'null': return None
        try: return float(v)
        except ValueError: return v

    return val()

"""Minimal parser for Supergiant SJSON (relaxed JSON: `=` or `:`, optional commas,
// and /* */ comments, unquoted identifiers as keys/values)."""
import re

_TOK = re.compile(r'''
    (?P<ws>\s+|,)
  | (?P<lc>//[^\n]*)
  | (?P<bc>/\*.*?\*/)
  | (?P<str>"(?:[^"\\]|\\.)*")
  | (?P<num>[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?(?![A-Za-z_]))
  | (?P<punct>[{}\[\]=:])
  | (?P<ident>[A-Za-z_][A-Za-z0-9_./\\-]*)
''', re.S | re.X)


def tokenize(text):
    pos = 0
    n = len(text)
    while pos < n:
        m = _TOK.match(text, pos)
        if not m:
            raise ValueError('sjson: bad char %r at %d' % (text[pos:pos + 20], pos))
        pos = m.end()
        kind = m.lastgroup
        if kind in ('ws', 'lc', 'bc'):
            continue
        yield kind, m.group(kind)


class _P:
    def __init__(self, text):
        self.toks = list(tokenize(text))
        self.i = 0

    def peek(self):
        return self.toks[self.i] if self.i < len(self.toks) else (None, None)

    def next(self):
        t = self.toks[self.i]
        self.i += 1
        return t

    def value(self):
        k, v = self.next()
        if k == 'punct' and v == '{':
            return self.obj()
        if k == 'punct' and v == '[':
            return self.arr()
        if k == 'str':
            # keep backslashes literal (paths like Tilesets\Tartarus\X)
            return v[1:-1].replace('\\"', '"')
        if k == 'num':
            f = float(v)
            return int(f) if re.fullmatch(r'[-+]?\d+', v) else f
        if k == 'ident':
            if v == 'true':
                return True
            if v == 'false':
                return False
            if v == 'null':
                return None
            return v
        raise ValueError('sjson: unexpected %r %r' % (k, v))

    def obj(self):
        d = {}
        while True:
            k, v = self.peek()
            if k == 'punct' and v == '}':
                self.next()
                return d
            k, key = self.next()
            if k == 'str':
                key = key[1:-1]
            k2, eq = self.next()
            if eq not in ('=', ':'):
                raise ValueError('sjson: expected = after %r' % key)
            d[key] = self.value()

    def arr(self):
        a = []
        while True:
            k, v = self.peek()
            if k == 'punct' and v == ']':
                self.next()
                return a
            a.append(self.value())


def loads(text):
    p = _P(text)
    k, v = p.peek()
    if k == 'punct' and v == '{':
        return p.value()
    # top-level object without braces
    p.toks = [('punct', '{')] + p.toks + [('punct', '}')]
    return p.value()


def load(path):
    with open(path, 'r', encoding='utf-8-sig', errors='replace') as f:
        return loads(f.read())

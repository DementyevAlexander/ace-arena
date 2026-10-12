"""Tolerant parser for Lua *data* files (table literals assigned to globals).
Does NOT execute Lua. Identifiers/dotted refs become strings prefixed with '@'.
Non-literal expressions are kept as raw token strings prefixed with '~'."""
import re

_TOK = re.compile(r'''
    (?P<ws>\s+)
  | (?P<bc>--\[(?P<eq>=*)\[.*?\](?P=eq)\])
  | (?P<lc>--[^\n]*)
  | (?P<lstr>\[(?P<eq2>=*)\[.*?\](?P=eq2)\])
  | (?P<str>"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*')
  | (?P<num>0[xX][0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)
  | (?P<name>[A-Za-z_][A-Za-z0-9_]*)
  | (?P<op>\.\.\.|\.\.|==|~=|<=|>=|[-+*/%^#<>=(){}\[\];:,.])
''', re.S | re.X)


def tokenize(text):
    out = []
    pos = 0
    n = len(text)
    while pos < n:
        m = _TOK.match(text, pos)
        if not m:
            pos += 1
            continue
        pos = m.end()
        k = m.lastgroup
        if k in ('ws', 'bc', 'lc'):
            continue
        if k == 'eq' or k == 'eq2':
            continue
        if m.group('bc'):
            continue
        if m.group('lstr') is not None:
            out.append(('str', re.sub(r'^\[=*\[|\]=*\]$', '', m.group('lstr'))))
            continue
        if m.group('str') is not None:
            s = m.group('str')[1:-1]
            out.append(('str', s.encode('utf-8').decode('unicode_escape', 'ignore')))
            continue
        if m.group('num') is not None:
            v = m.group('num')
            out.append(('num', int(v, 16) if v.lower().startswith('0x') else (float(v) if re.search(r'[.eE]', v) else int(v))))
            continue
        if m.group('name') is not None:
            out.append(('name', m.group('name')))
            continue
        out.append(('op', m.group('op')))
    return out


class _P:
    def __init__(self, toks):
        self.t = toks
        self.i = 0

    def peek(self, o=0):
        j = self.i + o
        return self.t[j] if j < len(self.t) else (None, None)

    def take(self):
        v = self.t[self.i]
        self.i += 1
        return v

    def expr(self):
        """Parse an expression; return python value."""
        start = self.i
        k, v = self.peek()
        if k == 'op' and v == '{':
            val = self.table()
            if self._expr_end():
                return val
            self._skip_expr()
            return '~expr'
        if k == 'op' and v == '-' and self.peek(1)[0] == 'num':
            self.take()
            _, n = self.take()
            if self._expr_end():
                return -n
            self.i = start
        elif k in ('str', 'num'):
            self.take()
            if self._expr_end():
                return v
            self.i = start
        elif k == 'name':
            if v in ('true', 'false', 'nil'):
                self.take()
                if self._expr_end():
                    return {'true': True, 'false': False, 'nil': None}[v]
                self.i = start
            else:
                parts = [v]
                self.take()
                while self.peek() == ('op', '.') and self.peek(1)[0] == 'name':
                    self.take()
                    parts.append(self.take()[1])
                if self._expr_end():
                    return '@' + '.'.join(parts)
                self.i = start
        # generic: skip balanced until , ; } at depth 0
        self._skip_expr()
        return '~' + ' '.join(str(x[1]) for x in self.t[start:self.i])

    def _expr_end(self):
        k, v = self.peek()
        return k is None or (k == 'op' and v in (',', ';', '}', ']')) or (k == 'name' and self._looks_stmt_start())

    def _looks_stmt_start(self):
        # top-level: next token starts a new statement (name followed by = or . or ( )
        return True

    def _skip_expr(self):
        depth = 0
        while self.i < len(self.t):
            k, v = self.peek()
            if k == 'op' and v in ('{', '(', '['):
                depth += 1
            elif k == 'op' and v in ('}', ')', ']'):
                if depth == 0:
                    return
                depth -= 1
            elif k == 'op' and v in (',', ';') and depth == 0:
                return
            self.take()

    def table(self):
        assert self.take() == ('op', '{')
        d = {}
        arr = []
        while True:
            k, v = self.peek()
            if k is None:
                break
            if k == 'op' and v == '}':
                self.take()
                break
            if k == 'op' and v in (',', ';'):
                self.take()
                continue
            if k == 'name' and self.peek(1) == ('op', '='):
                self.take()
                self.take()
                d[v] = self.expr()
            elif k == 'op' and v == '[':
                self.take()
                key = self.expr()
                if self.peek() == ('op', ']'):
                    self.take()
                if self.peek() == ('op', '='):
                    self.take()
                d[key] = self.expr()
            else:
                arr.append(self.expr())
        if arr and not d:
            return arr
        if arr:
            for i, a in enumerate(arr):
                d[i + 1] = a
        return d


def parse_assignments(text):
    """Return dict of 'Dotted.Name' -> value for top-level `X.Y = expr` statements."""
    toks = tokenize(text)
    p = _P(toks)
    out = {}
    while p.i < len(toks):
        k, v = p.peek()
        if k == 'name' and v != 'local':
            # collect dotted name
            j = p.i
            parts = [v]
            j += 1
            while j + 1 < len(toks) and toks[j] == ('op', '.') and toks[j + 1][0] == 'name':
                parts.append(toks[j + 1][1])
                j += 2
            if j < len(toks) and toks[j] == ('op', '='):
                p.i = j + 1
                # top-level expression: stop at table end or next statement
                k2, v2 = p.peek()
                if k2 == 'op' and v2 == '{':
                    out['.'.join(parts)] = p.table()
                else:
                    t = p.take()
                    out['.'.join(parts)] = t[1]
                continue
        p.take()
    return out


def load(path):
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        return parse_assignments(f.read())

"""Tolerant reader for Tzar INI files (see docs/formats/ini-tsv.md).

parse(bytes, codepage) -> list of (section, [(key or None, value)]) preserving
order, duplicate sections/keys and key-less lines.
"""
import sys


def parse(data, codepage='cp1252'):
    text = data.decode(codepage)
    sections = []
    cur = (None, [])
    sections.append(cur)
    warnings = []
    for lineno, raw in enumerate(text.splitlines(), 1):
        line = raw.split(';', 1)[0].strip()
        if not line:
            continue
        if line.startswith('['):
            if not line.endswith(']'):
                warnings.append((lineno, 'unterminated section header'))
                line = line + ']'
            cur = (line[1:-1].strip(), [])
            sections.append(cur)
            continue
        if '=' in line:
            k, v = line.split('=', 1)
            cur[1].append((k.strip(), v.strip()))
        else:
            cur[1].append((None, line))
    if not sections[0][1]:
        sections.pop(0)
    return sections, warnings


def to_dict(sections):
    """Last-wins dict of dicts with lower-case section/key names."""
    out = {}
    for name, entries in sections:
        d = out.setdefault((name or '').lower(), {})
        for k, v in entries:
            if k is not None:
                d[k.lower()] = v
    return out


def load(path, codepage='cp1252'):
    with open(path, 'rb') as f:
        return parse(f.read(), codepage)


if __name__ == '__main__':
    secs, warns = load(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else 'cp1252')
    print('%d sections, %d warnings' % (len(secs), len(warns)))
    for w in warns:
        print('warning: line %d: %s' % w)

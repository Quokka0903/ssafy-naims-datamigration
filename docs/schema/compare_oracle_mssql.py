import re, glob, io

def load_oracle():
    tabs, cur = {}, None
    for f in sorted(glob.glob('oracle_p*.txt')):
        for line in io.open(f, encoding='utf-8', errors='replace'):
            line = line.rstrip('\n')
            m = re.match(r'^### TABLE:\s*(.+?)\s*(\[.*)?$', line)
            if m:
                name = m.group(1).strip()
                if name.startswith('<') or name.upper().startswith('(UNKNOWN'):
                    continue
                cur = name
                tabs.setdefault(cur, [])
                continue
            if not line.strip() or line.startswith(('##', 'PK:', '(', 'NOTE', '-')):
                continue
            if '\t' in line and cur:
                col, typ = line.split('\t', 1)
                col = col.strip().strip('"')
                if re.match(r'^[A-Z_][A-Z0-9_#$]*$', col):
                    tabs[cur].append((col, typ.strip()))
    return tabs

def load_mssql():
    tabs, cur = {}, None
    for line in io.open('mssql_naims_schema.txt', encoding='utf-8'):
        line = line.rstrip('\n')
        if line.startswith('## PRIMARY KEYS'):
            break
        m = re.match(r'^### TABLE:\s*(.+)$', line)
        if m:
            cur = m.group(1).strip(); tabs[cur] = []; continue
        if '\t' in line and cur:
            col, typ = line.split('\t', 1)
            tabs[cur].append((col.strip(), typ.strip()))
    return tabs

def fam_o(t):
    t = t.upper().replace('BYTE', '').replace(' ', '')
    if re.match(r'^NUMBER\((\*|\d+),0\)$', t): return ('INT', None)
    if t == 'NUMBER': return ('NUM', None)
    if t.startswith('NUMBER('): return ('NUM', None)
    m = re.match(r'^VARCHAR2\((\d+)\)$', t) or re.match(r'^CHAR\((\d+)\)$', t)
    if m: return ('STR', int(m.group(1)))
    m = re.match(r'^RAW\((\d+)\)$', t)
    if m: return ('BIN', int(m.group(1)))
    if t.startswith('TIMESTAMP') or t == 'DATE': return ('DT', None)
    if t in ('BLOB', 'CLOB', 'LONGRAW'): return ('LOB', None)
    return (t, None)

def fam_m(t):
    t = t.upper()
    for s in (' NOT NULL', ' IDENTITY', ' COMPUTED'): t = t.replace(s, '')
    t = t.replace(' ', '')
    if t in ('INT', 'SMALLINT', 'TINYINT', 'BIGINT', 'BIT'): return ('INT', None)
    if t in ('FLOAT', 'REAL') or t.startswith(('DECIMAL', 'NUMERIC')): return ('NUM', None)
    m = re.match(r'^N?(?:VAR)?CHAR\((\d+|MAX)\)$', t)
    if m: return ('STR', -1 if m.group(1) == 'MAX' else int(m.group(1)))
    m = re.match(r'^(?:VAR)?BINARY\((\d+|MAX)\)$', t)
    if m: return ('BIN', -1 if m.group(1) == 'MAX' else int(m.group(1)))
    if 'DATE' in t or 'TIME' in t: return ('DT', None)
    if t in ('IMAGE', 'TEXT', 'NTEXT'): return ('LOB', None)
    return (t, None)

ora, ms = load_oracle(), load_mssql()
ms.pop('sysdiagrams', None)

real, shrink, ident = [], [], 0
for t in sorted(set(ora) & set(ms)):
    od, md = dict(ora[t]), dict(ms[t])
    bad, shr = [], []
    for c, ot in od.items():
        if c not in md: continue
        fo, lo = fam_o(ot); fm, lm = fam_m(md[c])
        if fo != fm:
            bad.append((c, ot, md[c]))
        elif fo in ('STR', 'BIN') and lo is not None and lm is not None and lm >= 0 and lo > lm:
            shr.append((c, ot, md[c]))
    if bad: real.append((t, bad))
    if shr: shrink.append((t, shr))
    if not bad and not shr: ident += 1

print('common tables            : %d' % len(set(ora) & set(ms)))
print('fully type-compatible    : %d' % ident)
print()
print('== FAMILY MISMATCH (needs real conversion) ==')
for t, bad in real:
    print('  %s' % t)
    for c, o, m in bad: print('      %-26s ORA %-20s  MS %s' % (c, o, m))
print()
print('== LENGTH SHRINK (Oracle wider than MsSQL) ==')
for t, shr in shrink:
    print('  %s' % t)
    for c, o, m in shr: print('      %-26s ORA %-20s  MS %s' % (c, o, m))

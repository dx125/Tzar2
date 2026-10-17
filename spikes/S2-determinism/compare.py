"""Spike S2: compare the outputs of run-matrix.sh. Usage: python3 compare.py OUT_DIR

Prints a Markdown table: one row per configuration; the reference is the first label in sort order that starts with
the native arm64/x86_64 Release net10 build. For the tick stream it reports the first diverging tick, for each probe
category '=' (identical to the reference) or the number of differing values.
"""
import os
import sys


def load(out, label):
    summary = {}
    probes = []
    with open(os.path.join(out, label + ".summary.txt"), encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if parts[0] == "probe":
                probes.append((parts[3], parts[1]))
            else:
                summary[parts[0]] = parts[1]
    with open(os.path.join(out, label + ".ticks.txt"), encoding="utf-8") as f:
        ticks = [line.split()[1] for line in f]
    values = {}
    current = None
    with open(os.path.join(out, label + ".probes.txt"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("## "):
                current = line[3:].rstrip("\n")
                values[current] = []
            elif "\t" in line and line.split("\t", 1)[0].isdigit():
                values[current].append(line.rstrip("\n").split("\t", 1)[1])
            else:  # continuation of a multi-line value (error messages)
                values[current][-1] += "\\n" + line.rstrip("\n")
    return summary, probes, ticks, values


def main():
    out = sys.argv[1]
    labels = sorted(f[: -len(".summary.txt")] for f in os.listdir(out) if f.endswith(".summary.txt"))
    ref_label = next((l for l in labels if l.endswith("net10-release") and not l.startswith("x64rosetta")), labels[0])
    ref = load(out, ref_label)
    names = [p[0] for p in ref[1]]
    short = [n.split(" ")[0] for n in names]

    print(f"Reference: `{ref_label}` ({ref[0]['runtime']}, {ref[0]['arch']}); final hash `{ref[0]['final']}`")
    print()
    print("| configuration | runtime | final hash | tick stream | " + " | ".join(short) + " |")
    print("|---|---|---|---|" + "---|" * len(short))
    details = []
    for label in labels:
        summary, probes, ticks, values = load(out, label)
        first = next((i for i, (a, b) in enumerate(zip(ref[2], ticks)) if a != b), None)
        if first is None and len(ticks) != len(ref[2]):
            first = min(len(ticks), len(ref[2]))
        stream = "identical" if first is None else f"differs from tick {first}"
        cells = []
        for name in names:
            a, b = ref[3].get(name, []), values.get(name, [])
            diff = [i for i in range(max(len(a), len(b))) if i >= len(a) or i >= len(b) or a[i] != b[i]]
            cells.append("=" if not diff else f"**{len(diff)}**")
            for i in diff[:6]:
                details.append(f"* `{label}` {name.split(' ')[0]}[{i}]: ref `{a[i] if i < len(a) else '-'}` vs `{b[i] if i < len(b) else '-'}`")
        print(f"| `{label}` | {summary['runtime']} {summary['config']} | `{summary['final']}` | {stream} | " + " | ".join(cells) + " |")
    print()
    print("Probe categories: " + "; ".join(names))
    if details:
        print()
        print("First differing probe values (up to 6 per category and configuration):")
        print()
        print("\n".join(details))


if __name__ == "__main__":
    main()

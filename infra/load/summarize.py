"""Resumo dos CSVs de uma rodada do teste de carga (infra/load/run.sh run <rótulo> ...).

    python infra/load/summarize.py normal stress3x

Lê .out/stats-<rótulo>.csv (docker stats a cada ~3 s) e .out/outbox-<rótulo>.csv (amostra do outbox a cada 5 s) e
imprime CPU (média, p95, máx.; 100% = 1 vCPU), memória máxima por contêiner, a soma da "VM" (todos os contêineres no
mesmo instante, sobre as vCPUs de LOAD_CPUSET) e o pior momento do outbox. Só a biblioteca padrão.
"""

import csv
import os
import statistics
import sys
from collections import defaultdict

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".out")
VCPUS = len(os.environ.get("LOAD_CPUSET", "0,1").split(","))


def pct(values, p):
    if not values:
        return 0.0
    s = sorted(values)
    k = max(0, min(len(s) - 1, int(round(p / 100 * (len(s) - 1)))))
    return s[k]


def mib(text):
    value = text.strip().split("/")[0].strip()
    for unit, factor in (("GiB", 1024), ("MiB", 1), ("KiB", 1 / 1024), ("B", 1 / 1048576)):
        if value.endswith(unit):
            return float(value[: -len(unit)]) * factor
    return 0.0


def summarize(label):
    path = os.path.join(OUT, f"stats-{label}.csv")
    cpu = defaultdict(list)
    mem = defaultdict(list)
    per_ts = defaultdict(float)
    with open(path, encoding="utf-8") as f:
        for row in csv.DictReader(f):
            name = row["container"].replace("almoseis-loadtest-", "").replace("-1", "")
            c = float(row["cpu_pct"].rstrip("%") or 0)
            cpu[name].append(c)
            mem[name].append(mib(row["mem_usage"]))
            per_ts[row["ts"]] += c
    # Descarta as amostras ociosas do começo e do fim (antes do primeiro pedido / depois do último).
    print(f"\n## {label}: recursos (docker stats, 100% = 1 vCPU; VM = {VCPUS} vCPUs)")
    print("contêiner | CPU média | CPU p95 | CPU máx | memória máx (MiB)")
    for name in sorted(cpu):
        v = cpu[name]
        print(f"{name} | {statistics.mean(v):.0f}% | {pct(v, 95):.0f}% | {max(v):.0f}% | {max(mem[name]):.0f}")
    total = list(per_ts.values())
    print(f"VM (soma) | {statistics.mean(total):.0f}% | {pct(total, 95):.0f}% | {max(total):.0f}% | "
          f"{sum(max(m) for m in mem.values()):.0f}")
    print(f"VM em % das {VCPUS} vCPUs: média {statistics.mean(total) / VCPUS:.0f}%, p95 {pct(total, 95) / VCPUS:.0f}%, "
          f"máx {max(total) / VCPUS:.0f}%")

    path = os.path.join(OUT, f"outbox-{label}.csv")
    if os.path.exists(path):
        rows = []
        with open(path, encoding="utf-8") as f:
            for row in csv.DictReader(f):
                try:
                    rows.append((int(row["pending"]), float(row["oldest_pending_s"]), int(row["processed_last_5s"])))
                except (TypeError, ValueError):
                    continue
        if rows:
            print(f"outbox: pendentes máx {max(r[0] for r in rows)}, pendente mais antiga máx {max(r[1] for r in rows):.1f} s, "
                  f"vazão máx {max(r[2] for r in rows) / 5:.1f} msg/s ({len(rows)} amostras)")


if __name__ == "__main__":
    for label in sys.argv[1:] or ["normal"]:
        summarize(label)

# Registro de ensaios e restaurações (RNF-005, docs/09-devops.md §9.7)

Um registro por ensaio (`restore.sh test`) ou restauração real (`restore.sh production`). Obrigatório antes do piloto
(na VM de produção **e** na VPS do plano B) e a cada trimestre.

| Data | Ambiente / máquina | Tipo | Base | Instante alvo | tenants / orders / payments / daily_closings | Tempo total | Conferido no app | Quem |
|---|---|---|---|---|---|---|---|---|
| 2026-11-27 | máquina do dev (compose da VM + S3 local no lugar do R2) | ensaio (teste da ferramenta, sem dados reais) | `base_000000010000000000000003` | último WAL / `2026-11-27 22:39:19+00` | 2 / 0 / 0 / 0 e 1 / 0 / 0 / 0 (PITR antes do 2º cadastro) | 41 s / ~20 s | não se aplica | devops-agent |
| 2026-11-27 | máquina do dev (idem) | restauração "produção" (volume novo + troca) | `LATEST` | último WAL | 2 / 0 / 0 / 0, linha do tempo 2, base nova tirada | 46 s | API `Healthy` | devops-agent |

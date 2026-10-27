# ADR-0008 — Alterações e pagamentos com trilhas de auditoria

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 RF-013, §16
- **Refinado por:** ADR-0031 (implementação da auditoria e do outbox)

## Contexto

O dono precisa saber quem alterou pedidos, pagamentos, cardápios, fechamentos e permissões (US-013), inclusive depois que o funcionário for desativado.

## Decisão

Todas as operações de negócio listadas na RN-013.1 geram um registro de auditoria **append-only** com tenant, usuário, data/hora (UTC), entidade, tipo de operação, valores relevantes antes e depois, e origem. A auditoria de negócio é separada dos logs operacionais (RN-013.7).

## Consequências

- A forma de gravação, imutabilidade e minimização de dados está no ADR-0031 e no ADR-0024 (LGPD).

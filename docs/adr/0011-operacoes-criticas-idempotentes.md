# ADR-0011 — Operações críticas idempotentes

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 §9.1, §16
- **Refinado por:** ADR-0033

## Contexto

Reenvios causados por timeout ou queda de conexão não podem gerar pedidos nem pagamentos duplicados (RN-009.9, RN-014.7).

## Decisão

Operações críticas de criação e efeito financeiro exigem o header `Idempotency-Key`. O servidor guarda o resultado e, ao receber a mesma chave de novo, devolve o mesmo resultado sem reexecutar. A lista de operações, o armazenamento e o TTL estão no ADR-0033.

## Consequências

- A chave é gerada pelo cliente e persistida junto do rascunho antes do primeiro envio.

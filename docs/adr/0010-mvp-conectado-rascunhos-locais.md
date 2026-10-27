# ADR-0010 — MVP conectado, com rascunhos locais e recuperação de conexão

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 RF-014, §16
- **Relacionados:** ADR-0032, ADR-0033, ADR-0034

## Contexto

Funcionar offline por completo (pedidos e pagamentos sincronizados depois) exige resolver conflitos financeiros complexos. Por outro lado, a internet de restaurantes pequenos cai com frequência.

## Decisão

O app opera **conectado à API**. Offline ele:

- mostra o indicador de conexão e os dados em cache (somente leitura);
- guarda **rascunhos** de pedidos em SQLite, cada um com sua `Idempotency-Key` gerada no momento do envio;
- nunca mostra rascunho como pedido confirmado nem considera uma operação financeira concluída sem confirmação da API.

Ao reconectar: revalida a sessão, consulta as operações com resultado desconhecido (`GET /operations/{key}`), sincroniza pelo feed (ADR-0032) e só depois permite enviar os rascunhos.

## Alternativas consideradas

- **Offline integral:** ficou como F-022 (P2).

## Consequências

- Operações que exigem conexão ficam desabilitadas com uma explicação na tela.

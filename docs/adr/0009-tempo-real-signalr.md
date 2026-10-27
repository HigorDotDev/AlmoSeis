# ADR-0009 — Comunicação em tempo real via SignalR

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 §8, §16
- **Refinado por:** ADR-0031 (outbox), ADR-0032 (feed de sincronização), ADR-0036 (push), ADR-0037 (escala)

## Contexto

A cozinha precisa ver pedidos novos e alterações sem atualizar a tela, com meta de propagação de 3 s (RNF-002).

## Decisão

ASP.NET Core **SignalR** com grupos por tenant (`tenant:{id}`) e por permissão relevante (por exemplo, quem enxerga a produção). Os eventos levam identificadores, versão e `seq` do feed, **não o estado completo**. O cliente busca na API o que precisar.

O SignalR é só um aviso: **não garante** persistência nem entrega exatamente uma vez. A recuperação de eventos perdidos é feita pelo `/sync/changes` (ADR-0032).

## Alternativas consideradas

- **Polling:** simples, mas não atinge 3 s sem custo alto de bateria e de requisições.
- **Firebase/push como canal principal:** não serve de canal de dados entre funcionários conectados (veja o ADR-0036).

## Consequências

- O hub autentica com o mesmo JWT da API e entra no grupo do tenant presente no token.
- Com mais de uma réplica, é preciso backplane ou Azure SignalR Service (ADR-0037).

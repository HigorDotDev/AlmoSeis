# ADR-0036 — Notificações só em primeiro plano no MVP (sem push)

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-007/RF-008 (alertas com o app em segundo plano)
- **Relacionados:** ADR-0009

## Contexto

O SignalR só funciona com o app aberto. O épico não diz se a cozinha deve ser avisada com o app em segundo plano ou com a tela bloqueada.

## Decisão

1. No MVP, os alertas são entregues **só em primeiro plano**, via SignalR, com indicador visual e **aviso sonoro opcional** para pedido novo e alteração (configuração local do aparelho).
2. A aba **Pedidos** mantém a tela ligada (`expo-keep-awake`) enquanto está visível. A recomendação operacional é deixar um aparelho fixo na cozinha.
3. Ao voltar para o primeiro plano, o app reconecta e sincroniza pelo feed (ADR-0032).
4. **Push** (Expo Notifications/FCM/APNs) fica como evolução P1, junto com o EP-002 (pedidos do WhatsApp chegando com o app fechado).

## Alternativas consideradas

- **Push no MVP:** exige tokens por aparelho, permissões, regras de quem recebe e provedores. Pouco ganho com o aparelho fixo na cozinha.

## Consequências

- Precisa ficar documentado no onboarding do piloto (aparelho fixo, tela ligada, carregador).

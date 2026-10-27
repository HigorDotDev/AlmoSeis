# ADR-0035 — Versão mínima do app, EAS Update e versionamento da API

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em §7 ("versionamento dos contratos")
- **Relacionados:** ADR-0001, ADR-0037

## Contexto

Os apps nas lojas não atualizam todos ao mesmo tempo. Um cliente antigo, com regras de idempotência ou de versão desatualizadas, pode corromper dados financeiros.

## Decisão

1. **Expo** (Dev Client) + **EAS Build** para os binários e **EAS Update** para atualizações só de JavaScript, com `runtimeVersion` pela política `fingerprint`.
2. O app envia `X-App-Version` e `X-App-Platform` em toda requisição e na conexão SignalR.
3. A API tem `MinSupportedAppVersion` por plataforma (configuração). Abaixo dela responde **`426 Upgrade Required`** (`errorCode: AppVersionUnsupported`), e o app mostra uma tela bloqueante com link para a loja.
4. A API é versionada na rota (`/api/v1`). Mudanças incompatíveis criam `/api/v2`, que convive com a v1 até a versão mínima do app migrar.
5. Mudanças compatíveis (campos novos opcionais) não mudam a versão. O cliente ignora campos desconhecidos.

## Consequências

- Precisa de pipeline EAS no CI (ADR-0037) e de canais `preview`/`production`.

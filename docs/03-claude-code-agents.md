# 03. Desenvolvimento com Claude Code (agentes, regras e fases)

> Este documento descreve **como** o código é produzido. O **o quê** está em `01-epico-ep001.md` e em `adr/`.

## 3.1 Agentes

| Agente | Escopo | Gera código? |
|---|---|---|
| `domain-agent` | Regras de restaurante e financeiras, lacunas do épico, rascunho de ADRs | Não (só docs) |
| `architecture-agent` | Aderência aos ADRs, fronteiras de módulos, isolamento de tenant | Não |
| `database-agent` | Modelo físico, migrations EF, RLS, índices, consultas de fechamento | Sim (`backend/**/Migrations`, configurações EF, SQL) |
| `backend-agent` | Handlers, agregados, controllers, hub SignalR, outbox, idempotência | Sim (`backend/`) |
| `mobile-agent` | Telas Expo Router, hooks de API, rascunhos SQLite, sincronização | Sim (`mobile/`) |
| `devops-agent` | Docker Compose, GitHub Actions, Azure, Key Vault, EAS | Sim (`infra/`, `.github/`, Dockerfiles) |
| `ux-agent` | Telas atuais e proposta de UI/UX no Figma (plugin Figma), design system, acessibilidade e textos | Não (Figma e docs) |

## 3.2 Regras permanentes ("skills")

As regras sempre/nunca de cada domínio estão em `02-padroes.md`. Cada agente as repete no próprio arquivo, junto com os ADRs que mais afetam o seu domínio. Se uma regra mudar, atualize `02-padroes.md` **e** os agentes afetados.

## 3.3 Fluxo por funcionalidade

1. Regras/ADRs confirmados → 2. Modelo físico → 3. Contrato da API → 4. Backend → 5. Mobile → 6. Testes → 7. Revisão arquitetural.

## 3.4 Próximas fases da documentação

Na mesma linha do UrbanCarePlatform, antes de gerar código:

1. ~~Revisar os ADRs `Proposto`~~ — concluído em 2026-10-27: todas as recomendações (0018, 0024, 0026, 0029–0036) foram aceitas.
2. ~~`04-modelo-dados.md`~~ — concluído em 2026-10-27 (gerou os ADRs 0038 e 0039).
3. ~~`05-ddl.md`~~ — concluído em 2026-10-27, validado num PostgreSQL 16 com `05-ddl-smoke-test.sql`.
4. ~~`06-contratos-api.md`~~ — concluído em 2026-10-27 (gerou os ADRs 0040 e 0041 e dois ajustes de DDL revalidados).
5. ~~`07-backend.md` e `08-mobile.md`~~ — concluídos em 2026-10-27.
6. ~~`09-devops.md`~~ — concluído em 2026-10-27.
7. UX/UI (wireframes das 4 abas).
8. Desenvolvimento na ordem Identity/Tenancy → Catalog/Menu → BusinessDay → Ordering → Payments → Closing → Sync → Billing. Feitos: fundação, Identity (autocadastro, login, refresh, troca de tenant) Catalog/Menu (produtos, tamanhos, cardápios, If-Match/ETag) BusinessDay (abertura idempotente, cardápio semanal materializado, `GET /menus/current`) e Ordering (clientes e fiado, pedidos de marmita/PF/quilo, itens, pesagens, status, cancelamento, alertas, painel da cozinha e histórico) Payments (pagamento parcial, troco, divisão, fiado e estorno) Closing (movimentações, resumo, finalização, ajustes, histórico e mensal) Sync (`/sync/changes` e `/operations/{key}`) tempo real (outbox dispatcher, hub SignalR, `ClosingUpdated` agrupado, limpeza de retenção) e Billing (webhook do Asaas, ciclo de vida da assinatura, `402` de escrita, `GET /subscription`). Identity completo (inclui aparelho compartilhado, login por PIN e "esqueci a senha"). Administração da plataforma feita (ADR-0043: `POST /auth/admin/login` e `/admin/*`). Upload de imagem de produto no R2 feito (mesma configuração de cliente S3 do UrbanCarePlatform). Fluxo de imagem validado de ponta a ponta contra o bucket `almoseis-images-homolog`. Pendentes do backend: provedor de e-mail (ADR-0040) e chave do Data Protection persistida entre réplicas. App mobile (Expo SDK 58) feito em 2026-10-27: todas as telas de 08 §8.7–8.10, escritas críticas com resultado desconhecido, rascunhos, sync, SignalR, aparelho compartilhado; 50 testes Jest/RNTL; fluxos Maestro escritos e ainda não rodados (notas em 08 §8.14). Versão mínima (426), rate limiting (ADR-0041) e `GET /audit-logs` estão feitos.

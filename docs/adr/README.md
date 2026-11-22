# Architecture Decision Records — AlmoSeis

Toda decisão arquitetural ou de negócio que afeta modelagem, contrato de API, segurança, custo ou operação vira um ADR neste diretório (`NNNN-titulo-em-kebab-case.md`), usando o [modelo](0000-modelo.md).

**Status possíveis:** `Proposto` (aguarda revisão), `Aceito`, `Substituído por ADR-XXXX`, `Descartado`.

Quando um ADR conflita com o texto de um épico (`docs/01-epico-ep001.md`, `docs/11-epico-ep002.md`, `docs/12-epico-ep003.md`), **o ADR prevalece**. Um ADR `Proposto` já orienta a implementação, mas precisa ser revisado antes do código que depende dele ir para produção.

## Índice

### Registradas no épico (§16)

| ADR | Decisão | Status |
|---|---|---|
| [0001](0001-aplicativo-exclusivamente-mobile.md) | Aplicativo exclusivamente mobile para os restaurantes | Aceito |
| [0002](0002-backend-monolito-modular.md) | Backend em monólito modular ASP.NET Core | Aceito |
| [0003](0003-postgresql-banco-central.md) | PostgreSQL como banco de dados central | Aceito |
| [0004](0004-autenticacao-b2b2e-isolamento-tenant.md) | Autenticação e autorização B2B2E com isolamento por tenant | Aceito |
| [0005](0005-dominio-unico-pedidos-tres-modalidades.md) | Um único domínio de pedidos com três modalidades | Aceito |
| [0006](0006-independencia-producao-pagamento.md) | Independência entre produção e pagamento | Aceito |
| [0007](0007-pedidos-quitados-bloqueados.md) | Pedidos quitados bloqueados para edição financeira comum | Aceito |
| [0008](0008-auditoria-alteracoes-pagamentos.md) | Alterações e pagamentos com trilhas de auditoria | Aceito |
| [0009](0009-tempo-real-signalr.md) | Comunicação em tempo real via SignalR | Aceito |
| [0010](0010-mvp-conectado-rascunhos-locais.md) | MVP conectado, com rascunhos locais e recuperação de conexão | Aceito |
| [0011](0011-operacoes-criticas-idempotentes.md) | Operações críticas idempotentes | Aceito |
| [0012](0012-concorrencia-otimista.md) | Controle de concorrência otimista | Aceito |
| [0013](0013-fechamento-conciliacao-caixa.md) | Fechamento financeiro com conciliação do caixa físico | Aceito |
| [0014](0014-fechamentos-preservados-ajustes.md) | Fechamentos preservados, com ajustes posteriores rastreáveis | Aceito |
| [0015](0015-data-venda-data-recebimento.md) | Separação entre data da venda e data do recebimento | Aceito |
| [0016](0016-whatsapp-pagamentos-online-posterior.md) | Integração WhatsApp e pagamentos online em épico posterior | Aceito |

### Lacunas do épico (negócio)

| ADR | Decisão | Status |
|---|---|---|
| [0017](0017-cobranca-assinatura-gateway-externo.md) | Cobrança da assinatura por gateway brasileiro (Asaas), fora do app; ciclo de vida da assinatura | Aceito |
| [0018](0018-onboarding-tenant-convite-funcionarios.md) | Autocadastro do proprietário; convite de funcionário por link/código via WhatsApp | Aceito |
| [0019](0019-expediente-abertura-explicita-caixa-unico.md) | Expediente (`BusinessDay`) com abertura explícita e caixa único | Aceito |
| [0020](0020-composicao-preco-marmita-pf-quilo.md) | Marmita com preço por tamanho; PF por prato; quilo por preço/kg do estabelecimento | Aceito |
| [0021](0021-pagamento-parcial-troco-divisao-fiado.md) | Taxa de entrega, pagamento parcial, troco, divisão de conta, `Customer` e fiado; sem desconto | Aceito |
| [0022](0022-cancelamento-pedido-estorno-pagamento.md) | Cancelamento com motivo e permissão; estorno antes de cancelar; estorno destrava edição | Aceito |
| [0023](0023-numeracao-pedidos-por-expediente.md) | Número curto do pedido (reinicia por data operacional após o ADR-0039); `TabNumber` para comanda física | Aceito |
| [0024](0024-lgpd-papeis-retencao.md) | LGPD: controlador/operador, minimização, anonimização, retenção de 90 dias | Aceito |
| [0025](0025-administracao-plataforma-endpoints-internos.md) | Administração da plataforma por endpoints `/admin` internos, sem UI | Aceito |

### Lacunas do épico (técnicas)

| ADR | Decisão | Status |
|---|---|---|
| [0026](0026-isolamento-tenant-query-filter-rls.md) | Esquema compartilhado + query filter do EF + RLS do PostgreSQL | Aceito |
| [0027](0027-identidade-propria-jwt-pin-dispositivo-compartilhado.md) | ASP.NET Core Identity + JWT/refresh rotativo + PIN em dispositivo compartilhado | Aceito |
| [0028](0028-perfis-fixos-autorizacao-por-permissao.md) | Perfis fixos no MVP, autorização sempre por permissão | Aceito |
| [0029](0029-estrutura-modulos-backend.md) | .NET 10; projetos por módulo; DbContext único; sem MediatR | Aceito |
| [0030](0030-precisao-monetaria-peso-arredondamento.md) | `numeric(12,2)`, peso em gramas, arredondamento por pesagem, dinheiro como string na API | Aceito |
| [0031](0031-auditoria-transacional-outbox.md) | Auditoria explícita na mesma transação; outbox para SignalR | Aceito |
| [0032](0032-feed-sincronizacao-sequencia-por-tenant.md) | Feed de sincronização com sequência por tenant | Aceito |
| [0033](0033-idempotencia-armazenamento-ttl.md) | Idempotência em tabela, TTL de 72 h, `/operations/{key}` | Aceito |
| [0034](0034-versao-explicita-etag-if-match.md) | Coluna `version` + ETag/If-Match; 409 com o estado atual | Aceito |
| [0035](0035-versao-minima-app-eas-update.md) | Versão mínima do app (426), EAS Update, `/api/v1` | Aceito |
| [0036](0036-notificacoes-primeiro-plano-sem-push.md) | Alertas só em primeiro plano; push fica para P1 | Aceito |
| [0037](0037-hospedagem-azure-brasil-github-actions.md) | Azure Brazil South (Container Apps + PostgreSQL Flexible), R2, GitHub Actions | Aceito |

### Surgidas na consolidação do modelo de dados

| ADR | Decisão | Status |
|---|---|---|
| [0038](0038-cardapio-semanal-materializado-na-abertura.md) | Modelo semanal clonado como cardápio do dia na abertura do expediente | Aceito |
| [0039](0039-expedientes-multiplos-por-data-numeracao-por-data.md) | Vários expedientes por data; número do pedido contínuo por data (refina 0023) | Aceito |

O ADR-0026 também recebeu o item 9 (RLS das tabelas de vínculo por `user_id`).

### Surgidas na escrita dos contratos da API

| ADR | Decisão | Status |
|---|---|---|
| [0040](0040-email-transacional-azure-communication-services.md) | E-mail transacional (recuperação de senha do proprietário) via Azure Communication Services | Aceito |
| [0041](0041-limite-requisicoes-endpoints-publicos.md) | Rate limiting nativo do ASP.NET Core em login, PIN, convites e webhooks | Aceito |

### Surgidas na implementação

| ADR | Decisão | Status |
|---|---|---|
| [0042](0042-migrations-sql-first-a-partir-do-ddl.md) | Migrations SQL-first geradas dos blocos do `05-ddl.md`, com testes de sincronia, de fumaça e de mapeamento EF | Aceito |
| [0043](0043-login-administrador-plataforma.md) | `POST /auth/admin/login` só para `is_platform_admin`: token sem tenant, sem refresh, revalidado a cada requisição | Aceito |

Complementos: ADR-0027 (`deviceSecret` do aparelho compartilhado) e ADR-0028 (permissões `orders.view`/`production.view` e matriz em `06-contratos-api.md` §6.2).

### EP-002 — página de pedidos (`docs/11-epico-ep002.md`)

| ADR | Decisão | Status |
|---|---|---|
| [0044](0044-canal-web-pagina-publica-slug.md) | Página pública `dominio/{slug}` só com marmitas e bebidas, entrega e retirada; `Origin = WEB`; perfil público, estados da página e regras do slug | Proposto |
| [0045](0045-rotas-publicas-tenant-por-slug-anti-abuso.md) | Rotas `/public/*` resolvem o tenant pelo slug ou token e aplicam RLS (complementa 0026); idempotência sem usuário; Turnstile e limites | Proposto |
| [0046](0046-aceite-pedido-web-status-atendimento-entrega.md) | `AcceptanceStatus` (aceite, recusa com motivo, expiração, desistência) e `FulfillmentStatus` (saiu, entregue, retirado); número só no aceite; permissões e eventos novos | Proposto |
| [0047](0047-acompanhamento-pedido-pelo-cliente.md) | Acompanhamento por link com token opaco de 128 bits, 7 dias, consulta a cada 20 s | Proposto |
| [0048](0048-taxa-entrega-por-distancia.md) | Taxa = `max(km × preço/km, mínimo)`, rota de carro sem trânsito, km com 1 casa, cotação de 15 min, sem entrega se o provedor falhar (refina 0021) | Proposto |
| [0049](0049-push-pedidos-da-pagina.md) | Push (Expo → FCM/APNs) para pedido do site aguardando aceite, sem dado pessoal (substitui em parte 0036) | Proposto |
| [0050](0050-pagamento-online-split-asaas.md) | Fase 2: cobrança emitida pela subconta do restaurante com split da comissão, `IOrderPaymentGateway`, `apiKey` no Key Vault, online fora do caixa físico | Proposto |
| [0051](0051-projeto-web-nextjs-hospedagem.md) | Next.js em `web/`, `packages/shared` (ainda não criado), Container App `almoseis-web` com Cloudflare na frente | Proposto |
| [0052](0052-lgpd-cliente-da-pagina.md) | Aviso de privacidade na página, anonimização dos pedidos não aceitos em 7 dias, dados só no navegador (complementa 0024) | Proposto |
| [0053](0053-app-restaurante-navegador-desktop.md) | App do restaurante também no navegador do desktop: mesma base `mobile/` exportada para web (PWA) em `app.{dominio}`; refresh e `deviceSecret` em cookie `HttpOnly` só para `X-App-Platform: WEB`, CORS por lista, rascunhos em IndexedDB, 426 recarrega a página, Web Push depois (complementa 0001, 0010, 0027, 0035, 0036) | Proposto |

Afetados (linha de referência no próprio ADR): 0001, 0016, 0021, 0026, 0036. Também complementados, sem linha própria: 0006, 0013, 0022, 0023/0039, 0024, 0028, 0041; e, pelo 0053, 0010, 0017, 0027, 0035 e 0049.

Em 2026-11-20, os ADRs 0044 a 0053 foram alinhados à implementação das fatias 1 a 5 do EP-002 (até o commit `2f65bff`), e continuam `Proposto`. Os ADRs 0026 (item 10: funções `SECURITY DEFINER`, `store_slugs` com leitura aberta, correção de dados com `NO FORCE`/`FORCE`), 0029 (módulos Storefront e Notifications, portas e os dois laços do `OutboxDispatcher`) e 0033 (itens 7 a 9: segredo fora do corpo guardado, preparação antes da transação, chave anônima, aceite e desistência) mantêm o status `Aceito` e ganharam a linha "Complementado em 2026-11-20".

### EP-003 — tipo de estabelecimento e lanchonete (`docs/12-epico-ep003.md`)

| ADR | Decisão | Status |
|---|---|---|
| [0054](0054-comportamentos-estabelecimento-rotulos-modulos.md) | Comportamentos `RESTAURANT` e `SNACK_BAR` (conjunto por tenant, um no cadastro, fixo; segundo só pelo admin), rótulos de lista fixa, módulos fixos por comportamento atrás de `ITenantModules`; migração de todos como `RESTAURANT` | Proposto |
| [0055](0055-abas-app-por-comportamento.md) | Abas por módulo: restaurante como hoje; lanchonete Salão (comandas) · Delivery · Cozinha · Fechamento; sugestão para os dois comportamentos | Proposto |
| [0056](0056-cardapio-fixo-pratos-bebidas-combos-adicionais.md) | Cardápio fixo permanente: categorias, pratos com tamanho por prato, bebidas tipo → tamanho → sabor, combos fixos, grupos de adicionais, pausa até reativar com disponibilidade derivada | Proposto |
| [0057](0057-preco-de-site-vende-no-site.md) | "Vende no site" e preço de site (pratos, bebidas, combos e tamanhos de marmita); todo pedido do site usa o preço de site; `Order.PriceList`; cobrança da plataforma em aberto | Proposto |
| [0058](0058-modalidade-a-la-carte-comandas-cozinha.md) | `OrderType = A_LA_CARTE` (salão, entrega, retirada) só com o cardápio fixo; combo como `COMBO` + `COMBO_PART` + `GROUP_ADDON`; cozinha recebe lista normal | Proposto |
| [0059](0059-delivery-por-telefone-cliente.md) | Delivery por telefone: nome e telefone, endereço e taxa manual, forma prevista e troco, entra aceito; aba Delivery; `Customer` pelo telefone sem endereço salvo | Proposto |

Complementados em 2026-11-22 (seção "Complementado por ADR-00xx" no próprio ADR, status mantido): 0005, 0017, 0020, 0021, 0024, 0025, 0028, 0038 (`Aceito`) e 0044, 0046, 0050 (`Proposto`; no 0050, nota no item 19).

## Impacto no modelo do épico (§6)

O modelo consolidado, com todos os campos, invariantes e o mapeamento épico → modelo, está em [`docs/04-modelo-dados.md`](../04-modelo-dados.md).

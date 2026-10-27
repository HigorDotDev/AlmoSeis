# Architecture Decision Records — AlmoSeis

Toda decisão arquitetural ou de negócio que afeta modelagem, contrato de API, segurança, custo ou operação vira um ADR neste diretório (`NNNN-titulo-em-kebab-case.md`), usando o [modelo](0000-modelo.md).

**Status possíveis:** `Proposto` (aguarda revisão), `Aceito`, `Substituído por ADR-XXXX`, `Descartado`.

Quando um ADR conflita com o texto do épico (`docs/01-epico-ep001.md`), **o ADR prevalece**. Um ADR `Proposto` já orienta a implementação, mas precisa ser revisado antes do código que depende dele ir para produção.

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

Complementos: ADR-0027 (`deviceSecret` do aparelho compartilhado) e ADR-0028 (permissões `orders.view`/`production.view` e matriz em `06-contratos-api.md` §6.2).

## Impacto no modelo do épico (§6)

O modelo consolidado, com todos os campos, invariantes e o mapeamento épico → modelo, está em [`docs/04-modelo-dados.md`](../04-modelo-dados.md).

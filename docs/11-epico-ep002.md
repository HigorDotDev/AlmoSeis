# ÉPICO EP-002 — Página de pedidos pela web

> **Nota de precedência (2026-11-19):** as decisões deste épico estão nos ADRs 0044–0052 (`docs/adr/README.md`), todos `Proposto`. **Onde houver diferença, o ADR prevalece.** O EP-002 substitui o esboço "Atendimento automatizado" do §15 do EP-001: o chatbot de WhatsApp deu lugar a uma página web que o restaurante envia pelo WhatsApp.

**Produto:** AlmoSeis — página pública de pedidos por restaurante  
**Versão:** 1.0 — Requisitos iniciais  
**Status:** Planejamento  
**Plataformas:** web (consumidor, navegador do celular); Android e iOS (restaurante, app existente)  
**Modelo de negócio:** a página vem com a assinatura; taxa por transação online soma à assinatura (Fase 2)  
**Arquitetura:** a mesma API e o mesmo domínio de pedidos do EP-001, mais um projeto web Next.js (ADR-0051)

---

# 1. Visão geral

## 1.1. Descrição

Cada restaurante ganha, junto com a conta, uma página pública **`dominio/{slug}`**. Todas as páginas usam o mesmo template.

O restaurante envia o link pelo WhatsApp. O cliente final monta o pedido sozinho, sem chatbot nem atendente: escolhe as marmitas e as bebidas, diz se é entrega ou retirada, vê a taxa de entrega calculada pela distância e envia. O pedido cai direto no app do restaurante.

- Pedido **"pagar na entrega/retirada"** passa por **aceite** no app antes de ir para a cozinha.
- Pedido **pago online** (Fase 2) vai direto para a cozinha.
- O cliente **acompanha** o pedido por um link, sem login.

O ADR-0001 continua valendo: o produto para o restaurante continua sendo só o app. A página é para o consumidor.

## 1.2. Objetivos de negócio

- Tirar do atendente a digitação dos pedidos que chegam pelo WhatsApp.
- Reduzir erro de anotação (tamanho, acompanhamento, endereço, troco).
- Cobrar a taxa de entrega justa, pela distância.
- Responder "meu pedido já saiu?" sem o cliente precisar perguntar.
- Abrir uma nova receita por transação online (Fase 2).
- Preparar o marketplace de restaurantes (Fase 3).

## 1.3. Público

- **Consumidor final:** cliente da marmitaria, no celular, a partir do link no WhatsApp. Sem cadastro.
- **Restaurante:** os mesmos perfis do EP-001, que passam a aceitar, recusar e despachar pedidos do site.

---

# 2. Escopo por fase

| Fase | Conteúdo | Situação |
|---|---|---|
| **Fase 1** | Página pública e perfil (slug), cardápio do dia de marmitas e bebidas, entrega e retirada, taxa por distância, envio "pagar na entrega/retirada", aceite/recusa no app, push com o app fechado, acompanhamento pelo cliente, status "Saiu para entrega"/"Entregue"/"Retirado" | Detalhada neste épico |
| **Fase 2** | Pagamento online na página (Pix e cartão) com split via Asaas, subconta do restaurante, estorno online, fechamento com recebimento online | Detalhada, com questões em aberto |
| **Fase 3** | Marketplace: homepage que lista restaurantes próximos e todos da plataforma | Só esboço (§3, RF-215) |

---

# 3. Requisitos funcionais

## RF-201 — Página pública e perfil do restaurante

Cada tenant tem uma página `dominio/{slug}` com perfil público (ADR-0044).

- RN-201.1: O slug é gerado do nome no cadastro, é único global e só usa `a-z`, `0-9` e `-` (3 a 40 caracteres).
- RN-201.2: O slug é editável com `storefront.manage`. O antigo redireciona para o novo e continua reservado ao mesmo tenant.
- RN-201.3: Existe uma lista de slugs reservados, pensando nas rotas do marketplace e do sistema.
- RN-201.4: O perfil mostra nome, logo, capa opcional, endereço, contato opcional, texto de horário, modos (entrega/retirada) e a regra da taxa de entrega.
- RN-201.5: O estado da página é calculado no servidor: `OPEN`, `CLOSED` ("Fechado agora", sem expediente aberto ou fora da janela), `PAUSED` (pausa rápida) ou `UNAVAILABLE` (página inativa ou assinatura sem escrita).
- RN-201.6: A página nasce inativa, com o slug reservado. Ativar exige endereço confirmado e um tamanho de marmita ativo (*a confirmar*).
- RN-201.7: O link tem prévia no WhatsApp (título, descrição e imagem).

**Critérios de aceite:** abrir `dominio/{slug}` mostra o perfil e o estado correto; um slug antigo redireciona; um slug reservado é recusado no cadastro; a prévia aparece ao colar o link no WhatsApp.

---

## RF-202 — Cardápio na página

- RN-202.1: A página vende **só marmitas e bebidas** (ADR-0044).
- RN-202.2: O cardápio é o `DAILY` de `MARMITA` da data do expediente aberto (ADR-0038). Sem expediente aberto, a página mostra "Fechado agora" e não mostra o cardápio.
- RN-202.3: Itens indisponíveis aparecem desabilitados ("Acabou").
- RN-202.4: Preço da marmita = tamanho + adicionais (ADR-0020). Bebidas pelo preço do produto.
- RN-202.5: Bebidas e adicionais podem ser escondidos da página (`IsVisibleOnPage`).

**Critérios de aceite:** a página reflete em até 30 s uma mudança de disponibilidade feita no app; um item marcado como indisponível não pode ser escolhido.

---

## RF-203 — Montagem e envio do pedido

- RN-203.1: O cliente informa **nome, telefone** e, na entrega, **endereço**. Sem login e sem código de confirmação.
- RN-203.2: O pedido tem pelo menos 1 marmita e no máximo 20 marmitas e 30 linhas (*a confirmar*).
- RN-203.3: Cada marmita tem tamanho, prato(s), acompanhamentos, adicionais e observação.
- RN-203.4: Na opção "pagar na entrega/retirada", o cliente informa a forma prevista (dinheiro, Pix ou cartão) e, se dinheiro, "troco para R$ X". Isso não cria pagamento.
- RN-203.5: O servidor recalcula tudo. Se um preço mudou ou um item acabou desde a abertura da página, o cliente vê o carrinho atualizado e confirma de novo (`409 CartChanged`).
- RN-203.6: O envio é idempotente: tocar duas vezes ou perder a resposta não cria dois pedidos (ADR-0045).
- RN-203.7: O envio passa por anti-robô (Turnstile) e por limites de requisição e de pedidos pendentes por telefone e IP (ADR-0045).
- RN-203.8: O aviso de privacidade aparece antes do botão "Enviar pedido" (ADR-0052).
- RN-203.9: Depois do envio, o cliente recebe o link de acompanhamento.

**Critérios de aceite:** um pedido enviado aparece no app em até 3 s; reenviar com a mesma chave devolve o mesmo pedido; um terceiro pedido pendente do mesmo telefone é recusado.

---

## RF-204 — Taxa de entrega por distância

- RN-204.1: Taxa = `max(km × preço por km, taxa mínima)`, com km de rota de carro sem trânsito, arredondado a 1 casa, e taxa arredondada ao centavo (ADR-0048).
- RN-204.2: O cálculo é sempre no servidor, por cotação com validade de 15 min.
- RN-204.3: Raio máximo opcional. Fora do raio, a página oferece retirada (*raio a confirmar*).
- RN-204.4: O cliente vê o endereço como foi entendido e confirma antes de pagar a taxa.
- RN-204.5: Se o provedor de mapas falhar, a página não oferece entrega naquele momento e oferece retirada.
- RN-204.6: Pedidos do app continuam com a taxa padrão editável do ADR-0021.

### Exemplo

| Distância | Preço por km | Mínimo | Taxa |
|---|---:|---:|---:|
| 4,5 km | R$ 0,80 | R$ 3,00 | R$ 3,60 |
| 2,1 km | R$ 0,80 | R$ 3,00 | R$ 3,00 |
| 4,5 km | R$ 0,85 | R$ 3,00 | R$ 3,83 |

**Critérios de aceite:** a taxa mostrada é a cobrada; uma cotação vencida com taxa diferente exige nova confirmação; um endereço aproximado (só bairro) não gera taxa.

---

## RF-205 — Configuração da página no app

- RN-205.1: Tela "Página de pedidos" com `storefront.manage`: ativar/desativar, slug, perfil público, endereço e ponto no mapa, modos, regra da taxa, janela de horário opcional.
- RN-205.2: Chaves rápidas de efeito imediato: **pausar pedidos** e **pausar entrega** (só retirada).
- RN-205.3: Compartilhar o link pelo WhatsApp e mostrar QR code para imprimir no balcão.

**Critérios de aceite:** pausar no app muda o estado da página em até 30 s; o link compartilhado abre a página certa.

---

## RF-206 — Aceite e recusa no app

- RN-206.1: Pedido "pagar na entrega/retirada" chega como `AWAITING_ACCEPTANCE` e só vai para a cozinha depois do aceite (ADR-0046).
- RN-206.2: Aceitar e recusar exigem `orders.accept`.
- RN-206.3: O número do pedido é atribuído **no aceite**. Recusados e expirados não recebem número.
- RN-206.4: A recusa exige motivo da lista (item acabou, fora da área, muitos pedidos, fechando, pedido suspeito, outro) e texto opcional, mostrado ao cliente.
- RN-206.5: Prazo de aceite de **10 min** (*a confirmar*). Vencido, o pedido expira e o cliente é avisado.
- RN-206.6: Ao finalizar o fechamento, os pendentes de aceite são recusados automaticamente, com aviso na confirmação.
- RN-206.7: O cliente pode desistir enquanto o pedido aguarda aceite.
- RN-206.8: Pedidos do site têm o selo "Site", o aviso "telefone não verificado" e o alerta "Possível duplicado".
- RN-206.9: Pedido não aceito não entra no fechamento, na cozinha nem nas pendências do cliente.
- RN-206.10: O aceite pode informar um tempo estimado (15 a 60 min).

**Critérios de aceite:** dois funcionários aceitando ao mesmo tempo resultam em um único aceite; um pedido recusado nunca aparece no fechamento; o pedido expira sozinho no prazo.

---

## RF-207 — Status de atendimento e entrega

- RN-207.1: Pedidos de entrega e retirada ganham `FulfillmentStatus`, independente da produção e do pagamento (ADR-0046).
- RN-207.2: Entrega: Pendente → **Saiu para entrega** → **Entregue**. Retirada: Pendente → **Retirado**. "Pronto para retirar" é derivado da produção concluída.
- RN-207.3: Marcar "Saiu" ou "Retirado" exige produção concluída. O app oferece "Concluir e marcar" numa operação só.
- RN-207.4: "Voltou sem entregar" retorna a Pendente.
- RN-207.5: Os status valem também para pedidos de entrega e retirada do app, com uso opcional (*a confirmar*).
- RN-207.6: Depois que o pedido saiu, não é possível acrescentar itens (*a confirmar*).
- RN-207.7: Mudar o atendimento exige `orders.fulfillment.update`.

**Critérios de aceite:** marcar "Saiu para entrega" atualiza o acompanhamento do cliente em até 20 s; pedido entregue e não pago continua com saldo.

---

## RF-208 — Acompanhamento pelo cliente

- RN-208.1: Link `dominio/{slug}/pedido/{token}`, com token aleatório de 128 bits, válido por 7 dias (ADR-0047).
- RN-208.2: Mostra status com horários, itens, taxa, total, forma de pagamento, rua e número da entrega e o primeiro nome. Não mostra telefone, complemento nem dados de outros pedidos.
- RN-208.3: Atualiza a cada 20 s enquanto a tela está visível.
- RN-208.4: Avisa quando o restaurante altera o pedido.

**Critérios de aceite:** um token inventado devolve `404`; o token de um tenant não abre pedido de outro; o link expira em 7 dias.

---

## RF-209 — Notificações push

- RN-209.1: Pedido do site aguardando aceite gera push para os aparelhos com `orders.accept`, com o app fechado (ADR-0049).
- RN-209.2: Lembrete aos 3 min sem resposta.
- RN-209.3: O push não leva nome, telefone, endereço nem valor.
- RN-209.4: Com o app aberto, o pedido do site toca um som próprio e fica numa faixa até alguém responder.
- RN-209.5: O aparelho pode desligar o aviso do site.

**Critérios de aceite:** com o app fechado e a tela bloqueada, o push chega e abre o pedido; após o logout, o aparelho deixa de receber.

---

## RF-210 — Fechamento com pedidos do site (Fase 1)

- RN-210.1: Pedidos do site aceitos entram no vendido por modalidade (`MARMITA`), como qualquer pedido.
- RN-210.2: Recusados, expirados e desistências aparecem só num indicador informativo (quantidade).
- RN-210.3: Pagamentos de pedidos do site na Fase 1 são manuais e seguem o RF-009 e o ADR-0013.

---

## RF-211 — Pagamento online com split (Fase 2)

- RN-211.1: "Pagar agora" por Pix (QR na página) ou cartão (checkout do provedor). A AlmoSeis nunca recebe dados de cartão (ADR-0050).
- RN-211.2: A cobrança é emitida pela **subconta do restaurante**, com split da comissão para a AlmoSeis.
- RN-211.3: Pagamento confirmado por webhook → pedido pago, aceito pelo sistema, numerado e enviado à cozinha.
- RN-211.4: Pix com validade de 15 min (*a confirmar*). Pagamento que chega depois do prazo ou do fechamento é estornado automaticamente.
- RN-211.5: O valor online é sempre o total. Sem pagamento parcial online.

### Exemplo (comissão ilustrativa)

| | Valor |
|---|---:|
| Pedido (2 marmitas M + refrigerante + entrega 4,5 km) | R$ 45,60 |
| Taxa Pix do Asaas (a confirmar) | − R$ 1,08 |
| Comissão AlmoSeis: 2% do líquido (ilustrativo) | − R$ 0,89 |
| **Restaurante recebe** | **R$ 43,63** |

---

## RF-212 — Recebedor do restaurante (Fase 2)

- RN-212.1: Só o proprietário (`payments.online.manage`) liga o recebimento online.
- RN-212.2: O app cria a subconta no provedor e mostra a situação (pendente, ativa, recusada, bloqueada). O KYC é feito com o provedor.
- RN-212.3: A chave de API da subconta é segredo por tenant no Key Vault, nunca em banco, log ou resposta.

---

## RF-213 — Estorno online (Fase 2)

- RN-213.1: Cancelar pedido pago online exige estornar antes (ADR-0022), e o estorno passa pelo provedor.
- RN-213.2: O estorno fica pendente até o provedor confirmar. Falha volta o pagamento a confirmado e avisa o proprietário.
- RN-213.3: O estorno da cobrança estorna também a comissão do split.

---

## RF-214 — Fechamento com recebimento online (Fase 2)

- RN-214.1: Recebimento online aparece separado ("Recebido online pela página"), pelo valor bruto, e nunca entra no dinheiro esperado.
- RN-214.2: As taxas (provedor e plataforma) aparecem como informação, não como desconto.

| Informação | Valor |
|---|---:|
| Recebido em dinheiro | R$ 850,00 |
| Recebido em Pix (manual) | R$ 300,00 |
| Recebido em cartão (manual) | R$ 200,00 |
| Recebido online pela página | R$ 45,60 |
| Taxas do recebimento online (informativo) | R$ 1,97 |
| Dinheiro esperado (inalterado) | R$ 750,00 |

---

## RF-215 — Marketplace (Fase 3, esboço)

- Homepage com restaurantes próximos ao usuário (por localização) e lista de todos da plataforma, com páginas por cidade.
- Rotas sob prefixos reservados (`/restaurantes`, `/cidades/{cidade}`). A raiz `/{slug}` continua do restaurante (ADR-0044).
- Exige: geolocalização dos restaurantes (já feita no RF-204), opção do restaurante de aparecer ou não no marketplace, SEO (ADR-0051).
- Não detalhado. Vira um épico próprio.

---

# 4. Requisitos não funcionais

## RNF-201 — Desempenho da página

- JavaScript inicial ≤ 150 KB comprimido; LCP ≤ 2,5 s em 4G num Android de entrada.
- A página funciona sem instalar nada e sem fontes externas.
- Pedido enviado aparece no app em até 3 s (RNF-002 do EP-001).

## RNF-202 — Prévia e indexação

- HTML com Open Graph renderizado no servidor (ADR-0051).
- Acompanhamento com `noindex` e sem cache.

## RNF-203 — Segurança e anti-abuso

- Tenant das rotas públicas resolvido pelo slug ou pelo token, com query filter e RLS (ADR-0045).
- Rotas autenticadas continuam tirando o tenant só do JWT (ADR-0026).
- Turnstile, limites por IP, por telefone e por tenant, honeypot, tamanho máximo do pedido.
- Segredos (provedor de mapas, Turnstile, Expo, subcontas) no Key Vault.

## RNF-204 — Privacidade

- Aviso de privacidade com o restaurante como controlador (ADR-0052).
- Dados mínimos; anonimização dos pedidos não aceitos após 7 dias.
- Nenhum dado pessoal em log, auditoria, evento ou push.

## RNF-205 — Custo e disponibilidade

- Custo de mapas dentro da faixa grátis no piloto, com alerta de consumo.
- Falha do provedor de mapas não derruba a página (a retirada continua).
- Página com 1 réplica mínima na Azure Brazil South (ADR-0051).

## RNF-206 — Acessibilidade

- WCAG 2.1 AA, áreas de toque de pelo menos 44 px, funciona com a fonte do sistema grande.

---

# 5. Arquitetura

| Componente | Mudança |
|---|---|
| Projeto `web/` (Next.js) | Novo. Página do restaurante, envio e acompanhamento (ADR-0051) |
| `packages/shared` | Novo. Tipos da API pública, Zod, dinheiro em centavos, prévia de preço |
| API — rotas `/api/v1/public/*` | Novas, sem JWT, tenant pelo slug ou token (ADR-0045) |
| Módulo Ordering | Aceite, recusa, expiração, atendimento, `Origin = WEB` (ADR-0046) |
| Módulo Storefront (novo, ou dentro de Identity) | Perfil público, slug, configuração de entrega, cotação (ADR-0044, ADR-0048) |
| Módulo Notifications | Push pela Expo (ADR-0049) |
| Módulo OnlinePayments (novo, Fase 2) | `IOrderPaymentGateway`, Asaas, webhooks (ADR-0050) |
| Provedores externos | Google Geocoding/Routes (a confirmar), Cloudflare Turnstile, Expo Push, Asaas (Fase 2) |

---

# 6. Impacto nos documentos (trabalho dos outros agentes)

Este épico **não** altera agora os documentos abaixo. Cada agente atualiza o seu, depois da revisão dos ADRs:

| Documento | Agente | O que muda |
|---|---|---|
| `04-modelo-dados.md` | `database-agent` | `Order`: `Origin = WEB`, `CreatedByEmployeeId` nulo, `OrderNumber` nulo até o aceite, `AcceptanceStatus` e campos de aceite/recusa, `FulfillmentStatus`, `ExpectedPaymentMethod`, `ChangeFor`, snapshot da taxa por distância, `RiskFlag`. Novas entidades: `StorefrontSettings`, `StoreSlug`, `OrderTrackingToken`, `DeliveryQuote`, `GeocodeCacheEntry`, `PushRegistration`; Fase 2: `OnlinePaymentRecipient`, `OnlineCharge`, `OrderPaymentWebhookEvent`, `Payment.Channel`/taxas/`REVERSAL_PENDING`, `PaymentReversal.Status`. `Product.IsVisibleOnPage`. `AuditLog.Origin` com `WEB` e `ORDER_PAYMENT_WEBHOOK`. Consultas de cozinha, fechamento e pendências filtrando `AcceptanceStatus = ACCEPTED` |
| `05-ddl.md` | `database-agent` | Tabelas e colunas acima; `store_slug` e `order_tracking_token` sem RLS com grants só de `SELECT`/`INSERT`/`UPDATE`; unicidade do número só para não nulos; índices de expiração |
| `06-contratos-api.md` | `backend-agent` (contrato) | Rotas `/public/*` com exemplos JSON; `POST /orders/{id}/accept`, `/reject`, `PATCH /orders/{id}/fulfillment`; `/storefront`; `PUT /devices/push-token`; novos `errorCode`s (`StoreNotFound`, `StoreUnavailable`, `CartChanged`, `OutOfDeliveryArea`, `DeliveryQuoteChanged`, `TooManyPendingOrders`, `WebOrderExpired`, `OrderAlreadyDispatched`, `TrackingExpired`); permissões novas na §6.2; eventos novos na §6.14; Fase 2: recebedor, cobrança e webhook de pedidos |
| `07-backend.md` | `backend-agent` | `ITenantContext.Source`, middleware das rotas públicas, `IPublicHandler` + teste de arquitetura, jobs de expiração e retenção, envio de push pelo outbox, `IGeocoder`/`IRouteDistanceProvider`, Fase 2 `IOrderPaymentGateway` |
| `08-mobile.md` | `mobile-agent` | Faixa "Novos do site", aceite/recusa, selo "Site", status de atendimento, tela "Página de pedidos", mapa para confirmar o endereço, push (permissão, canal, som, registro do token), uso do `packages/shared` |
| `09-devops.md` | `devops-agent` | Container App `almoseis-web`, workflow `web.yml`, DNS e CDN no Cloudflare, segredos novos, alertas de mapas e de push, credenciais FCM/APNs no EAS |
| `10-ux.md` | `ux-agent` | Telas da página (perfil, cardápio, carrinho, endereço, confirmação, acompanhamento) e as novas telas do app |

---

# 7. Fora do escopo

- PF, quilo e consumo no salão pela página.
- Chatbot de WhatsApp e mensagens automáticas pelo WhatsApp.
- Login ou cadastro do consumidor; histórico de pedidos do consumidor no servidor.
- Cupom e desconto (ADR-0021 continua: sem desconto).
- Pedido agendado para outro horário ou outro dia (ADR-0019).
- Pedido mínimo por valor.
- Taxa por bairro (pode virar um modo da taxa depois).
- Gestão de entregadores, roteirização e rastreio do entregador no mapa.
- Avaliações e comentários.
- Chave Pix do restaurante na página para pagamento manual antecipado (avaliar depois do piloto).
- Dashboard web para restaurantes (ADR-0001).
- Impressão automática de comanda.

---

# 8. Backlog de features

| ID | Feature | Fase | Prioridade |
|---|---|---|---|
| F-201 | Perfil público, slug e estado da página | 1 | P0 |
| F-202 | Rotas públicas, resolução por slug e anti-abuso | 1 | P0 |
| F-203 | Cardápio do dia na página (marmitas e bebidas) | 1 | P0 |
| F-204 | Carrinho, identificação do cliente e envio idempotente | 1 | P0 |
| F-205 | Taxa de entrega por distância e cotação | 1 | P0 |
| F-206 | Aceite, recusa, expiração e desistência | 1 | P0 |
| F-207 | Status de atendimento (saiu, entregue, retirado) | 1 | P0 |
| F-208 | Acompanhamento pelo cliente | 1 | P0 |
| F-209 | Push para pedido do site | 1 | P0 |
| F-210 | Tela "Página de pedidos" no app (configuração, pausa, compartilhar, QR) | 1 | P0 |
| F-211 | Projeto web, hospedagem e CI/CD | 1 | P0 |
| F-212 | Aviso de privacidade e rotinas de retenção | 1 | P0 |
| F-213 | Tempo estimado no aceite | 1 | P1 |
| F-214 | Alerta "Possível duplicado" | 1 | P1 |
| F-215 | Recebedor (subconta) e KYC | 2 | P1 |
| F-216 | Cobrança Pix e cartão com split | 2 | P1 |
| F-217 | Estorno online e chargeback | 2 | P1 |
| F-218 | Fechamento com recebimento online | 2 | P1 |
| F-219 | Calcular taxa por distância no app | — | P2 |
| F-220 | Link de acompanhamento para pedidos do app | — | P2 |
| F-221 | Bloquear telefone após trote | — | P2 |
| F-222 | Marketplace | 3 | P2 |

**P0:** obrigatório para o piloto da página. **P1:** logo depois. **P2:** evolução.

---

# 9. Histórias de usuário

### US-201 — Pedir pela página

Como cliente, quero montar minhas marmitas pelo link que o restaurante mandou para não precisar ficar trocando mensagens.

### US-202 — Saber a taxa antes

Como cliente, quero ver a taxa de entrega antes de enviar para decidir entre entrega e retirada.

### US-203 — Acompanhar

Como cliente, quero ver se o restaurante aceitou e quando meu pedido saiu para não precisar perguntar.

### US-204 — Desistir

Como cliente, quero desistir do pedido enquanto ninguém aceitou para não ter um pedido que não quero mais.

### US-205 — Aceitar ou recusar

Como atendente, quero aceitar ou recusar o pedido do site com um toque, dizendo o motivo, para que só entre na cozinha o que dá para fazer.

### US-206 — Não perder pedido

Como atendente, quero ser avisado com o app fechado quando chegar pedido do site para que ele não expire.

### US-207 — Despachar

Como atendente, quero marcar "Saiu para entrega" e "Entregue" para que o cliente e a equipe saibam onde está o pedido.

### US-208 — Configurar a página

Como proprietário, quero definir meu link, o preço por km e a taxa mínima e pausar os pedidos quando a cozinha lota.

### US-209 — Divulgar

Como proprietário, quero compartilhar o link no WhatsApp e imprimir um QR code para o balcão.

### US-210 — Receber online (Fase 2)

Como proprietário, quero receber Pix e cartão pela página, com o dinheiro caindo na minha conta, para que o pedido pago vá direto para a cozinha.

### US-211 — Conferir o online (Fase 2)

Como caixa, quero ver o recebido online separado do dinheiro para que a conferência do caixa físico continue certa.

---

# 10. Critérios gerais de conclusão (Fase 1)

1. Cada restaurante do piloto tem uma página ativa com slug próprio.
2. Um pedido enviado pela página aparece no app em até 3 s, com push se o app estiver fechado.
3. Pedido "pagar na entrega/retirada" só vai para a cozinha depois do aceite.
4. Recusados e expirados não têm número e não entram no fechamento.
5. A taxa de entrega cobrada é a mostrada ao cliente.
6. O cliente acompanha o pedido pelo link até "Entregue" ou "Retirado".
7. Um slug ou token de um tenant nunca lê ou grava dados de outro (testes de integração).
8. Reenvio do mesmo pedido não cria duplicado.
9. Nenhum dado pessoal do cliente aparece em log, auditoria, evento ou push.
10. A página carrega dentro das metas do RNF-201 num Android de entrada.

---

# 11. Métricas de sucesso

- Restaurantes com página ativa.
- Pedidos pela página por dia e % do total de pedidos de marmita.
- Tempo médio entre envio e aceite.
- Taxa de recusa (por motivo) e de expiração.
- Desistências.
- Cotações por pedido enviado (custo de mapas) e falhas do provedor.
- Push entregues / enviados.
- Acompanhamentos abertos por pedido.
- Fase 2: % de pedidos pagos online, receita de comissão por restaurante.

**Meta inicial:** nos 10 restaurantes do piloto, pelo menos 20% dos pedidos de marmita chegando pela página em 30 dias, com expiração abaixo de 5%.

---

# 12. Riscos

| ID | Risco | Mitigação |
|---|---|---|
| R-201 | Pedido expira porque ninguém viu (celular no bolso, economia de bateria do Android) | Push com som próprio, lembrete, faixa persistente, orientação de onboarding (ADR-0049) |
| R-202 | Trote ou robô enchendo a fila de aceite | Turnstile, limites por IP e telefone, aceite humano, motivo "pedido suspeito" (ADR-0045) |
| R-203 | Termos do Google não permitem exibir resultado sem mapa ou limitam o cache | Verificar antes de contratar; alternativa Mapbox ou OSRM (ADR-0048) |
| R-204 | Custo de mapas acima do esperado | Cache por tenant, cotação só ao confirmar o endereço, limite diário por tenant, alerta |
| R-205 | Expediente esquecido aberto aceita pedido de madrugada | Janela de horário opcional (obrigatória para pagamento online, a confirmar) e expiração |
| R-206 | Limite do período de avaliação do Asaas (R$ 2.000 por subconta em 60 dias, cerca de 44 pedidos de R$ 45) | Piloto curto do pagamento online e adequação ao BaaS em paralelo (ADR-0050) |
| R-207 | Asaas exigir CPF do pagador | Confirmar antes da Fase 2; pedir CPF só no "Pagar agora" |
| R-208 | Chargeback com split sem regra clara | Confirmar com o Asaas antes de liberar cartão |
| R-209 | Enquadramento regulatório se a AlmoSeis receber o valor cheio | Cobrança emitida pela subconta do restaurante (ADR-0050) |
| R-210 | Telefone de outra pessoa digitado no pedido | Vínculo com `Customer` sem alterar dados, aviso "telefone não verificado" (ADR-0044) |
| R-211 | Lojas de apps questionarem a comissão exibida no app | Venda de bem físico; confirmar com as diretrizes, como no ADR-0017 |

---

# 13. Decisões arquiteturais e de negócio

| ADR | Decisão |
|---|---|
| [0044](adr/0044-canal-web-pagina-publica-slug.md) | Canal web, perfil público e slug |
| [0045](adr/0045-rotas-publicas-tenant-por-slug-anti-abuso.md) | Rotas públicas: tenant pelo slug, RLS e anti-abuso |
| [0046](adr/0046-aceite-pedido-web-status-atendimento-entrega.md) | Aceite do pedido web e status de atendimento/entrega |
| [0047](adr/0047-acompanhamento-pedido-pelo-cliente.md) | Acompanhamento pelo cliente com token opaco |
| [0048](adr/0048-taxa-entrega-por-distancia.md) | Taxa de entrega por distância de rota |
| [0049](adr/0049-push-pedidos-da-pagina.md) | Push para pedidos da página |
| [0050](adr/0050-pagamento-online-split-asaas.md) | Pagamento online com split (Asaas, `IOrderPaymentGateway`) |
| [0051](adr/0051-projeto-web-nextjs-hospedagem.md) | Projeto web Next.js e hospedagem |
| [0052](adr/0052-lgpd-cliente-da-pagina.md) | LGPD do cliente da página |

---

# 14. Questões em aberto

Precisam de decisão do usuário antes do código que depende delas ir para produção. **N** = negócio (o usuário decide); **T** = técnica (há recomendação).

| # | Tipo | Questão | Recomendação atual | ADR |
|---|---|---|---|---|
| Q-01 | N | **Valor e forma da taxa da plataforma** nos pedidos pagos online: fixa, percentual sobre o líquido, ou as duas; com mínimo? | Sem recomendação de valor. O exemplo usa 2% ou R$ 0,99 só como ilustração | 0050 |
| Q-02 | N | **Quem absorve a taxa do Asaas:** restaurante (padrão do desenho), cliente (taxa de serviço separada no total) ou AlmoSeis | Restaurante, que é o vendedor | 0050 |
| Q-03 | N | **Comissão em estorno:** o Asaas devolve o split junto. A AlmoSeis aceita devolver a comissão sempre? | Sim, devolver (simples e justo com o restaurante) | 0050 |
| Q-04 | N | **Monetização dos pedidos "pagar na entrega/retirada"** | Em aberto por decisão do usuário ("veremos mais pra frente"). Por enquanto, sem cobrança | — |
| Q-05 | N | **Raio máximo de entrega:** existe? opcional? qual padrão? | Opcional, sem padrão | 0048 |
| Q-06 | N | **Prazo de aceite** e o lembrete | 10 min, lembrete aos 3 min | 0046 |
| Q-07 | T | **Domínio** da página e da API | Lacuna. Estrutura: `{dominio}`, `api.{dominio}`, `homolog.{dominio}` | 0051 |
| Q-08 | T | **Termos do Google** (exibir resultado sem mapa, cache de 30 dias, atribuição) e escolha final do provedor de mapas | Google se os termos permitirem; senão Mapbox ou OSRM | 0048 |
| Q-09 | N/T | **Adequação BaaS / white label no Asaas** após o período de avaliação (10 subcontas, R$ 2.000 cada, 60 dias); custo de criação de subconta | Iniciar a conversa com o Asaas antes da Fase 2 | 0050 |
| Q-10 | N | Os status **"Saiu para entrega"/"Entregue"** valem para pedidos de entrega do app? | Sim, com uso opcional | 0046 |
| Q-11 | N | **CPF do pagador** se o Asaas exigir na Fase 2 | Pedir só no "Pagar agora" | 0050 |
| Q-12 | N | A página nasce **ativa ou inativa** no cadastro? | Inativa, até o endereço ser confirmado | 0044 |
| Q-13 | N | **Limite de marmitas por pedido** da página | 20 | 0044 |
| Q-14 | N | **Pedido só de bebidas** pela página | Não aceitar | 0044 |
| Q-15 | N | **Limite de acompanhamentos por tamanho** na página (o ADR-0020 não tem; sem atendente, o cliente pode marcar todos) | Avaliar no piloto; se virar problema, limite por tamanho | 0020, 0044 |
| Q-16 | N | **Bloquear acréscimo de itens** depois de "Saiu para entrega" | Bloquear; o atendente cria outro pedido | 0046 |
| Q-17 | N | **Arredondamento comercial da taxa** (múltiplos de R$ 0,50 ou R$ 1,00 para cima) | Não arredondar (o exemplo do usuário usa R$ 3,60) | 0048 |
| Q-18 | N | **Janela de horário obrigatória** para ligar o pagamento online | Sim | 0044, 0050 |
| Q-19 | N | **Validade do Pix** na página | 15 min | 0050 |
| Q-20 | T | **Comissão exibida no app** e as diretrizes das lojas | Exibir como informação contratual; confirmar | 0050 |
| Q-21 | T | **Chargeback com split** no Asaas | Confirmar antes de liberar cartão | 0050 |

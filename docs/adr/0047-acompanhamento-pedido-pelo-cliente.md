# ADR-0047 — Acompanhamento do pedido pelo cliente, por link com token opaco

- **Status:** Proposto
- **Data:** 2026-11-19
- **Atualizado:** 2026-11-20, alinhado à implementação das fatias 2 a 5 do EP-002 e às correções da revisão (até o commit `2f65bff`). Itens alterados: 1, 2, 3, 5, 8. Ver também "Implementação (2026-11-20)".
- **Origem:** EP-002, RF-208; decisão do dono do produto (item 6) em 2026-11-19
- **Relacionados:** ADR-0024, ADR-0045, ADR-0046, ADR-0050, ADR-0052

## Contexto

O cliente quer saber se o restaurante aceitou e quando o pedido sai, sem login. Hoje ele pergunta pelo WhatsApp, e é exatamente isso que a página quer evitar. O link de acompanhamento é o único "acesso" do cliente ao pedido: se for previsível, qualquer um lê pedidos alheios. Se mostrar demais, um link encaminhado num grupo expõe o endereço de alguém.

## Decisão

1. **Link:** `dominio/{slug}/pedido/{trackingToken}`. O token tem **128 bits**, codificados em base64url (22 caracteres). Não é sequencial e não é o `Id` do pedido. *(Alterado em 2026-11-20: era "aleatório, não derivado de nada".)*
   - Ele é **derivado por HMAC**: `base64url(HMAC-SHA-256(PublicPage:TrackingTokenSecret, "tracking-token:v1:{tenant}:{pedido}")[..16])`.
   - Sem o segredo, é tão imprevisível quanto um token aleatório. E é sempre o **mesmo** para o mesmo pedido, o que permite ao reenvio idempotente recalculá-lo em vez de guardá-lo.
   - O segredo fica no Key Vault, é obrigatório fora de Development (32 caracteres ou mais, diferente do `IpHashSecret`) e é aleatório por processo em Development.
   - Trocar o segredo não invalida os tokens já emitidos, porque o banco guarda o hash deles. Só um reenvio idempotente feito depois da troca devolveria um token diferente.
2. O servidor guarda só o **SHA-256 do token**, em `order_tracking_tokens` (ADR-0045). O token em claro **não fica guardado em lugar nenhum**: nem no pedido, nem no registro de idempotência, cujo corpo sai sem o `trackingToken` (ADR-0033 item 7), nem em log. Ele aparece na resposta do envio do pedido e na repetição idempotente dessa resposta, que o recalcula.
3. **A API resolve o tenant só pelo token**, com a função `resolve_tracking_token`, sem leitura aberta da tabela (ADR-0045 item 5). A rota da API é `/public/tracking/{token}`, sem slug. **A página confere o slug:** se o `store.slug` da resposta for diferente do `{slug}` do endereço, ela redireciona para o atual. *(Alterado em 2026-11-20: era "o servidor confere o slug".)*
4. **Validade: 7 dias** desde o envio. Depois disso, `410 TrackingExpired`: "Este acompanhamento expirou" + link para a página do restaurante. O registro do token é apagado no mesmo job.
5. **O que a página mostra:**
   - nome e contato do restaurante (logo fora desta fase, ADR-0044 "Implementação");
   - número do pedido (só depois do aceite) e horário do envio;
   - linha do tempo do status (tabela abaixo), com horário de cada passo e o tempo estimado, se informado no aceite;
   - itens com composição, quantidades e preços, taxa de entrega com a distância ("4,5 km"), total, forma de pagamento prevista ou "Pago online";
   - entrega: **rua e número** (sem complemento nem referência), para o cliente conferir; retirada: endereço do restaurante;
   - **primeiro nome** do cliente ("Pedido de Carla").
   - Quando o restaurante edita o pedido depois do aceite: aviso "O restaurante alterou seu pedido" e o total atualizado.
6. **Nunca mostra:** telefone, sobrenome, complemento, ponto de referência, funcionários, outros pedidos, saldo de fiado.
7. **Status mostrado ao cliente** (derivado das dimensões do ADR-0046):

   | Condição | Texto |
   |---|---|
   | `AWAITING_PAYMENT` | "Aguardando pagamento" (Fase 2) |
   | `AWAITING_ACCEPTANCE` | "Aguardando o restaurante confirmar" + botão "Desistir do pedido" |
   | `REJECTED` / `EXPIRED` / `WITHDRAWN` | mensagem do ADR-0046 |
   | `ACCEPTED` + produção `OPEN` | "Pedido confirmado" |
   | `ACCEPTED` + `IN_PROGRESS` | "Em preparo" |
   | entrega + `COMPLETED` + `PENDING` | "Pronto, aguardando o entregador" |
   | retirada + `COMPLETED` + `PENDING` | "Pronto para retirar" |
   | `OUT_FOR_DELIVERY` | "Saiu para entrega" |
   | `DELIVERED` / `PICKED_UP` | "Entregue" / "Retirado" |
   | `CancellationStatus = CANCELED` | "Pedido cancelado pelo restaurante" + contato |

   Um pedido que volta de `COMPLETED` para `IN_PROGRESS` (item acrescentado) volta a mostrar "Em preparo". Isso é esperado.
8. **Atualização: consulta periódica**, não SignalR. A página consulta a cada **20 s** enquanto está visível (`visibilitychange`) e para nos estados finais. A resposta tem `ETag`; sem mudança → `304`.
   - O `ETag` é **opaco**: combina a versão do pedido, o status calculado, a versão de `storefront_settings` e o slug (ex.: `"3-IN_PREPARATION-7-marmitaria-da-ana"`). Ele muda quando o prazo de aceite vence antes de o job gravar, porque o status calculado já vira `EXPIRED` sem mudar a versão, e também quando o restaurante muda a página. A página não interpreta o valor.
   - **Os dados são carregados no navegador**, direto da API. O servidor da página renderiza só a casca, sem dados, sem cache e sem indexação. Assim o limite `public-tracking` (60/min por IP, ADR-0045 item 11) vê o IP de cada cliente, e não o do servidor da página. *(Detalhado em 2026-11-20.)*
9. Cabeçalhos: `Cache-Control: private, no-store`, `Referrer-Policy: no-referrer`, `X-Robots-Tag: noindex`. A página de acompanhamento não carrega recursos de terceiros (o token está no caminho e vazaria no `Referer`).
10. Na tela de confirmação do envio, a página pede para o cliente **guardar o link** e oferece "Copiar link" e "Enviar para mim no WhatsApp" (`wa.me` com o link, aberto pelo próprio cliente). Os pedidos dos últimos 7 dias ficam no `localStorage` do navegador: ao voltar para `/{slug}`, aparece "Acompanhar pedido em andamento".
11. O restaurante pode copiar o link de acompanhamento no app (para mandar ao cliente pelo WhatsApp). Pedidos do **app** também podem ganhar um link, sob demanda, se o atendente tocar em "Gerar link de acompanhamento" (P2).

## Alternativas consideradas

- **Login ou código por SMS:** o dono do produto descartou (atrito e custo).
- **SignalR anônimo:** atualização instantânea, mas abre uma conexão persistente sem autenticação, com custo de réplica e superfície de ataque. Para um status que muda poucas vezes em 40 minutos, 20 s bastam.
- **Link com o `Id` (UUID v7) do pedido:** o UUID v7 tem parte temporal previsível e o `Id` aparece em outros lugares (logs, feed).
- **Link sem validade:** links encaminhados expõem dados para sempre.
- **Mostrar o endereço completo:** ajuda pouco o cliente e expõe complemento e referência se o link circular.

## Consequências

- Nova tabela de plataforma `order_tracking_tokens` (`token_hash` único, `tenant_id`, `order_id` único, `expires_at`), lida antes do tenant só pela função `resolve_tracking_token`.

## Implementação (2026-11-20)

- **Desistência** (`POST /public/tracking/{token}/withdraw`, ADR-0046 item 17): sem corpo e sem `Idempotency-Key`, **idempotente pelo estado**.
  - Repetir com o pedido já `WITHDRAWN` (resposta perdida) devolve o mesmo `200` com o acompanhamento atual, sem nova auditoria nem evento.
  - Aceito ou recusado → `409 OrderNotAwaitingAcceptance`; prazo vencido → `409 WebOrderExpired`.
  - A resposta leva o mesmo `ETag` do acompanhamento.
- **Erros:**
  - token inventado, malformado ou de pedido apagado → `404 NotFound`, sem nada do restaurante. Um token com formato inválido nem chega ao banco;
  - mais de 7 dias → `410 TrackingExpired`. Depois que o job de retenção apaga o registro, o mesmo token passa a dar `404`.
- **Cabeçalhos** (item 9): saem em **toda** resposta de `/public/tracking/*`, inclusive nos erros.
- **Status** (item 7): a API devolve o código (`CONFIRMED`, `IN_PREPARATION`, `READY_FOR_DELIVERY`, `READY_FOR_PICKUP`…) e a página monta o texto. Um prazo de aceite vencido aparece como `EXPIRED` antes mesmo de o job gravar.
- **Item 11:** nenhuma das duas partes foi implementada nesta fase. O app não mostra o link de acompanhamento do pedido do site, e não há link sob demanda para os pedidos do app. Como o token é derivado (item 1), a API pode recalculá-lo quando isso entrar, sem guardá-lo.
- 20 s × 40 min ≈ 120 requisições leves por pedido acompanhado. Dentro do `public-tracking` do ADR-0045.
- Revisitar a validade de 7 dias se o pagamento posterior (fiado de pedido web) precisar do link.

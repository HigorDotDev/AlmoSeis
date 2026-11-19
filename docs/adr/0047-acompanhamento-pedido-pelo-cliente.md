# ADR-0047 — Acompanhamento do pedido pelo cliente, por link com token opaco

- **Status:** Proposto
- **Data:** 2026-11-19
- **Origem:** EP-002, RF-208; decisão do dono do produto (item 6) em 2026-11-19
- **Relacionados:** ADR-0024, ADR-0045, ADR-0046, ADR-0050, ADR-0052

## Contexto

O cliente quer saber se o restaurante aceitou e quando o pedido sai, sem login. Hoje ele pergunta pelo WhatsApp, e é exatamente isso que a página quer evitar. O link de acompanhamento é o único "acesso" do cliente ao pedido: se for previsível, qualquer um lê pedidos alheios. Se mostrar demais, um link encaminhado num grupo expõe o endereço de alguém.

## Decisão

1. **Link:** `dominio/{slug}/pedido/{trackingToken}`. O token tem **128 bits aleatórios** (gerador criptográfico), codificados em base64url (22 caracteres). Não é sequencial, não é o `Id` do pedido e não é derivado de nada.
2. O servidor guarda só o **SHA-256 do token** em `order_tracking_token` (ADR-0045). O token em claro aparece uma única vez, na resposta do envio do pedido (e na repetição idempotente dessa resposta).
3. O servidor confere que o token pertence ao tenant do `{slug}`. Se o slug mudou, a página redireciona para o atual.
4. **Validade: 7 dias** desde o envio. Depois disso, `410 TrackingExpired`: "Este acompanhamento expirou" + link para a página do restaurante. O registro do token é apagado no mesmo job.
5. **O que a página mostra:**
   - nome, logo e contato do restaurante;
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

- Nova tabela de plataforma `order_tracking_token` (`token_hash` único, `tenant_id`, `order_id`, `expires_at`).
- 20 s × 40 min ≈ 120 requisições leves por pedido acompanhado. Dentro do `public-tracking` do ADR-0045.
- Revisitar a validade de 7 dias se o pagamento posterior (fiado de pedido web) precisar do link.

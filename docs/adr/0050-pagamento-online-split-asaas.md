# ADR-0050 — Pagamento online na página, com split (Asaas atrás de `IOrderPaymentGateway`)

- **Status:** Proposto
- **Data:** 2026-11-19
- **Origem:** EP-002 Fase 2, RF-211 a RF-214; decisões do dono do produto (itens 2, 7 e 8) em 2026-11-19
- **Relacionados:** ADR-0006, ADR-0007, ADR-0013, ADR-0014, ADR-0015, ADR-0016 (substituído em parte), ADR-0017, ADR-0019, ADR-0022, ADR-0024, ADR-0029, ADR-0031, ADR-0037, ADR-0046, ADR-0052

## Contexto

O dono do produto decidiu:

- pedido pago na plataforma vai direto para a cozinha (ADR-0046);
- a receita é uma **taxa por transação que soma à assinatura**;
- o provedor é o **Asaas**, atrás de uma interface genérica (para trocar por Mercado Pago ou outro);
- **ainda não decidiu** quem absorve a taxa do Asaas nem o valor da taxa da plataforma.

Levantamento de 2026-11-19 (*tudo a confirmar com o Asaas*):

- O split é só pela API. Cada recebedor é um `walletId`. O split pode ser `fixedValue` e/ou `percentualValue`, e o percentual incide sobre o `netValue` (valor já sem as taxas do Asaas). O saldo não distribuído fica com quem emitiu a cobrança. A carteira emissora não pode estar no split.
- Estornar a cobrança estorna também os repasses. A liquidação de cada repasse vem no webhook `PAYMENT_SPLIT_DONE` (`AWAITING_CREDIT`/`DONE`). A documentação não fala de chargeback com split nem de custo extra pelo split.
- Subcontas: `POST /v3/accounts`; a conta-mãe precisa ser PJ (CNPJ); cada subconta recebe `apiKey` e `walletId`; o titular faz o KYC (e-mail de ativação e documentos) ou, no modelo BaaS, onboarding por link. Há webhooks de situação da conta. Criar subconta pode ter taxa conforme o contrato.
- **Período de avaliação em produção:** até **10 subcontas**, até **R$ 2.000,00** em cobranças por subconta, por no máximo **60 dias**. Depois, gerenciar as chaves das subcontas exige adequação ao **BaaS** do Asaas.
- Taxas na página oficial: Pix R$ 1,08 por transação recebida (R$ 2,15 aparece ao lado, talvez preço cheio ou promoção); débito R$ 0,35 + 1,89%; crédito à vista ≈ R$ 0,49 + 2,99% (fonte secundária; promoção de R$ 0,49 + 1,99% nos 3 primeiros meses).

## Decisão

**Quem vende e quem recebe**

1. **A cobrança é emitida pela subconta do restaurante.** O restaurante é o vendedor e o dinheiro da venda não passa pela conta da AlmoSeis. A **comissão** da AlmoSeis vai por split para a carteira da AlmoSeis (conta-mãe). Recomendação; a alternativa está abaixo.
2. Cada restaurante que liga o pagamento online ganha um **recebedor** (`OnlinePaymentRecipient [T]`): `Provider` (`ASAAS`), `ProviderAccountId`, `WalletId`, `Status` (`NOT_CONFIGURED`, `PENDING_ONBOARDING`, `ACTIVE`, `REJECTED`, `BLOCKED`), datas e motivo. A situação vem **só de webhook ou de consulta ao provedor**, nunca do app.
3. Ligar exige `payments.online.manage` (só o proprietário, ADR-0046). O app coleta os dados que o provedor pede, cria o recebedor e mostra a situação. O KYC (documentos, e-mail de ativação) é feito **com o provedor**, fora do app.
4. A página só oferece "Pagar agora" quando o recebedor está `ACTIVE`, o restaurante ligou a opção, e a janela de horário está configurada (*a confirmar*, ADR-0044). "Pagar na entrega/retirada" continua disponível, salvo se o restaurante desligar.

**Porta de integração**

5. Módulo novo `AlmoSeis.Modules.OnlinePayments` (ADR-0029), com a porta **`IOrderPaymentGateway`**, separada do `ISubscriptionBillingGateway` do Billing (ADR-0017). Operações:
   - recebedor: `CreateRecipient`, `GetRecipient`;
   - cobrança: `CreateCharge` (pedido, valor, método `PIX` ou `CARD`, regras de split, vencimento, referência externa = `OrderId`), `GetCharge`, `CancelCharge`;
   - estorno: `RefundCharge` (sempre o valor integral);
   - webhooks: `ParseWebhook` → eventos normalizados (`CHARGE_PAID`, `CHARGE_EXPIRED`, `CHARGE_REFUNDED`, `REFUND_FAILED`, `SPLIT_SETTLED`, `CHARGEBACK_OPENED`, `RECIPIENT_STATUS_CHANGED`).

   O domínio não conhece nada do Asaas. O adaptador `AsaasOrderPaymentGateway` traduz.
6. **Webhooks:** rota própria `POST /webhooks/order-payments/asaas`, diferente do webhook da assinatura, com token próprio. Cada evento é gravado em `OrderPaymentWebhookEvent` (plataforma, único por id do evento do provedor), respondido com `200` antes de processar e processado de forma assíncrona e idempotente (mesmo padrão do ADR-0017). O tenant é resolvido pela cobrança (`ProviderChargeId` → `OnlineCharge`), com `ITenantContext.Source = GATEWAY_WEBHOOK` (ADR-0045). Cai na política `webhook` do ADR-0041.
7. **Cartão:** a AlmoSeis nunca recebe número de cartão. A página usa o **checkout hospedado do provedor** (link da fatura) e volta para o acompanhamento. Pix: QR code e "copia e cola" mostrados na própria página.

**Segredos por tenant**

8. A `apiKey` de cada subconta é um **segredo por tenant**: fica **no Azure Key Vault**, um segredo por tenant (`order-gateway-apikey-{tenantId}`), nunca no banco, em log, em auditoria ou em resposta da API. O banco guarda só o nome do segredo e os 4 últimos caracteres para conferência. A aplicação lê com identidade gerenciada e mantém em memória por até 10 min.
9. Rotação: o proprietário (ou o admin, ADR-0025) pode pedir rotação. Revogação imediata ao desligar o recebimento online ou ao cancelar o tenant.
10. Quando passar de alguns milhares de tenants (limites de operações do Key Vault), migrar para **criptografia de envelope** no banco (chave mestra no Key Vault). Revisitar neste ADR.

**Fluxo do pedido online**

11. O cliente escolhe "Pagar agora" → o pedido é criado com `AcceptanceStatus = AWAITING_PAYMENT` (ADR-0046) e a cobrança é criada no provedor (`OnlineCharge [T]`: `OrderId`, `ProviderChargeId`, `Method`, `Amount`, `Status`, `ExpiresAt`, `SplitRule` snapshot).
12. **Prazo do pagamento:** Pix com validade de **15 min** (*a confirmar se o Pix do Asaas aceita esse prazo*). Vencido sem pagamento → `EXPIRED`, cobrança cancelada no provedor.
13. Webhook de pagamento confirmado → na mesma transação: `Payment` com `Channel = ONLINE`, `PaymentMethod` `PIX` ou `CARD`, valor = total do pedido, no **expediente aberto**; `IsPaid = true`; aceite pelo sistema (número, cozinha, push, ADR-0046). O pedido fica bloqueado (ADR-0007).
14. **Pagamento que chega depois** (Pix pago no último segundo, expediente fechado, pedido expirado): o pedido **não** vai para a cozinha. O sistema pede o **estorno automático** no provedor e o acompanhamento mostra "Seu pagamento chegou depois do prazo e será devolvido". Auditoria com `Origin = ORDER_PAYMENT_WEBHOOK`.
15. Ao **finalizar o fechamento**, as cobranças `AWAITING_PAYMENT` do expediente são canceladas no provedor e os pedidos vão para `EXPIRED`. Se o cancelamento falhar e o pagamento chegar mesmo assim, vale o item 14.
16. O valor online é sempre o **total** do pedido. Não há pagamento parcial online nem acréscimo de itens depois de pago (ADR-0007). Divisão de conta não se aplica.

**Split e taxas** (valores da plataforma **ilustrativos**; a decisão é do usuário)

17. A regra de split é configurada **na plataforma**, nunca pelo restaurante: `PlatformFeeFixed` e/ou `PlatformFeePercent` (sobre o `netValue`, como o Asaas calcula). Cada `OnlineCharge` guarda o snapshot da regra.
18. Exemplo, pedido de 2 marmitas M (2 × R$ 18,00) + 1 refrigerante (R$ 6,00) + entrega 4,5 km (R$ 3,60) = **R$ 45,60** em Pix:

    | Passo | Comissão 2% do líquido | Comissão fixa R$ 0,99 |
    |---|---:|---:|
    | Valor cobrado do cliente | R$ 45,60 | R$ 45,60 |
    | Taxa Pix do Asaas (R$ 1,08, a confirmar) | − R$ 1,08 | − R$ 1,08 |
    | `netValue` | R$ 44,52 | R$ 44,52 |
    | Comissão AlmoSeis (split) | − R$ 0,89 | − R$ 0,99 |
    | **Restaurante recebe** | **R$ 43,63** | **R$ 43,53** |

    No crédito à vista (R$ 0,49 + 2,99%): taxa ≈ R$ 1,85, `netValue` R$ 43,75, comissão de 2% = R$ 0,88, restaurante R$ 42,87.
19. Nesse desenho, **a taxa do Asaas é absorvida pelo restaurante** (sai do saldo da subconta emissora). Repassar ao cliente (taxa de serviço) ou absorver pela AlmoSeis são decisões de negócio em aberto. Qualquer acréscimo cobrado do cliente teria de aparecer separado no total, como a taxa de entrega.

**Fechamento** (complementa o ADR-0013)

20. Pagamento online **não é dinheiro físico** e nunca entra no dinheiro esperado. O resumo do expediente mostra:

    | Linha | Exemplo |
    |---|---:|
    | Vendido (inclui pedidos do site aceitos) | R$ 3.695,60 |
    | Recebido em dinheiro | R$ 850,00 |
    | Recebido em Pix (manual) | R$ 300,00 |
    | Recebido em cartão (manual) | R$ 200,00 |
    | **Recebido online pela página** (Pix e cartão, valor bruto) | R$ 45,60 |
    | Taxas descontadas no recebimento online (provedor + plataforma), informativo | R$ 1,97 |
    | Dinheiro esperado (inalterado: saldo inicial + dinheiro + suprimentos − retiradas − estornos em dinheiro) | R$ 750,00 |

    `Payment.Amount` é o **valor bruto** (o que abate o saldo do pedido). As taxas ficam em `Payment.GatewayFeeAmount` e `Payment.PlatformFeeAmount`, só informativas: **não são desconto** (ADR-0021) e não mudam o vendido. `DailyClosingLine` ganha a chave de canal (`MANUAL`/`ONLINE`) para cada forma de pagamento.

**Estorno online** (complementa o ADR-0022)

21. Cancelar um pedido pago online continua exigindo **estornar antes** (ADR-0022). `POST /payments/{id}/reverse` (`payments.reverse`, `Idempotency-Key`) num pagamento `ONLINE` chama `RefundCharge` no provedor.
22. O estorno online é assíncrono: o `PaymentReversal` nasce com `Status = PENDING` no expediente aberto, e o `Payment` fica `REVERSAL_PENDING` (continua válido para o saldo). Com o webhook de estorno confirmado → `PaymentReversal.Status = CONFIRMED`, `Payment.Status = REVERSED`, o pedido destrava (ADR-0022, item 4) e pode ser cancelado. Falha (por exemplo, saldo insuficiente na subconta) → `FAILED`, `Payment` volta a `CONFIRMED` e o proprietário recebe alerta.
23. O estorno estorna também a comissão do split (comportamento do Asaas). Se a AlmoSeis deve ou não ficar com alguma parte num estorno é questão em aberto.
24. **Chargeback** (contestação de cartão): registrado como evento do pedido e mostrado ao proprietário. O tratamento financeiro depende da resposta do Asaas sobre chargeback com split (*a confirmar*).

## Alternativas consideradas

- **Cobrança emitida pela conta da AlmoSeis, com repasse ao restaurante:** uma conta só e onboarding mais simples, mas a AlmoSeis passa a receber o valor cheio de vendas de terceiros. Isso a aproxima de **subcredenciadora** (regulação do Banco Central), a torna responsável por chargeback e devolução perante o consumidor e mistura o dinheiro do restaurante no caixa da AlmoSeis. Descartada.
- **Restaurante com conta própria no provedor, sem subconta (só `walletId` no split):** a AlmoSeis não teria a `apiKey` e não poderia emitir a cobrança em nome do restaurante.
- **Gateway único para assinatura e pedidos (`ISubscriptionBillingGateway` estendido):** mistura a cobrança da AlmoSeis (ela é a vendedora) com a cobrança do restaurante (ele é o vendedor). Contratos, webhooks e segredos diferentes.
- **Guardar a `apiKey` cifrada no banco desde o início:** viável, mas exige gestão de chave de envelope. Para dezenas de tenants, o Key Vault direto é mais simples.
- **Checkout de cartão transparente (cartão digitado na página):** amplia o escopo PCI. O checkout hospedado basta.

## Consequências

- Riscos que dependem do Asaas: o limite do período de avaliação (R$ 2.000 por subconta em 60 dias) **acaba em cerca de 44 pedidos de R$ 45** por restaurante, então o piloto do pagamento online precisa ser curto ou feito com a adequação ao BaaS já em andamento.
- O Asaas costuma exigir **CPF/CNPJ do pagador** para emitir cobrança. Isso conflita com "só nome, telefone e endereço" (ADR-0044). *A confirmar com o Asaas*; se for obrigatório, o usuário decide entre pedir o CPF só no "Pagar agora" ou usar um modo de cobrança sem identificação, se existir.
- Mudanças: `Payment` ganha `Channel`, `GatewayFeeAmount`, `PlatformFeeAmount`, `REVERSAL_PENDING`; `PaymentReversal` ganha `Status`; novas entidades `OnlinePaymentRecipient`, `OnlineCharge`, `OrderPaymentWebhookEvent`.
- O app mostra a comissão como informação contratual (venda de bem físico, não de conteúdo digital). Confirmar com as diretrizes das lojas, como no ADR-0017.
- Os termos de uso e o contrato de tratamento (ADR-0024) precisam incluir o pagamento online e o papel do Asaas.

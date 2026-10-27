# ADR-0017 — Cobrança da assinatura por gateway brasileiro, fora do app

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-001 (RN-001.9), F-017, US-018
- **Relacionados:** ADR-0018 (onboarding), ADR-0025 (administração)

## Contexto

O épico define assinatura mensal por estabelecimento com 7 dias grátis, mas não diz como cobrar, o que acontece quando o teste vence ou quando há inadimplência, nem como isso convive com as regras das lojas. Apple (diretriz 3.1.1) e Google Play Billing exigem a compra dentro do app para conteúdo digital desbloqueado no app, com taxa de 15 a 30%. O público paga preferencialmente por Pix ou boleto.

## Decisão

1. **Gateway:** um gateway brasileiro com Pix, boleto, cartão recorrente e webhooks. **Asaas** é a primeira opção. A integração fica atrás de `ISubscriptionBillingGateway` no módulo Billing, para permitir trocar de gateway.
2. **Fora do app:** o app **não tem botão, preço nem link de compra**. Mostra só a situação da assinatura e os dias restantes. O link de pagamento é enviado ao proprietário por e-mail ou WhatsApp, pelo gateway ou pelo backend.
3. **Estados de `Subscription`:**

   | Estado | Acesso | Transição |
   |---|---|---|
   | `TRIALING` | Completo | 7 dias corridos desde a criação do tenant, sem cartão |
   | `ACTIVE` | Completo | Pagamento confirmado por webhook |
   | `PAST_DUE` | Completo + aviso para o proprietário | Fatura vencida; carência de **5 dias** (configurável) |
   | `SUSPENDED` | **Somente leitura**: consultas, histórico e finalização do fechamento em aberto. Bloqueia novos pedidos e pagamentos. | Fim da carência sem pagamento, ou fim do teste sem contratação |
   | `CANCELED` | Sem acesso operacional; exportação disponível | Pedido de cancelamento ou suspensão por mais de 30 dias |

4. O estado vem **sempre** dos webhooks do gateway (validados por assinatura/token e processados de forma idempotente pelo id do evento). Nunca vem do cliente.
5. Um teste grátis por documento (CPF/CNPJ) ou telefone do proprietário.
6. Endpoints de escrita de negócio verificam a situação comercial do tenant por um filtro de autorização (`402 SubscriptionInactive`).

## Alternativas consideradas

- **Compra dentro do app (IAP):** taxa de 15–30%, sem Pix/boleto e com conciliação mais difícil.
- **Cobrança manual no piloto:** não escala e adia o fluxo real que precisa ser validado (métrica de conversão do teste).

## Consequências

- Antes da publicação, é preciso validar com as diretrizes vigentes da App Store e do Google Play que o app não "direciona" para compra externa (anti-steering). Se a Apple exigir IAP no iOS, reabrir este ADR.
- Retenção de dados após o cancelamento está no ADR-0024.
- A suspensão nunca impede a consulta de dados financeiros já registrados.

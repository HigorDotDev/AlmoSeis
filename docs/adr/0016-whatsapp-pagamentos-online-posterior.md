# ADR-0016 — Integração WhatsApp e pagamentos online em épico posterior

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 §15, §16

## Contexto

O atendimento automatizado e os pagamentos online são o diferencial do plano avançado, mas aumentam muito o escopo e as dependências externas.

## Decisão

Ficam fora do EP-001. O EP-001 apenas **prepara o terreno**:

- `Order.Origin` desde o início;
- entidade `Customer` com telefone (ADR-0021);
- regras de pedido, cardápio e pagamento expostas como casos de uso de aplicação, não acopladas aos controllers, para serem reutilizadas por outro canal;
- `Payment` com `PaymentMethod` e `Status` extensíveis para pagamentos confirmados por um provedor.

## Consequências

- Nenhum código de WhatsApp ou de gateway de pagamento de pedidos entra no MVP. O gateway de **assinatura** (ADR-0017) é outra integração e entra.

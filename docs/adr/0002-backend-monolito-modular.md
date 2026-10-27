# ADR-0002 — Backend em monólito modular ASP.NET Core

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 §5.1, §16
- **Refinado por:** ADR-0029 (estrutura concreta dos módulos)

## Contexto

O time é pequeno, o domínio ainda está sendo descoberto e a meta inicial é validar com dez estabelecimentos. Ao mesmo tempo, o EP-002 (WhatsApp, pagamentos online) precisa reaproveitar o mesmo domínio de pedidos e pagamentos.

## Decisão

Um único deploy ASP.NET Core (Web API + SignalR), com o domínio dividido em módulos de fronteira explícita: Identity & Tenancy, Catalog, Menu, Ordering, Payments, Closing, Audit, Notifications, Synchronization e Billing.

## Alternativas consideradas

- **Microsserviços:** custo operacional e de consistência (pedido ↔ pagamento ↔ fechamento) desproporcional ao MVP.
- **Monólito em camadas sem módulos:** mais simples no início, mas as regras de pedido e de pagamento tendem a se misturar, o que atrapalha o EP-002.

## Consequências

- Operações que atravessam módulos (registrar um pagamento atualiza a quitação do pedido) acontecem numa única transação de banco.
- As fronteiras entre módulos são verificadas por testes de arquitetura (ADR-0029), não por rede.

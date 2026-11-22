# ADR-0059 — Delivery por telefone, aba Delivery e cliente pelo telefone

- **Status:** Proposto
- **Data:** 2026-11-22
- **Origem:** EP-003, RF-312 a RF-314; decisões do dono do produto em 2026-11-22 (itens 7 a 9)
- **Relacionados:** ADR-0006, ADR-0007, ADR-0011, ADR-0021 (complementado), ADR-0024 (complementado), ADR-0044, ADR-0046 (complementado), ADR-0055, ADR-0058

## Contexto

Hoje a entrega de marmita no app (`NewMarmitaScreen`) pede o nome do cliente (telefone opcional), oferece os endereços salvos do `Customer`, usa a taxa padrão (editável só com `orders.delivery-fee.edit`) e cria o `Customer` só se o atendente marcar "salvar cliente", gravando também o endereço. A forma prevista e o "troco para" existem só no pedido do site (ADR-0044 item 19).

A lanchonete recebe muitos pedidos por telefone, e o dono do produto quer um fluxo de delivery manual que valha também para a marmita por telefone do restaurante.

## Decisão

**Pedido manual por telefone**

1. Vale para:
   - a lanchonete, na aba **Delivery** (`A_LA_CARTE`, `DELIVERY` ou `PICKUP`, ADR-0058);
   - a **marmita por telefone** do restaurante, na aba Marmitas. Como a aba Marmitas também atende o balcão, a regra é a da Q-312, decidida em 2026-11-22: nome e telefone obrigatórios só na **entrega** de marmita; na retirada, opcionais (balcão).
2. **Nome e telefone obrigatórios** (telefone brasileiro, normalizado em E.164, como no ADR-0044 item 18).
3. **Entrega:** endereço obrigatório (rua, número, complemento, bairro, referência) e **taxa de entrega manual**, digitada no pedido. O campo começa com a taxa padrão e quem cria o pedido pode alterá-lo só na criação, com auditoria; depois, só com `orders.delivery-fee.edit` (Q-315, decidida em 2026-11-22; ADR-0021).
4. **Retirada:** sem endereço e sem taxa.
5. **Forma de pagamento prevista** (`ExpectedPaymentMethod`: dinheiro, Pix ou cartão, dentro das formas habilitadas) e, se dinheiro, **"troco para quanto"** (`ChangeFor`, pelo menos o total), os mesmos campos do pedido do site. Obrigatória na entrega e opcional na retirada (Q-316, decidida em 2026-11-22). Vale também para a marmita.
6. **Entra direto na cozinha e já ganha número**, sem fila de aceite: é um pedido do app (`Origin = APP`), que nasce `ACCEPTED` (ADR-0046 item 1). Não há `Origin` novo para telefone.
7. Envio idempotente (ADR-0011): internet caindo no meio do registro não duplica o pedido.

**Pagamento**

8. A forma prevista **não marca nada como pago** e não cria `Payment`. O pagamento é registrado só na confirmação do recebimento, como hoje nas marmitas (RF-009):
   - **Pix:** o funcionário confere o Pix e registra; o pedido fica pago;
   - **dinheiro:** o motoboy confirma o recebimento e o valor, e o funcionário registra a forma e o valor, com o troco (`CashReceived`, ADR-0021).
9. Pedido entregue e não pago continua com saldo e vira fiado no fechamento (ADR-0021, ADR-0046 item 3).

**Aba Delivery** (lanchonete, ADR-0055)

10. Lista os pedidos `A_LA_CARTE` com `DELIVERY` e `PICKUP` do expediente, **do telefone e do site**, com filtro por atendimento e por status. A faixa **"Novos do site"** da lanchonete fica aqui (ADR-0046 item 21).
11. Os status de atendimento do EP-002 valem (ADR-0046 itens 18 a 20): Pendente → Saiu para entrega → Entregue; Pendente → Retirado; "Pronto para retirar" derivado.
12. O card mostra a forma prevista e o "troco para", para o motoboy levar o troco certo. Exemplo: total R$ 47,00, "troco para R$ 50,00" → levar R$ 3,00 de troco.

**Cliente pelo telefone**

13. O `Customer` do fiado (ADR-0021) é reaproveitado como cadastro simples: **nome e telefone**. O endereço fica **só no pedido**, porque muda. (O cadastro de endereços salvos da marmita continua existindo; ver Q-313.)
14. Ao digitar o telefone, o app procura o `Customer` pelo telefone e **preenche o nome**. No registro, o pedido é **vinculado** ao cliente existente ou **cria** o cliente, sem o atendente precisar marcar "salvar cliente".
15. Telefone já cadastrado com outro nome: *recomendação (Q-314)*: o pedido guarda o nome digitado no snapshot e o cadastro não muda sozinho; o cadastro muda só pela tela de clientes.
16. O cliente do delivery aparece na lista de clientes **sem saldo**. As pendências por cliente mostram só os pedidos com saldo, como hoje.
17. LGPD pelo ADR-0024: o estabelecimento é o controlador; auditoria, log e evento não levam telefone nem endereço; a anonimização sob demanda vale para esses clientes.

## Alternativas consideradas

- **Fila de aceite também para o telefone:** quem atende o telefone já está aceitando. Um passo a mais sem ganho.
- **`Origin = PHONE`:** permitiria separar telefone de balcão nos relatórios, mas não muda regra nenhuma nesta fase. Pode entrar depois sem quebrar contrato, se a métrica for pedida.
- **Salvar o endereço no cadastro automaticamente:** o dono do produto decidiu que o endereço muda e fica no pedido. Cadastros com dez endereços antigos confundem o atendente.
- **Cliente só com "salvar cliente" marcado** (como hoje na marmita): o nome não seria preenchido pelo telefone na próxima ligação.
- **Marcar como pago ao escolher Pix:** o Pix pode não chegar. O pagamento continua manual e confirmado.

## Consequências

- O `CreateOrderCommand` do app ganha `expectedPaymentMethod` e `changeFor` (hoje só no pedido do site), sem marca de "pedido por telefone": na marmita, a entrega já exige nome e telefone (Q-312).
- O `CustomerResolver` ganha o vínculo automático por telefone, sem gravar `CustomerAddress` nesse fluxo.
- A aba Delivery e o formulário de delivery são novos no app; o formulário de marmita ganha a forma prevista e o "troco para".
- Testes: telefone conhecido preenche o nome; pedido por telefone cria o cliente uma vez só (dois atendentes com o mesmo telefone ao mesmo tempo não criam dois clientes); a forma prevista Pix não marca pago.

# ADR-0013 — Fechamento financeiro com conciliação do caixa físico

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 RF-010, §16
- **Refinado por:** ADR-0019 (expediente e caixa)

## Contexto

A divergência no fechamento de caixa é uma dor central do público-alvo.

## Decisão

O fechamento de cada expediente:

- consolida o vendido, recebido e pendente por modalidade e por forma de pagamento;
- calcula o **dinheiro esperado** = saldo inicial + recebimentos em dinheiro (valor aplicado, sem o troco, ADR-0021) + suprimentos − retiradas − estornos em dinheiro;
- recebe o **dinheiro contado**, calcula a diferença e exige justificativa quando a diferença for diferente de zero;
- exige permissão `closing.finalize` e confirmação, e guarda um snapshot imutável dos valores apurados.

Pix e cartão aparecem separados e nunca entram no dinheiro físico.

## Consequências

- O fechamento parcial é uma consulta calculada na hora. Só o fechamento finalizado é persistido como snapshot.

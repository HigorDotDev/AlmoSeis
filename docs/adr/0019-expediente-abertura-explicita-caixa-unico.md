# ADR-0019 — Expediente com abertura explícita e caixa único

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-010, RF-011, §6.4 ("não depender da meia-noite")
- **Relacionados:** ADR-0013, ADR-0014, ADR-0015, ADR-0023
- **Refinado por:** ADR-0039 (vários expedientes por data; o contador de pedidos sai de `BusinessDay.NextOrderNumber` e vai para `BusinessDateCounter`) e ADR-0038 (a abertura materializa os cardápios semanais)

## Contexto

O épico usa `BusinessDate` e "expediente", mas não define como um expediente começa e termina, quantos caixas existem nem o que acontece num expediente que passa da meia-noite.

## Decisão

1. Nova entidade **`BusinessDay`** (expediente/sessão de caixa): `Id`, `TenantId`, `BusinessDate`, `OpenedBy`, `OpenedAt`, `OpeningBalance`, `Status` (`OPEN`, `CLOSED`), `NextOrderNumber`, `Version`.
2. **Abertura explícita:** quem tem `cash.open` abre o expediente informando o **saldo inicial**. `BusinessDate` = data local do estabelecimento (fuso do tenant) no momento da abertura e **não muda** se o expediente passar da meia-noite.
3. **Um expediente aberto por vez** e **um caixa por estabelecimento** no MVP (garantido por índice único parcial `(tenant_id) WHERE status = 'OPEN'`).
4. Pedidos, pagamentos, estornos e movimentações de caixa **exigem um expediente aberto** e herdam seu `BusinessDayId` e `BusinessDate`. Sem expediente aberto, o app oferece abrir (se o usuário tiver permissão) ou explica o bloqueio.
5. Para abrir um novo expediente, o anterior precisa estar fechado. Um fechamento esquecido aparece como pendência ao abrir o app.
6. `DailyClosing` (o snapshot do ADR-0013) pertence a exatamente um `BusinessDay` e é criado na finalização, que muda o `BusinessDay` para `CLOSED`.
7. Cardápios continuam associados a **datas** do calendário. O cardápio usado é o da `BusinessDate` do expediente aberto.

## Alternativas consideradas

- **Corte por horário configurável:** dispensa um passo, mas resolve mal o saldo inicial e expedientes irregulares.
- **Turnos e múltiplos caixas:** fora do MVP. O modelo permite evoluir com `CashRegisterId` sem quebrar `BusinessDay`.

## Consequências

- `CashMovement` e `Payment` passam a referenciar `BusinessDayId`.
- Pagamentos de pedidos antigos (RF-011) caem no expediente aberto no momento.
- Pedidos para datas futuras (encomendas) não são suportados no MVP.

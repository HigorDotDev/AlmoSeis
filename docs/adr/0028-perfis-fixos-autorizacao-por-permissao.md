# ADR-0028 — Perfis fixos no MVP, autorização por permissão

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-001 (perfis), menu "Perfis e permissões"
- **Relacionados:** ADR-0004, ADR-0027

## Contexto

O épico lista 5 perfis de estabelecimento e um item de menu "Perfis e permissões", sem dizer se são configuráveis. Em restaurantes pequenos, uma pessoa costuma acumular funções (atendente + caixa).

## Decisão

1. **O código autoriza sempre por permissão, nunca por perfil.** São constantes em código, por exemplo:
   - `orders.create`, `orders.edit`, `orders.cancel`, `orders.delivery-fee.edit`
   - `production.update`
   - `payments.register`, `payments.reverse`
   - `cash.open`, `cash.movement`, `closing.view`, `closing.finalize`, `closing.adjust`
   - `menu.manage`, `catalog.manage`, `customers.manage`
   - `employees.manage`, `devices.manage`, `settings.manage`
   - `audit.view`, `subscription.view`

   Cada uma vira uma policy do ASP.NET Core.
2. **Perfis fixos** (Proprietário, Gerente, Atendente, Cozinha, Caixa) são **mapas permissão → perfil definidos em código e versionados**. O Proprietário tem todas as permissões e não pode ser removido do último proprietário.
3. Um funcionário pode ter **vários perfis** no mesmo tenant. As permissões efetivas são a união.
4. As permissões **não vão no JWT**. São resolvidas no servidor por `employee_id`, com o mesmo cache do `security_stamp` (ADR-0027). O app recebe a lista em `GET /auth/me` só para mostrar ou esconder telas e abas.
5. A tela "Perfis e permissões" no MVP **mostra** o que cada perfil pode fazer e permite atribuir perfis aos funcionários.

## Alternativas consideradas

- **Perfis customizáveis já no MVP:** mais telas e testes. O modelo atual permite migrar para perfis como dados sem mexer em endpoints.
- **Perfis fixos + exceções por funcionário:** complexidade de interface sem demanda comprovada.

## Consequências

- A matriz perfil × permissão é documentada e coberta por testes.

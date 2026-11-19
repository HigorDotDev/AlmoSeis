# ADR-0049 — Notificação push para pedidos da página

- **Status:** Proposto
- **Data:** 2026-11-19
- **Origem:** EP-002, RF-209; decisão do dono do produto (item 4) em 2026-11-19
- **Relacionados:** ADR-0009, ADR-0024, ADR-0027, ADR-0028, ADR-0031, ADR-0032, ADR-0036 (substituído em parte), ADR-0046

## Contexto

O ADR-0036 deixou os alertas só em primeiro plano, com aparelho fixo na cozinha, e adiou o push para o épico em que os pedidos chegam sem atendente. Esse épico chegou: um pedido do site aguardando aceite expira em minutos (ADR-0046) se ninguém olhar o celular. O dono do produto pediu push com o app fechado, além do som com o app aberto.

## Decisão

1. **Provedor:** Expo Notifications → serviço de push da Expo → **FCM** (Android) e **APNs** (iOS). O backend chama a API de push da Expo. O token de acesso da Expo e as credenciais FCM/APNs ficam no Key Vault e no EAS (ADR-0037).
2. **O que gera push** (só isto no EP-002):

   | Gatilho | Quem recebe | Título | Corpo |
   |---|---|---|---|
   | Pedido do site aguardando aceite | aparelhos com `orders.accept` | "Novo pedido do site" | "2 marmitas · Entrega · aceite em até 10 min" |
   | Lembrete: 3 min sem resposta | os mesmos | "Pedido do site esperando" | "Faltam 7 min para expirar" |
   | Pedido pago online aceito pelo sistema (Fase 2) | aparelhos com `production.view` | "Novo pedido pago #12" | "2 marmitas · Retirada" |

   Mudanças comuns de pedido, alertas da cozinha e pedidos do app **continuam só em primeiro plano** (ADR-0036).
3. **Conteúdo mínimo:** sem nome, telefone ou endereço do cliente, e sem valor. A notificação aparece na tela bloqueada, então leva só o tipo, a quantidade de marmitas e o modo. Quando o usuário tem vínculo com mais de um estabelecimento, o título leva o nome do estabelecimento. O `data` da notificação leva só `tenantId`, `orderId` e `kind`. Tocar abre o pedido; o app busca tudo pela API.
4. **Registro do aparelho:** `PUT /devices/push-token` (autenticado) com `installationId`, `expoPushToken` e `platform`. Fica em `PushRegistration [T]` ligado ao `TenantEmployee` da sessão, ou ao `SharedDevice` quando o login é por PIN (ADR-0027). Um `installationId` tem no máximo um registro ativo, sempre do **tenant ativo** da sessão.
   - Logout, troca de tenant, `AccessRevoked` e desativação apagam o registro.
   - Resposta `DeviceNotRegistered` da Expo apaga o registro.
   - Registro sem uso há 30 dias é apagado.
5. **Destinatários** são resolvidos no envio, pelas permissões **atuais** do funcionário do registro. No aparelho compartilhado, vale o último funcionário que entrou por PIN, mesmo depois do tempo ocioso (o tablet do balcão continua avisando). Push nunca vai para aparelho logado em outro tenant.
6. **Opção por aparelho:** "Avisar pedidos do site com o app fechado", ligada por padrão para quem tem `orders.accept`. A permissão do sistema operacional é pedida **no primeiro uso da página de pedidos**, com uma explicação antes ("Para não perder pedidos do site…"), nunca na abertura do app.
7. **Som:** canal Android `web-orders` com importância alta e um som próprio, curto e insistente (até 5 s). No iOS, o mesmo som no pacote do app. Não usamos alertas críticos do iOS (exigem autorização especial da Apple). O canal comum (`default`) fica para o futuro.
8. **Envio pelo outbox** (ADR-0031): a mensagem de push é gravada na mesma transação do pedido e enviada por um `BackgroundService`. Recibos da Expo são conferidos depois de 15 min. Falha não reenvia indefinidamente (3 tentativas).
9. **Primeiro plano:** com o app aberto, o pedido do site toca o som do canal `web-orders` e mostra a faixa "Novos do site" até alguém aceitar ou recusar. O push é suprimido no aparelho que está com o app em primeiro plano (o app já tratou pelo SignalR).
10. O push é **aviso, não estado**. A fonte continua sendo a API e o feed (ADR-0032). Push que não chegou não perde pedido: o app aberto vê a faixa, e o pedido expira de forma visível.

## Alternativas consideradas

- **Manter só primeiro plano (ADR-0036):** o pedido do site expira com o celular no bolso. Inviável sem atendente.
- **FCM/APNs direto, sem Expo:** um intermediário a menos, mas duas integrações e o gerenciamento de credenciais por plataforma. O app já é Expo (ADR-0035).
- **SMS ou WhatsApp para o restaurante:** custo por mensagem e outra integração.
- **Push para todo pedido e alteração:** ruído, e o funcionário desliga as notificações.
- **Nome do cliente no push:** ajuda a reconhecer o pedido, mas expõe dado pessoal na tela bloqueada (ADR-0024).

## Consequências

- O ADR-0036 continua valendo para alertas comuns. O item 4 dele (push como P1) é substituído por este ADR, só para pedidos da página.
- Nova entidade `PushRegistration [T]`, novo endpoint de registro, novo canal e som no app, e o módulo Notifications ganha o envio pela Expo.
- O onboarding do piloto passa a pedir: permitir notificações e tirar o app da economia de bateria agressiva de alguns Android (Xiaomi, Samsung), que atrasa o push.
- Revisitar se o piloto pedir push para alterações de pedido ou para a cozinha.

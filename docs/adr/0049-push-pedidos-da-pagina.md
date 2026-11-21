# ADR-0049 — Notificação push para pedidos da página

- **Status:** Proposto
- **Data:** 2026-11-19
- **Atualizado:** 2026-11-20, alinhado à implementação da fatia 5 do EP-002 e às correções da revisão (migration 0007, até o commit `2f65bff`). Itens alterados: 2, 4, 6, 8. Ver também "Implementação (2026-11-20)".
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

   *(2026-11-20)* O **lembrete aos 3 min** é uma segunda mensagem do outbox, **agendada** (`outbox_messages.available_at` = envio + `WebOrders:ReminderMinutes`) e gravada na mesma transação do pedido, só se cair antes do prazo. No envio, o pedido precisa ainda estar `AWAITING_ACCEPTANCE` e no prazo. Não existe job que varra pedidos para lembrar: ele teria de marcar o lembrete no pedido, o que subiria a versão e faria o `If-Match` do aceite dar `409`.
3. **Conteúdo mínimo:** sem nome, telefone ou endereço do cliente, e sem valor. A notificação aparece na tela bloqueada, então leva só o tipo, a quantidade de marmitas e o modo. Quando o usuário tem vínculo com mais de um estabelecimento, o título leva o nome do estabelecimento. O `data` da notificação leva só `tenantId`, `orderId` e `kind`. Tocar abre o pedido; o app busca tudo pela API.
4. **Registro do aparelho:** `PUT /devices/push-token` (autenticado) com `installationId`, `expoPushToken` e `platform`. Fica em `PushRegistration [T]` ligado ao `TenantEmployee` da sessão, ou ao `SharedDevice` quando o login é por PIN (ADR-0027). Um `installationId` tem no máximo um registro ativo, sempre do **tenant ativo** da sessão.
   - Logout, troca de tenant, `AccessRevoked` e desativação apagam o registro.
   - Resposta `DeviceNotRegistered` da Expo apaga o registro.
   - Registro sem uso há 30 dias é apagado.
   - *(Detalhado em 2026-11-20:)*
     - **O token é único no sistema inteiro** (`ux_push_registrations_token`): um token, um registro.
     - O mesmo token registrado **pela mesma instalação** em outro tenant (o aparelho trocou de estabelecimento sem o logout chegar ao servidor) é liberado pela função `release_push_token`, sem a sessão ver a linha (ADR-0026 item 10).
     - Registrado por **outra instalação** em outro tenant → `409 PushTokenInUse`, e o registro de lá não é tocado. Quem conhece só a string do token não tira o aviso de outro aparelho. O app não mostra erro e tenta de novo na próxima abertura.
     - O mesmo token em outra instalação **do mesmo** tenant (app reinstalado) sai pela RLS de tenant.
     - `installationId` diferente do da sessão e `platform: WEB` são recusados (`422`).
     - **Logout do aparelho compartilhado mantém o registro.** É o mesmo logout do tempo ocioso, e o tablet do balcão precisa continuar avisando (item 5). **Troca de perfis** do funcionário (`AccessRevoked`) e revogação do aparelho compartilhado apagam o registro.
5. **Destinatários** são resolvidos no envio, pelas permissões **atuais** do funcionário do registro. No aparelho compartilhado, vale o último funcionário que entrou por PIN, mesmo depois do tempo ocioso (o tablet do balcão continua avisando). Push nunca vai para aparelho logado em outro tenant.
6. **Opção por aparelho:** "Avisar pedidos do site com o app fechado", ligada por padrão para quem tem `orders.accept`. A permissão do sistema operacional é pedida **no primeiro uso da página de pedidos**, com uma explicação antes ("Para não perder pedidos do site…"), nunca na abertura do app. *(2026-11-20, interpretação a confirmar, Q-30: o app mostra a explicação, com "Permitir avisos" e "Agora não", em dois pontos e só para quem tem `orders.accept`: na faixa "Novos do site", quando há pedido do site aguardando, e na tela de configuração "Página de pedidos". O pedido do sistema operacional só aparece depois de "Permitir avisos". "Agora não" esconde os dois, e a opção continua em Configurações gerais. "Página de pedidos" foi lida como "os pedidos do site", e não só como a tela de configuração.)*
7. **Som:** canal Android `web-orders` com importância alta e um som próprio, curto e insistente (até 5 s). No iOS, o mesmo som no pacote do app. Não usamos alertas críticos do iOS (exigem autorização especial da Apple). O canal comum (`default`) fica para o futuro.
8. **Envio pelo outbox** (ADR-0031): a mensagem de push é gravada na mesma transação do pedido e enviada por um `BackgroundService`. Recibos da Expo são conferidos depois de 15 min. Falha não reenvia indefinidamente (3 tentativas). *(Detalhado em 2026-11-20, ADR-0029, complemento de 2026-11-20:)*
   - O push é entregue pelo laço de **consumidores externos** do `OutboxDispatcher`. O lote de mensagens é **reservado** numa transação curta, com lease de **120 s** (`available_at`). A tentativa conta na reserva (`attempts + 1`).
   - A chamada à Expo acontece **fora de qualquer transação**. Uma Expo lenta não segura `FOR UPDATE` nem atrasa o tempo real.
   - Os destinatários vão em lotes de 100. O resultado de cada lote é gravado numa transação curta: `push_deliveries` + apagar os `DeviceNotRegistered`.
   - **Deduplicação:** `push_deliveries` (mensagem + registro) é consultada antes do envio. Se a réplica cair e outra pegar a mensagem quando a reserva vencer, quem já recebeu não recebe de novo. Ainda pode repetir se o processo cair entre a resposta da Expo e o commit do lote.
   - Falha da Expo (429, 5xx, rede, tempo) → nova tentativa em 30 s. Na terceira, a mensagem desiste com log de erro.
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

## Implementação (2026-11-20)

- **Sem `eas init`, o app não registra.** Sem o `projectId` do EAS (`extra.eas.projectId`), o app não consegue pedir o token da Expo. Ele avisa uma vez em desenvolvimento e mostra o motivo em Configurações ("Avisos com o app fechado indisponíveis nesta versão do app"). O push de verdade exige `eas init`, credenciais do Firebase/APNs e um build novo. Até lá, o aviso fica só em primeiro plano.
- **Provedor no backend:** `Push:Provider` = `Expo`, `Fake` ou `None`. `Fake` é usado em Development e nos testes e é recusado em Production. O token de acesso da Expo é opcional: sem ele, o envio funciona sem a "enhanced push security".
- **Título com o estabelecimento** (item 3): só quando o funcionário tem mais de um vínculo ativo. No aparelho compartilhado, nunca.
- **Ícone da notificação no Android:** não definido nesta fase (Q-32).
- **Web** (ADR-0053 §7.5): `platform: WEB` é recusado no registro. O app no navegador não usa push nesta fase.

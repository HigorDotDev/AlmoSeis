# ADR-0052 — LGPD do cliente da página: aviso, minimização e retenção

- **Status:** Proposto
- **Data:** 2026-11-19
- **Atualizado:** 2026-11-20, alinhado à implementação das fatias 2 a 5 do EP-002 e às correções da revisão (migration 0007, até o commit `2f65bff`). Itens alterados: 3, 5, 7.
- **Origem:** EP-002, RNF-204; ADR-0024 aplicado a um canal em que o consumidor digita os próprios dados
- **Relacionados:** ADR-0024 (complementado), ADR-0044, ADR-0045, ADR-0047, ADR-0048, ADR-0049, ADR-0050

## Contexto

No app, quem digita os dados do cliente é o funcionário. Na página, é o próprio consumidor, num site com a marca da AlmoSeis no rodapé. O ADR-0024 já define que o restaurante é **controlador** dos dados dos consumidores e a AlmoSeis é **operadora**. Faltam: o aviso na página, o que guardar dos pedidos que não viraram venda, o que fica no navegador e o tratamento por terceiros (mapas, anti-robô, pagamento).

## Decisão

1. **Aviso de privacidade** antes do botão "Enviar pedido": "Seus dados são usados por **{restaurante}** para preparar e entregar o pedido. [Privacidade]". O link abre um texto curto que identifica o restaurante como controlador (nome e contato), a AlmoSeis como operadora, a finalidade (preparar, entregar e contatar sobre o pedido), os terceiros (mapas para calcular a entrega, Cloudflare contra robôs, provedor de pagamento na Fase 2) e como pedir exclusão (falando com o restaurante). Sem caixa de consentimento: a base legal é a execução do pedido. *Texto sujeito a revisão jurídica, como no ADR-0024.*
2. **Minimização:** a página pede só nome, telefone, endereço (só na entrega), observações e a forma de pagamento prevista. Nada de CPF, e-mail ou data de nascimento na Fase 1. O CPF na Fase 2 depende do Asaas (ADR-0050).
3. **Pedidos que não viraram venda** (`REJECTED`, `EXPIRED`, `WITHDRAWN`): nome, telefone, endereço e observações do snapshot são **anonimizados após 7 dias**. Itens, valores e motivo ficam (métricas de recusa). *(Detalhado em 2026-11-20:)*
   - Um job de hora em hora acha os pedidos `WEB` nesses estados há 7 dias ou mais e anonimiza cada um no contexto do tenant, pelo agregado. A versão sobe e o feed leva a mudança aos aparelhos.
   - **Vira marcador:** o nome passa a "Cliente anonimizado". O endereço de entrega passa a ter só a rua "Endereço anonimizado", com número, complemento, bairro e referência nulos. O marcador existe porque o `CHECK` de entrega exige endereço.
   - **Apagados:** telefone, observação do pedido, observações dos itens e "troco para".
   - **Fica:** itens, quantidades, preços, taxa e snapshot da distância, totais, forma de pagamento prevista, datas, `acceptanceStatus`, o código do motivo e o texto da recusa escrito pelo restaurante.
   - Auditoria `WebOrderAnonymized` com `Origin = SYSTEM`, levando só o `acceptanceStatus`.
   - No mesmo job: os tokens de acompanhamento vencidos e os registros de entrada (hash do IP) com mais de 7 dias são apagados.
4. **Pedidos aceitos** seguem o ADR-0024 como qualquer pedido: o restaurante pode anonimizar o cliente, e a retenção após o cancelamento do tenant é a mesma.
5. **Dados técnicos:**
   - hash do IP com sal rotativo (ADR-0045): 7 dias. HMAC com sal diário derivado de um segredo obrigatório fora de Development;
   - `DeliveryQuote` (endereço e coordenadas exatas): apagada 24 h após vencer;
   - cache de geocodificação por tenant (ADR-0048): **7 dias** (era 30). Guarda o **HMAC** do endereço normalizado (segredo `Maps:AddressHashSecret`) e as coordenadas com **4 casas** (cerca de 11 m), nunca o texto. *(Alterado em 2026-11-20, migration 0007, que também apagou as linhas antigas com hash sem segredo e coordenadas exatas.)*
   - token de acompanhamento: 7 dias (ADR-0047). **Nenhum token de acompanhamento fica em claro no banco**: só o SHA-256, e o token é recalculado por HMAC quando precisa ser devolvido de novo;
   - registro de idempotência: 72 h (ADR-0033). O corpo da resposta guardado não inclui telefone nem endereço, e também não inclui o `trackingToken` (ADR-0033 item 7).
6. **Navegador:** a opção **"Lembrar meus dados neste aparelho"** (marcada por padrão) guarda nome, telefone e o último endereço **só no `localStorage`** do navegador, por tenant, para o próximo pedido. Nunca vai para o servidor fora do pedido. Um link "Esquecer meus dados" apaga tudo. Sem cookies de rastreamento.
7. **Logs, auditoria, eventos e push** nunca levam telefone, endereço ou IP em claro (ADR-0024, ADR-0049). O token do Turnstile e o token de acompanhamento não vão para log. *(Detalhado em 2026-11-20:)*
   - A auditoria da recusa (`OrderRejected`) leva **só o `reasonCode`**. O texto livre fica no pedido.
   - A auditoria da configuração da página (`StorefrontUpdated`) não leva telefone de contato, endereço nem coordenadas.
   - Os clientes HTTP dos provedores de mapas e da Expo não fazem log, porque a URL ou o corpo levam o endereço ou o token. O token de push aparece mascarado no log.
   - O `OrderCreated` do pedido do site sai com `label: null`.
8. **Transferência internacional:** o geocodificador e o Turnstile processam dados fora do Brasil. Isso aparece no aviso e no contrato de tratamento. O provedor de mapas recebe só o endereço, nunca nome ou telefone.
9. **Pedido de exclusão pelo consumidor:** feito ao restaurante, que usa a anonimização do ADR-0024. A AlmoSeis, como operadora, encaminha ao restaurante qualquer pedido que receber diretamente.

## Alternativas consideradas

- **Caixa de consentimento obrigatória:** consentimento não é a base adequada para executar um pedido e só adiciona atrito.
- **Guardar os dados do cliente no servidor para preencher o próximo pedido:** exigiria identificar o cliente sem login. O telefone digitado não prova identidade.
- **Manter pedidos recusados com dados pessoais pelo mesmo prazo dos aceitos:** não há finalidade depois de alguns dias.

## Consequências

- O job de retenção do ADR-0024 ganha as rotinas dos itens 3 e 5.
- Os Termos e o contrato de tratamento passam a citar a página, os terceiros e o aviso.
- Revisitar quando o marketplace (Fase 3) trouxer conta de consumidor: aí a AlmoSeis passa a ser controladora dos dados da conta.

# ADR-0044 — Canal web: página pública do restaurante, perfil público e slug

- **Status:** Proposto
- **Data:** 2026-11-19
- **Atualizado:** 2026-11-20, texto alinhado à implementação das fatias 1 a 5 do EP-002 (até o commit `2f65bff`). Ver "Implementação (2026-11-20)".
- **Origem:** EP-002 (`docs/11-epico-ep002.md`), RF-201 a RF-203 e RF-205; decisões do dono do produto em 2026-11-19
- **Relacionados:** ADR-0001 (continua valendo para o restaurante), ADR-0005, ADR-0016 (substituído em parte), ADR-0017, ADR-0019, ADR-0020, ADR-0021, ADR-0038, ADR-0045, ADR-0046, ADR-0048, ADR-0051, ADR-0052

## Contexto

O restaurante recebe pedidos pelo WhatsApp e digita tudo no app. O EP-002 cria uma página pública por restaurante (`dominio/{slug}`), com um único template para todos. O restaurante manda o link, o cliente monta o pedido sozinho e o pedido cai no app.

O ADR-0001 diz que o produto **para o restaurante** é só mobile. A página é para o **consumidor**, então não contradiz o 0001. O ADR-0016 deixou "WhatsApp e pagamento online" para um épico posterior. O EP-002 é esse épico, mas troca o chatbot por uma página web.

Sem uma regra, ficam abertos: o que a página vende, de onde vem o cardápio, quando ela está aberta, o que é público, como o slug nasce e muda, e como o pedido web entra no domínio de pedidos.

## Decisão

**Canal e escopo**

1. Novo valor `Order.Origin = WEB` (o enum fica `APP`, `WEB`; `WHATSAPP` continua futuro). O pedido web é um `Order` comum (ADR-0005), com as mesmas regras de preço, snapshot e auditoria.
2. A página vende **só marmitas e bebidas**, com **entrega ou retirada**:
   - `OrderType = MARMITA`, `FulfillmentType = DELIVERY` ou `PICKUP`;
   - itens: marmitas (tamanho + pratos e acompanhamentos do cardápio do dia + adicionais, ADR-0020) e bebidas avulsas;
   - PF e quilo não aparecem na página.
3. O pedido web precisa de **pelo menos 1 marmita**. Pedido só de bebidas não é aceito pela página (*a confirmar com o usuário*).
4. Limite por pedido: **até 20 marmitas** e até 30 linhas. Acima disso, a página orienta a falar com o restaurante (*a confirmar*). Quantidade por linha de 1 a 20.

**O que a página mostra**

5. Cardápio = o `DAILY` de `MARMITA` da `BusinessDate` do expediente aberto (ADR-0038). Itens com `IsAvailable = false` aparecem **desabilitados** ("Acabou"). Tamanhos ativos de `MarmitaSize`. Bebidas e adicionais ativos com `IsVisibleOnPage = true` (novo campo em `Product`, padrão `true`); os indisponíveis aparecem desabilitados.
6. Estado da página, calculado no servidor a cada requisição:

   | Estado | Condição | O que o cliente vê |
   |---|---|---|
   | `OPEN` | página ativa + expediente aberto + não pausada + dentro da janela de horário (se configurada) + assinatura com escrita liberada | cardápio e botão de pedir |
   | `CLOSED` | sem expediente aberto, ou fora da janela | "Fechado agora" + horário informativo; cardápio não é mostrado |
   | `PAUSED` | `IsAcceptingOrders = false` | "Não estamos recebendo pedidos pela página agora", com o cardápio para consulta e o envio recusado *(2026-11-20)* |
   | `UNAVAILABLE` | página inativa, ou assinatura `SUSPENDED`/`CANCELED` (ADR-0017) | "Página indisponível" (sem dizer o motivo) |

7. **Janela de horário opcional** (`OrderingHours`, por dia da semana). Serve para o caso real do expediente esquecido aberto à noite: sem janela, a página aceitaria pedido às 2h. Na Fase 1 é opcional. Para ligar o pagamento online (Fase 2, ADR-0050) recomendamos torná-la obrigatória (*a confirmar*).
8. **Pausa rápida:** `IsAcceptingOrders` (cozinha lotada) e `IsDeliveryEnabled` (entregador faltou: só retirada). Os dois são chaves no app, de efeito imediato.

**Perfil público** (`StorefrontSettings`, 1:1 com o tenant)

9. Campos visíveis: nome público (padrão `Tenant.Name`), logo (R2, ADR-0037), foto de capa opcional, endereço do restaurante (rua, número, bairro, cidade/UF), telefone/WhatsApp de contato opcional, texto curto de horário (informativo), modos oferecidos (entrega e/ou retirada) e a **regra da taxa** em texto ("R$ 0,80/km, mínimo R$ 3,00, até 6 km", ADR-0048).
10. Nunca são públicos: documento, funcionários, valores do caixa, pedidos de outros clientes, clientes cadastrados.
11. `IsPageActive` liga ou desliga a página inteira. **Recomendação:** a página nasce **inativa** no cadastro, com o slug já reservado. Para ativar, o app exige endereço do restaurante completo e pelo menos 1 tamanho de marmita ativo (*a confirmar se a página deve nascer ativa*). *(2026-11-20: era "endereço confirmado". O ponto confirmado no mapa passou a ser exigido só para a entrega por distância, ADR-0048 item 3.)*

**Slug**

12. Gerado do nome no cadastro (ADR-0018): minúsculas, sem acento, espaços e símbolos viram `-` ("Marmitaria da Dona Zélia" → `marmitaria-da-dona-zelia`).
13. Caracteres: `a-z`, `0-9` e `-`; de 3 a 40 caracteres; não começa nem termina com `-`; sem `--`; não pode ser só número.
14. **Único global**, incluindo os slugs antigos. Em colisão, o gerador tenta `-{cidade}` e depois `-2`, `-3`…
15. **Editável** pelo `storefront.manage` (ADR-0046). O slug antigo continua **redirecionando (301; 308 em POST)** para o novo e fica reservado para o mesmo tenant. Máximo de 5 slugs antigos por tenant. Voltar a um antigo o reativa. Depois que o tenant é anonimizado (ADR-0024, 90 dias após `CANCELED`), os slugs ficam bloqueados por mais **12 meses** antes de voltar a ficar livres, para que um link antigo no WhatsApp não aponte para outro restaurante.
16. **Lista de reservados** (pensando no marketplace na raiz do domínio, Fase 3): `api`, `app`, `admin`, `www`, `web`, `static`, `assets`, `_next`, `cdn`, `img`, `login`, `entrar`, `sair`, `cadastro`, `conta`, `minha-conta`, `restaurantes`, `restaurante`, `cidades`, `cidade`, `categorias`, `perto-de-mim`, `buscar`, `busca`, `pedido`, `pedidos`, `p`, `r`, `s`, `ajuda`, `suporte`, `contato`, `termos`, `privacidade`, `lgpd`, `blog`, `sobre`, `precos`, `planos`, `parceiros`, `almoseis`, `health`, `status`, `webhooks`, `sitemap.xml`, `robots.txt`, `favicon.ico`, `.well-known`. Mais uma lista de palavras ofensivas. A lista vive em código e é coberta por teste.
17. Rotas do marketplace futuro ficam **sob prefixos reservados** (`/restaurantes`, `/cidades/{cidade}`), nunca na raiz. A raiz `/{slug}` é só do restaurante.

**Cliente e dados do pedido**

18. O cliente informa **nome** (2 a 80), **telefone** (celular ou fixo brasileiro, normalizado em E.164) e, na entrega, **endereço** (rua, número, complemento, bairro, cidade, CEP opcional, ponto de referência). Sem login e sem código de confirmação.
19. Na opção "pagar na entrega/retirada", o cliente diz a **forma prevista** (dinheiro, Pix ou cartão, dentro de `EnabledPaymentMethods`) e, se for dinheiro, opcionalmente **"troco para R$ X"**. É só informação para o entregador: não cria `Payment` (o pagamento continua manual, RF-009).
20. **Vínculo com `Customer`** (ADR-0021): acontece **no aceite** (ou na confirmação do pagamento online), não no envio. Se já existe `Customer` com o telefone, o pedido é vinculado **sem alterar** os dados dele. Se não existe, é criado. O endereço informado fica só no snapshot do pedido e não vira `CustomerAddress` automaticamente. O app mostra "telefone não verificado" nos pedidos do site, porque qualquer pessoa pode digitar o telefone de outra.
21. O servidor **recalcula tudo**. O navegador manda `expectedTotal`. Se o preço mudou ou um item acabou entre a abertura da página e o envio: `409 CartChanged` com o carrinho recalculado, e o cliente confirma de novo.

## Alternativas consideradas

- **Chatbot de WhatsApp (EP-001 §15):** depende da API oficial do WhatsApp (custo por conversa, aprovação de templates) e de fluxo conversacional. O dono do produto escolheu a página: o cliente monta sozinho e o link é enviado pelo próprio WhatsApp do restaurante.
- **Subdomínio por restaurante (`zelia.dominio`):** certificado curinga e DNS mais simples de explicar, mas complica o marketplace e o Open Graph não ganha nada. O caminho `/{slug}` é mais simples.
- **Slug imutável:** evita redirecionamento, mas um erro de digitação no cadastro ficaria para sempre.
- **Página vendendo PF e quilo:** PF é consumo no salão e quilo depende de balança. Fora do escopo por decisão do usuário.
- **Atualizar `Customer` com os dados digitados:** permitiria que um estranho alterasse o cadastro de outro cliente pelo telefone.

## Consequências

- `Order` ganha `Origin = WEB`, `CreatedByEmployeeId` passa a aceitar nulo (pedido web), e ganha `ExpectedPaymentMethod`, `ChangeFor` e o estado de aceite (ADR-0046).
- Novas entidades: `StorefrontSettings [T]` e `StoreSlug` (plataforma, ADR-0045). `Product` ganha `IsVisibleOnPage`.
- O app ganha a tela "Página de pedidos" (configuração, slug, compartilhar link e QR code) e o selo "Site" nos pedidos.
- O ADR-0001 continua valendo: o restaurante segue sem dashboard web. A página não tem área do restaurante.
- Revisitar os limites (20 marmitas, pedido só de bebidas) depois do piloto.

## Implementação (2026-11-20)

O que a implementação decidiu onde o texto acima deixava espaço, e o que ficou fora desta fase. Contrato em `docs/06-contratos-api.md` §6.18 e §6.19.

- **Página nasce inativa** (item 11, Q-12): o autocadastro cria `storefront_settings` inativa e reserva o slug na mesma transação (`ITenantCreatedListener`). Para ativar (`isPageActive: true`), o `PUT /storefront` exige:
  - endereço **completo**: rua, número, bairro, cidade e UF;
  - pelo menos **1 tamanho de marmita ativo**.

  O ponto confirmado no mapa **não** é exigido para ativar. Ele só é exigido para a entrega por distância aparecer (ADR-0048 item 3, `pageDelivery.missing`).
- **Estados** (item 6), nesta ordem: `UNAVAILABLE`, `CLOSED`, `PAUSED`, `OPEN`.
  - `PAUSED` **mostra o cardápio** para consulta e recusa o envio e a cotação com `409 StoreUnavailable` (extensão `storeStatus`, sem o motivo).
  - `UNAVAILABLE` devolve só `slug`, `status`, `name` e `limits`.
  - `CLOSED` não mostra cardápio.
- **Janela de horário** (item 7): **fora desta fase**. O estado não considera `OrderingHours`, e `CLOSED` é só "sem expediente aberto". O texto livre de horário (`hoursText`) é informativo. A Q-18 continua aberta para a Fase 2.
- **Perfil público** (item 9): **logo e foto de capa ficaram fora desta fase** e não estão no contrato (Q-33). O resto do item 9 foi implementado: nome, endereço, contato, horário, modos e regra da taxa (`deliveryRule`).
  - As **formas de pagamento habilitadas não aparecem** no perfil público. Uma forma desabilitada só é recusada no envio (`422 PaymentMethodDisabled`, Q-34).
- **`isVisibleOnPage`** (item 5): vale só para **bebidas e adicionais**. Pratos e acompanhamentos seguem o cardápio do dia. Nasce `true` e muda pelo `PATCH` do produto.
- **Slug** (item 15):
  - até **5 slugs antigos** por tenant, ou seja, 5 trocas. Depois disso, só pelo suporte;
  - **voltar a um slug antigo** do próprio tenant o reativa e não conta como troca nova;
  - slug antigo ou com maiúsculas: `301` em GET e `308` em POST (o `308` preserva método e corpo);
  - a quarentena de 12 meses (`blocked_until`) existe no schema e será preenchida pelo job de retenção do ADR-0024, quando ele existir.
- **Endereço do cliente** (item 18): o pedido guarda rua, número, complemento, bairro e referência. A **cidade não entra** no `deliveryAddress` do pedido: ela só serve à cotação (ADR-0048).
- **Recálculo** (item 21): o carrinho é conferido duas vezes.
  - Antes de gravar, contra o cardápio público, o que devolve `409 CartChanged` com o carrinho recalculado.
  - Depois de gravar, o total, os itens e a taxa do pedido gravado precisam bater com o carrinho conferido. Senão, também `409 CartChanged`, e nada é gravado.

## Complementado por ADR-0054, ADR-0057 e ADR-0058 (2026-11-22)

O EP-003 muda **o que a página vende** (itens 2, 3, 5 e 11). O resto deste ADR continua valendo.

- **O que a página vende depende dos módulos do tenant** (ADR-0054):
  - restaurante (`MARMITA`): marmitas e bebidas, como no item 2, agora com o **preço de site** dos tamanhos de marmita (ADR-0057). Bebidas: ver EP-003 Q-307;
  - lanchonete (`FIXED_MENU` + `DELIVERY`): uma aba por **categoria** do cardápio fixo, mais **Bebidas** e **Combos**, só com itens "vende no site" e **não pausados**, pelo preço de site. O pedido é `A_LA_CARTE` com `DELIVERY` ou `PICKUP` (ADR-0058);
  - com os dois comportamentos: as duas coisas. Um pedido do site é de um tipo só (EP-003 Q-317).
- Itens pausados do cardápio fixo **não aparecem** na página (no item 5, os indisponíveis do cardápio do dia aparecem desabilitados; isso continua para a marmita).
- Item 1: o pedido web pode ser `MARMITA` ou `A_LA_CARTE`.
- Item 3 (pelo menos 1 marmita) e item 4 (até 20 marmitas): na lanchonete, valem a regra e o limite da EP-003 Q-318.
- Item 11 (ativação): na lanchonete, ativar exige o endereço completo e pelo menos **1 item vendável no site** (prato, tamanho, bebida ou combo), no lugar do tamanho de marmita ativo.
- Item 21: o recálculo usa o preço de site.

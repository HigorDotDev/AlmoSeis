# AlmoSeis — página pública de pedidos (`web/`)

Página do restaurante para o consumidor (EP-002 fatia 3: F-203, F-204, F-208, F-211, F-212). Next.js (App Router) +
TypeScript, renderizada no servidor para a prévia do link no WhatsApp (ADR-0051). Decisões: ADR-0044, 0045, 0046,
0047, 0051 e 0052 (todos `Proposto`). Contrato: `docs/06-contratos-api.md` §6.19.

## Rotas

| Rota | O que faz |
|---|---|
| `/{slug}` | Perfil, estado (`OPEN`/`CLOSED`/`PAUSED`/`UNAVAILABLE`) e cardápio do dia; monta marmitas e bebidas. Renderizada no servidor com Open Graph ("Peça na {nome}", cardápio do dia, imagem quando houver). Slug antigo → redireciona para o atual |
| `/{slug}/carrinho` | Carrinho, dados do cliente, retirada ou entrega (endereço + cotação da taxa por distância), forma de pagamento, aviso de privacidade, Turnstile e envio |
| `/{slug}/pedido/{token}` | Acompanhamento (ADR-0047): status, linha do tempo, itens, totais; consulta a cada 20 s com `If-None-Match`; "Desistir do pedido". `noindex`, `no-store`, `no-referrer` |

Leituras na renderização: servidor da página → API, com `X-Web-Renderer-Key`. Envio, desistência e acompanhamento:
navegador → API direto (CORS `Cors:PublicPageOrigins`), para os limites por IP verem o IP do cliente.
Carrinho, dados do cliente e links de acompanhamento ficam só no `localStorage` (ADR-0052); "Esquecer meus dados" no
rodapé apaga tudo.

## Como rodar

Pré-requisitos: Node 20.9+ (testado com 24) e a API rodando em `http://localhost:5080` (`docker compose up` na raiz +
`dotnet run` da API; ver `docs/09-devops.md`).

```bash
cd web
cp .env.example .env.local   # ajuste se precisar
npm install
npm run dev                  # http://localhost:3000/{slug}
```

Para a página aparecer aberta, o restaurante precisa de: página ativa com endereço (app → "Página de pedidos"),
expediente aberto e cardápio do dia de marmita publicado.

| Script | O que faz |
|---|---|
| `npm run dev` | servidor de desenvolvimento na porta 3000 |
| `npm run build` / `npm start` | build de produção / servidor de produção na porta 3000 |
| `npm run typecheck` | `tsc --noEmit` |
| `npm test` | testes unitários (Vitest): dinheiro, prévia do carrinho, corpo do pedido, erros do envio, entrega (regra, cotação, validade, erros), cliente da API, máscara do telefone, Open Graph |
| `npm run test:smoke` | teste de fumaça: roda `next build`, confere as rotas geradas, que o JS do navegador chama `/orders`, `/delivery-quotes` e `/withdraw` e que não leva o `X-Web-Renderer-Key` (~1 min) |

## Variáveis de ambiente

| Variável | Onde | Uso |
|---|---|---|
| `NEXT_PUBLIC_API_BASE_URL` | navegador e servidor | URL da API. Padrão `http://localhost:5080` |
| `API_BASE_URL` | servidor | URL da API vista pelo servidor da página (opcional; padrão = a anterior) |
| `WEB_RENDERER_KEY` | servidor | Igual a `PublicPage:RendererKey` da API (header `X-Web-Renderer-Key`, isenta a renderização do limite por IP). Vazio em dev (a API também está vazia). Segredo: Key Vault em homologação/produção |
| `NEXT_PUBLIC_TURNSTILE_SITE_KEY` | navegador | Site key do Cloudflare Turnstile. Dev: `1x00000000000000000000AA` (chave de teste oficial que sempre passa, par da chave secreta de teste da API). Vazio = sem widget |
| `NEXT_PUBLIC_SITE_URL` | servidor | Endereço público da página (canonical e Open Graph). Padrão `http://localhost:3000` |

`.env.example` é versionado; `.env.local` não (está no `.gitignore`).

## Estrutura

```
src/
  app/
    layout.tsx, globals.css, page.tsx, not-found.tsx, robots.ts, icon.svg
    [slug]/page.tsx                 perfil + cardápio (SSR, Open Graph)
    [slug]/carrinho/page.tsx        carrinho e envio
    [slug]/pedido/[token]/page.tsx  acompanhamento
  components/                       StoreClient, MarmitaBuilder, CheckoutClient, TrackingClient, Turnstile, PrivacyNotice…
  lib/
    api/client.ts   cliente das rotas públicas (sem Next; usado no servidor e no navegador)
    api/server.ts   cliente do servidor (X-Web-Renderer-Key, sem seguir o 301)
    api/types.ts    tipos do §6.19
    money.ts        dinheiro em centavos (ADR-0030)
    cart.ts         carrinho, prévia (ADR-0020), validação, CartChanged
    checkout.ts     corpo do POST /orders e interpretação dos erros
    delivery.ts     entrega: regra da taxa, endereço, validade da cotação, erros da cotação (ADR-0048)
    storage.ts      localStorage/sessionStorage (ADR-0052)
    idempotency.ts  Idempotency-Key por tentativa
tests/build.smoke.test.ts
```

## Comportamentos que valem conferir

- **Idempotência:** a `Idempotency-Key` nasce no primeiro toque em "Enviar pedido" e é reaproveitada enquanto o corpo
  for o mesmo (guardada no `sessionStorage`, sobrevive a recarregar). Corpo mudou (cliente editou, aceitou o
  `CartChanged` ou a taxa nova do `DeliveryQuoteChanged`) → chave nova. O token do Turnstile é de uso único: cada
  cotação e cada envio consome um e a página já pede o próximo.
- **Erros:** `409 CartChanged` mostra o carrinho recalculado e pede "Aceitar as mudanças"; `409 StoreUnavailable`
  recarrega o estado; `403 CaptchaFailed`; `429 TooManyPendingOrders`; `429` com `Retry-After` desabilita o botão
  com contagem; `422 ValidationFailed` aparece no campo (inclusive `items[i].*` na linha do carrinho).
- **Entrega (fatia 4, ADR-0048):** aparece quando o perfil traz `deliveryAvailable: true`, com a regra
  ("R$ 0,80 por km, mínimo R$ 3,00, até 6 km" ou "Taxa fixa de R$ 5,00"). O cliente preenche rua, número,
  complemento, bairro, cidade (opcional; vazia = a do restaurante), CEP (opcional) e referência e toca em "Calcular
  taxa de entrega" (`POST /delivery-quotes`, com token novo do Turnstile). A página mostra "Encontramos: …" e
  "4,5 km · Entrega R$ 3,60", e o total passa a incluir a taxa. Mudar rua, número, bairro, cidade ou CEP, ou
  passar dos 15 min da cotação (com 30 s de margem), pede uma cotação nova antes de enviar; complemento e referência
  não. Erros: 422 em `address.*` vai para o campo; `OutOfDeliveryArea` ("Fora da área de entrega", com
  distância e raio) e `DeliveryUnavailable` oferecem "Retirar no local"; 429 desabilita o botão com contagem.
  No envio, `409 DeliveryQuoteChanged` mostra a taxa nova e o novo total; "Confirmar e enviar" reenvia com
  `quote.quoteId` e chave nova assim que houver token. O acompanhamento mostra a taxa e a distância.
- **Metas (RNF-201):** JavaScript inicial de `/{slug}` medido no build de produção: ~143 KB gzip para navegadores
  modernos (+39 KB do polyfill `nomodule`, que eles não baixam). Sem fontes externas; o único terceiro é o
  Turnstile, carregado só em `/{slug}/carrinho` e só quando o perfil declara `ANTI_BOT`.

## Pendências conhecidas

- `packages/shared` (ADR-0051 item 3) ainda não existe: tipos, dinheiro e prévia estão em `src/lib` prontos para mudar.
- TanStack Query e React Hook Form + Zod (ADR-0051 item 4) não foram usados, para manter o JS leve; hooks e validação
  próprios.
- Workflow `web.yml`, Dockerfile e Container App (`almoseis-web`) ficam com o `devops-agent`.

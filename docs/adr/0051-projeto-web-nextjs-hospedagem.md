# ADR-0051 — Projeto web da página: Next.js em `web/`, Azure Container Apps e Cloudflare

- **Status:** Proposto
- **Data:** 2026-11-19
- **Atualizado:** 2026-11-20, alinhado à implementação de `web/` nas fatias 2 a 5 do EP-002 (até o commit `2f65bff`). Itens alterados: 1, 3, 4. Ver também "Implementação (2026-11-20)".
- **Origem:** EP-002, RNF-201 a RNF-205; a página é o primeiro front-end web do produto
- **Relacionados:** ADR-0001 (continua valendo para o restaurante), ADR-0030, ADR-0035, ADR-0037, ADR-0044, ADR-0045, ADR-0047

## Contexto

A página é aberta a partir de um link no WhatsApp, quase sempre num Android de entrada com 4G instável. Três necessidades puxam a escolha da tecnologia:

- **prévia do link no WhatsApp** (Open Graph): o robô do WhatsApp não executa JavaScript, então título, descrição e imagem precisam vir no HTML do servidor;
- **desempenho** no primeiro carregamento, sem app instalado;
- **SEO** para o marketplace da Fase 3 (páginas de restaurante e de cidade indexáveis).

O repositório já tem `backend/` (.NET), `mobile/` (Expo, TypeScript) e `infra/` (Bicep). Regras de preço calculadas no app e na página precisam bater com o servidor (ADR-0030).

## Decisão

1. **Next.js (App Router) + TypeScript**, na pasta **`web/`** do monorepo. *(2026-11-20: Next.js 16 com React 19.)*
   - `/{slug}`: renderizada **no servidor**, com `og:title` (nome do restaurante), `og:description` ("Peça sua marmita: entrega e retirada"), `og:image` (capa ou logo do R2, 1200 × 630) e `canonical`. Revalidação de **30 s** para o perfil. O estado aberto/fechado e o cardápio são buscados de novo no navegador ao abrir.
     - *(Alterado em 2026-11-20:)* **sem revalidação de 30 s no servidor**. A página é renderizada a cada requisição, sem cache, com o perfil da API (`Cache-Control: no-cache`). No navegador, os dados são buscados de novo a cada 30 s enquanto a aba está visível. Pausa e "Acabou" aparecem em até 30 s, como pede o RF-202.
     - **`og:image` usa a foto do primeiro prato disponível do dia.** O perfil público ainda não tem logo nem capa (ADR-0044 "Implementação", Q-33).
   - `/{slug}/pedido/{token}`: renderizada no servidor sem cache e sem indexação (ADR-0047). *(2026-11-20: o servidor renderiza só a casca; os dados vêm do navegador, ADR-0047 item 8.)*
   - Slug antigo: o servidor da página redireciona para o atual (ADR-0044) com **`308`** (`permanentRedirect` do Next.js; era `301`).
2. **Chamadas à API:**
   - leituras na renderização: servidor da página → API, com `X-Web-Renderer-Key` (isento do limite por IP, ADR-0045);
   - gravações (cotação, pedido, desistência) e consultas do acompanhamento: **navegador → API direto**, com CORS restrito à origem da página. Assim o limite por IP vê o IP real do cliente.
3. **Código compartilhado:** pacote `packages/shared` (TypeScript puro, sem React Native nem Next) com os tipos da API pública, os esquemas Zod, a aritmética de dinheiro em centavos inteiros (ADR-0030) e a prévia do preço da marmita (tamanho + adicionais, ADR-0020). App e página importam do mesmo pacote. **O valor oficial continua sendo o do servidor.**
   - *(2026-11-20: **`packages/shared` não foi criado.**)* Os tipos da API pública, o dinheiro em centavos, o preço do carrinho, o telefone e a chave de idempotência ficam em `web/src/lib`, sem dependência de React nem de Next, prontos para mover. O app continua com o código dele. Criar o pacote, e fazer o app importar dele, fica para quando houver um segundo consumidor real.
4. Mesma família de bibliotecas do app quando fizer sentido: TanStack Query, React Hook Form + Zod. Sem biblioteca de componentes pesada. O design segue `docs/10-ux.md` (o `ux-agent` define as telas da página).
   - *(2026-11-20:)* **TanStack Query, React Hook Form e Zod não foram usados.** As dependências de produção são só `next`, `react`, `react-dom` e `server-only`, para manter o bundle leve (item 5). Os poucos fetches usam um cliente próprio e hooks simples, e a validação é feita à mão. O servidor revalida tudo.
5. **Metas** (RNF-201): JavaScript inicial ≤ 150 KB comprimido na rota `/{slug}`; LCP ≤ 2,5 s em 4G num Android de entrada; funciona sem fontes externas; acessibilidade WCAG 2.1 AA.
6. **Hospedagem:** um segundo Container App, **`almoseis-web`**, no mesmo Container Apps Environment da API (Brazil South, ADR-0037), imagem no ghcr.io, **mínimo de 1 réplica** (o robô do WhatsApp desiste se a primeira resposta demorar, então não usamos scale-to-zero).
7. **Cloudflare na frente** da página (DNS e CDN dos arquivos estáticos `/_next/static`, com cache longo), aproveitando a conta já usada para o R2 e o Turnstile. O HTML não é cacheado na borda.
8. **Domínio:** lacuna. Estrutura proposta: `{dominio}` para a página e o marketplace, `api.{dominio}` para a API (já previsto em `docs/09-devops.md`) e `homolog.{dominio}` para homologação.
9. **CI/CD:** workflow `web.yml` no GitHub Actions: lint, tipos, testes, build da imagem, deploy em homologação automático e em produção por aprovação (mesmo fluxo da API). Mudanças em `packages/shared` disparam os testes do app e da página.
10. **Sem rastreadores de terceiros** na página. Métricas de uso só no servidor (OpenTelemetry → Application Insights), sem dados pessoais (ADR-0052).

## Alternativas consideradas

- **Página estática (SPA com Vite):** mais simples de hospedar, mas sem HTML no servidor a prévia do WhatsApp sai vazia e o SEO do marketplace sofre.
- **Expo Router para web (reaproveitar o app):** reaproveita componentes, mas o pacote do React Native Web é pesado para Android de entrada, a renderização no servidor ainda é limitada, e a página do consumidor tem outro desenho.
- **Razor Pages / Blazor no próprio backend:** um deploy a menos, mas outra pilha de front-end, sem compartilhar tipos e regras com o app em TypeScript.
- **Vercel:** a melhor experiência para Next.js, mas é mais um fornecedor, com faturamento em dólar e a renderização fora da região escolhida no ADR-0037.
- **Azure Static Web Apps:** o suporte a Next.js com renderização no servidor é limitado.
- **Cloudflare Workers (OpenNext):** barato e rápido na borda, mas adiciona outro ambiente de execução e afasta a página da observabilidade da Azure.

## Consequências

- O monorepo ganha `web/` e `packages/shared`. O app passa a importar os tipos e o dinheiro do pacote compartilhado (trabalho do `mobile-agent`).
- Custo fixo novo: 1 réplica pequena (0,25 vCPU / 0,5 GiB) no Container Apps.
- `docs/09-devops.md` ganha o Container App da página, o workflow `web.yml`, o DNS no Cloudflare e os segredos (`X-Web-Renderer-Key`, Turnstile).
- O ADR-0001 continua valendo: a página não tem área do restaurante. Um dashboard web para restaurantes continua fora do escopo.
- Este projeto é a base da Fase 3 (marketplace). Revisitar a hospedagem quando o marketplace precisar de busca por localização.

## Implementação (2026-11-20)

- **Dados do navegador por slug** (ADR-0052 item 6): carrinho, dados do cliente ("Lembrar meus dados") e pedidos recentes ficam no `localStorage`, com o prefixo `almoseis:` e uma chave por restaurante (slug). Os pedidos recentes são até 10 por restaurante, dentro da validade do acompanhamento. A tentativa de envio em andamento (`Idempotency-Key` + corpo) fica no `sessionStorage`, para o reenvio usar a mesma chave mesmo depois de recarregar a página. "Esquecer meus dados" apaga tudo com o prefixo, de todos os restaurantes. Nada vai para o servidor fora do envio.
- **Rotas:** `/{slug}` (perfil e cardápio), `/{slug}/carrinho` (carrinho, entrega e envio) e `/{slug}/pedido/{token}` (acompanhamento). A raiz `/` é só um aviso sem indexação, reservada ao marketplace (ADR-0044 item 17).
- **Hospedagem, Cloudflare, CI/CD e domínio** (itens 6 a 9): ainda não implementados. Não há Container App, Dockerfile nem workflow de `web/`. Continuam como decisão, e a Q-07 segue aberta.

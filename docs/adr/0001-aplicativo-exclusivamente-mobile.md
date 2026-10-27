# ADR-0001 — Aplicativo exclusivamente mobile para os restaurantes

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 §16
- **Relacionados:** ADR-0025 (administração da plataforma sem UI web)

## Contexto

O público-alvo (pequenos restaurantes e marmitarias) hoje trabalha com comandas de papel, WhatsApp e anotações. Em geral esses estabelecimentos não têm computador no balcão nem na cozinha, mas todos os funcionários têm celular Android, muitas vezes de entrada.

## Decisão

O produto para o estabelecimento é **somente o aplicativo mobile (Android e iOS)**, feito com React Native + Expo. Não existe dashboard web para restaurantes no MVP.

## Alternativas consideradas

- **Web responsivo (PWA):** dispensa as lojas, mas o armazenamento local (rascunhos) e o funcionamento em segundo plano ficam piores, e a experiência em aparelhos de entrada é inferior.
- **App + dashboard web:** dobra o esforço de front-end. Ficou como evolução fora do MVP.

## Consequências

- Toda tela é pensada para toque, uso com uma mão e telas pequenas (RNF-001).
- Relatórios extensos (fechamento mensal) precisam caber no celular: paginação e resumos.
- O app depende da aprovação nas lojas, então a cobrança precisa respeitar as regras de compra dentro do app (ADR-0017).

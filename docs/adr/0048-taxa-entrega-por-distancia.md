# ADR-0048 — Taxa de entrega por distância de rota

- **Status:** Proposto
- **Data:** 2026-11-19
- **Atualizado:** 2026-11-20, alinhado à implementação da fatia 4 do EP-002 e às correções da revisão (migration 0007, até o commit `2f65bff`). Itens alterados: 8, 10, 11, 12, 13, 15. Ver também "Implementação (2026-11-20)".
- **Origem:** EP-002, RF-204; decisão do dono do produto (item 3) em 2026-11-19
- **Relacionados:** ADR-0021 (refinado), ADR-0030, ADR-0037, ADR-0044, ADR-0045, ADR-0052

## Contexto

Hoje a taxa de entrega é um valor padrão do estabelecimento, editável no pedido com `orders.delivery-fee.edit` (ADR-0021). Na página não há atendente para ajustar a taxa: um valor fixo cobra demais de quem mora perto e de menos de quem mora longe.

O dono do produto decidiu: traçar a rota do restaurante ao endereço de entrega, multiplicar a distância pelo valor por km do restaurante e aplicar uma taxa mínima. Exemplo dele: 4,5 km × R$ 0,80/km = R$ 3,60, com mínimo de R$ 3,00.

Levantamento de 2026-11-19 (*a confirmar com o Google*): Routes API (Compute Route Matrix, Essentials) com 10.000 elementos grátis por mês e depois ≈ US$ 5 por 1.000; com trânsito (`TRAFFIC_AWARE`) vira Pro e o preço dobra. Geocoding com 10.000 grátis por mês e depois ≈ US$ 5 por 1.000. Um pedido de entrega custa cerca de 1 geocodificação + 1 elemento de rota. No piloto (10 restaurantes), fica dentro da faixa grátis. Mapbox tem ≈ 100.000 requisições grátis por mês (fonte secundária). OSRM ou OpenRouteService com dados do OpenStreetMap são gratuitos se auto-hospedados. Os termos do Google sobre exibir resultados sem um mapa do Google e sobre cache precisam ser verificados antes da escolha.

## Decisão

**Configuração** (`StorefrontSettings`, `storefront.manage`)

1. `PageDeliveryFeeMode`: `DISTANCE` (padrão, decisão do usuário) ou `FIXED` (usa o `DefaultDeliveryFee` do ADR-0021 e não consulta mapa).
2. `PricePerKm` (`numeric(10,2)`, > 0), `MinimumDeliveryFee` (Money, ≥ 0) e `MaxDeliveryDistanceKm` (`numeric(4,1)`, opcional; *raio máximo a confirmar com o usuário*).
3. **Localização do restaurante:** endereço completo nas configurações, geocodificado **uma vez** ao salvar. O app mostra o ponto no mapa do aparelho e o proprietário confirma ou arrasta o marcador. Guarda `Latitude`/`Longitude` (`numeric(9,6)`). Mudar o endereço exige nova confirmação. Sem localização confirmada, o modo `DISTANCE` não pode ser ligado.

**Cálculo** (sempre no servidor; o navegador nunca envia distância nem taxa)

4. Distância = **rota de carro, sem trânsito**, do ponto do restaurante ao ponto do endereço do cliente.
5. `km = round(distanceMeters / 1000, 1, AwayFromZero)`: **uma casa decimal**, porque é o que o cliente vê ("4,5 km") e a conta precisa bater com o que ele vê.
6. `taxa = max(round(km × PricePerKm, 2, AwayFromZero), MinimumDeliveryFee)` (mesma regra de arredondamento do ADR-0030).
7. Raio: se `MaxDeliveryDistanceKm` estiver definido e `km > MaxDeliveryDistanceKm` → `422 OutOfDeliveryArea` ("Não entregamos nesse endereço. Você pode retirar no local."). A comparação usa o `km` já arredondado, o mesmo que o cliente vê.

   | Distância da rota | km | km × R$ 0,80 | Mínimo R$ 3,00 | Taxa | Raio 6,0 km |
   |---|---:|---:|---:|---:|---|
   | 4.480 m | 4,5 | 3,60 | — | **R$ 3,60** | entrega |
   | 2.130 m | 2,1 | 1,68 | aplica | **R$ 3,00** | entrega |
   | 6.040 m | 6,0 | 4,80 | — | **R$ 4,80** | entrega |
   | 6.050 m | 6,1 | 4,88 | — | — | **fora da área** |

   Com R$ 0,85/km e 4,5 km: 3,825 → **R$ 3,83**.

**Cotação**

8. `POST /public/stores/{slug}/delivery-quotes` com o endereço → `quoteId`, `formattedAddress` (o endereço como o geocodificador entendeu), `distanceKm` (`"4.5"`), `deliveryFee` (`"3.60"`) e `expiresAt` (**15 min**). Fica em `DeliveryQuote [T]` com o hash do endereço normalizado, coordenadas, metros, taxa e a versão das configurações. *(2026-11-20: o hash é **HMAC-SHA-256** com o segredo `Maps:AddressHashSecret`, sobre tenant + endereço normalizado. Sem o segredo, qualquer um com a lista de endereços da cidade recalcularia o hash. O segredo é obrigatório fora de Development, com 32 caracteres ou mais. Trocá-lo invalida o cache e as cotações em aberto, e o envio recalcula.)*
9. A página mostra "Encontramos: Rua das Flores, 123 – Centro" e o cliente confirma. Se o geocodificador devolver um resultado aproximado (só rua, só bairro ou só cidade), a página pede para revisar o número ou o bairro. Resultado aproximado nunca vira taxa.
10. O pedido referencia o `quoteId`. Na criação, o servidor confere: cotação válida, do mesmo tenant e com o mesmo hash de endereço. Se venceu, recalcula. Se a taxa mudou (o restaurante mudou o preço por km, por exemplo) → `409 DeliveryQuoteChanged` com a nova taxa, e o cliente confirma de novo. **O cliente nunca paga uma taxa que não viu.** *(Detalhado em 2026-11-20:)*
    - O recálculo acontece com cotação vencida, endereço diferente (rua, número, bairro, cidade ou CEP; complemento e referência não contam) ou configuração mudada. Se a taxa for a mesma, o envio segue.
    - O `409 DeliveryQuoteChanged` traz a **cotação nova inteira** em `quote`, já gravada. Ela sobrevive ao rollback do envio. O cliente reenvia com o `quoteId` novo e a mesma `Idempotency-Key`.
    - A conferência, que pode chamar o provedor, roda no passo de **preparação, fora da transação** do envio (ADR-0033 item 8).
    - O pedido gravado precisa ter `itemsTotal + deliveryFee` igual ao carrinho conferido, senão `409 CartChanged`.
11. **Cache por tenant** de `(hash do endereço normalizado)` → coordenadas e metros. O cache nunca é compartilhado entre tenants (cada restaurante é controlador dos dados dos seus clientes, ADR-0024). Uma cotação que acerta o cache não chama o provedor. *(Alterado em 2026-11-20; era "até 30 dias, coordenadas exatas":)*
    - **Validade:** **7 dias** para as coordenadas e para os metros (`Maps:GeocodeCacheDays` e `Maps:RouteCacheDays`; 0 desliga). *O limite ainda depende dos termos do provedor, Q-08 e Q-31.*
    - **Coordenadas com 4 casas** (`numeric(7,4)`, cerca de 11 m). A cotação corrente usa o ponto **exato** do geocodificador, com 6 casas, em `delivery_quotes`, que dura 15 min + 24 h. Uma cotação servida pelo cache usa o ponto de 4 casas.
    - Os metros do cache só valem enquanto o ponto de partida (o do restaurante, com 6 casas) for o mesmo.
    - Hash por HMAC (item 8). O cache não guarda o texto do endereço: numa cotação servida por ele, o `formattedAddress` é o endereço digitado.
    - Uma cotação que termina em `OutOfDeliveryArea` grava o cache mesmo assim, fora da transação, para não pagar o provedor de novo.
12. A cotação só é pedida quando o cliente confirma o endereço (nunca a cada tecla) e passa pelos limites do ADR-0045 (`public-quote`) e pelo **Turnstile** (`X-Turnstile-Token`, ADR-0045 item 12). Ela chama o Turnstile e o provedor dentro da transação da própria requisição, que não tem idempotência nem advisory lock e grava só a cotação (ADR-0029, complemento de 2026-11-20).

**Falha do provedor**

13. Erro, tempo esgotado (3 s) ou cota estourada → a página **não oferece entrega naquele momento**: "Não conseguimos calcular a entrega agora. Você pode retirar no local." A retirada continua funcionando. Não existe taxa de reserva na página: uma taxa fixa poderia cobrar R$ 3,00 por uma entrega de 12 km. *(2026-11-20: a cotação responde `422 DeliveryUnavailable` com esse texto em `detail`. O perfil público **continua com `deliveryAvailable: true`**, porque a falha é momentânea e a próxima cotação tenta de novo. `deliveryAvailable: false` fica para o que é configuração: entrega desligada, preço por km ausente, ponto não confirmado ou API sem provedor.)*
14. Um alerta operacional dispara com mais de 5 falhas em 10 minutos (`docs/09-devops.md`).

**Provedor**

15. Atrás de duas interfaces: `IGeocoder` (endereço → coordenadas + precisão) e `IRouteDistanceProvider` (origem, destino → metros). As interfaces não expõem tipos do provedor. *(2026-11-20: `Maps:Provider` escolhe entre quatro implementações:)*
    - `None`: sem entrega por distância (`MAPS_PROVIDER` em `pageDelivery.missing`). É o padrão do `appsettings.json` versionado, ou seja, de produção, até o usuário escolher o provedor;
    - `Fake`: sem rede, só em Development e nos testes. A API recusa `Fake` em Production;
    - `Osm`: Nominatim + OSRM. As instâncias públicas servem só para desenvolvimento, com o limite de 1 req/s respeitado por réplica e um User-Agent genérico, sem e-mail;
    - `Google`: Geocoding + Compute Route Matrix (`DRIVE`, `TRAFFIC_UNAWARE`). Sem chave, a API não sobe.
16. **Recomendação para o piloto:** Google Geocoding + Routes (Compute Route Matrix, Essentials, sem trânsito), pela qualidade dos endereços brasileiros e porque o volume cabe na faixa grátis. **Antes de assinar, verificar os termos de uso** (exibição sem mapa do Google, cache e atribuição). Se os termos não servirem, a alternativa é Mapbox ou OSRM/OpenRouteService auto-hospedado com OpenStreetMap (sem custo por chamada, com custo de máquina e geocodificação brasileira pior).
17. A chave do provedor fica no Key Vault (ADR-0037).

**Convivência com a taxa do ADR-0021**

18. **Pedidos do app continuam com a taxa padrão editável** do ADR-0021, sem mapa. Um botão "Calcular pela distância" no app fica como evolução (P2) e usaria o mesmo serviço.
19. No pedido web aceito, a taxa calculada é o valor do pedido. Pode ser editada depois do aceite com `orders.delivery-fee.edit`, como hoje (auditada; o cliente vê o novo total no acompanhamento). Pedido pago online fica bloqueado (ADR-0007).
20. `Order` guarda o snapshot: `DeliveryDistanceMeters`, `DeliveryPricePerKm`, `DeliveryMinimumFee` e `DeliveryFeeSource` (`FIXED_DEFAULT`, `DISTANCE`, `MANUAL`). A taxa continua separada no total e no fechamento (ADR-0021).

## Alternativas consideradas

- **Distância em linha reta (Haversine):** grátis e sem provedor de rota, mas erra muito com rio, avenida de mão única ou morro. O dono do produto pediu rota.
- **Rota com trânsito:** o dobro do preço, e a taxa mudaria com o horário. Para o cliente, a mesma casa precisa ter a mesma taxa.
- **Taxa por bairro (tabela):** comum em marmitaria e não depende de provedor, mas exige cadastrar dezenas de bairros. Pode virar um terceiro modo depois.
- **Calcular no navegador:** expõe a chave do provedor e permite forjar a taxa.
- **Taxa fixa quando o provedor falha:** cobra errado justamente nos casos extremos.
- **Arredondar a taxa para múltiplos de R$ 0,50:** prática comercial comum, mas o exemplo do usuário usa R$ 3,60. Fica como questão aberta.

## Consequências

- Novas entidades: `DeliveryQuote [T]` (apagada 24 h após vencer) e `GeocodeCacheEntry [T]` (7 dias, 4 casas, hash com segredo; era 30 dias). `StorefrontSettings` ganha os campos de entrega e a localização.
- O custo do provedor entra no custo por tenant. Acima da faixa grátis, ≈ US$ 0,01 por cotação nova (*preços a confirmar*).
- O texto da regra aparece no perfil público ("R$ 0,80/km, mínimo R$ 3,00, até 6 km").
- Revisitar a escolha do provedor quando o volume passar da faixa grátis.

## Implementação (2026-11-20)

- **Modo `FIXED`** (item 1): implementado junto com `DISTANCE`. Usa a `defaultDeliveryFee` do estabelecimento, sem mapa, raio nem conferência de precisão. A cotação devolve `distanceKm: null`. O perfil público mostra `deliveryRule.fixedFee`.
- **Ponto do restaurante** (item 3): ao salvar um endereço completo sem ponto, a API geocodifica uma vez, na mesma transação e com 3 s no máximo, e devolve o ponto como `isConfirmed: false`. O proprietário confirma ou arrasta no mapa. Mudar o endereço descarta o ponto. Uma falha do provedor deixa a configuração sem ponto, sem erro.
- **Exemplos do item 7:** viraram casos de teste (4,5 km → R$ 3,60; 2,1 km → mínimo R$ 3,00; 6,0 km → R$ 4,80; 6,1 km → fora da área; R$ 0,85/km × 4,5 km → R$ 3,83). "Sem rota de carro" também responde `422 OutOfDeliveryArea`.
- **Endereço não encontrado** responde `422` em `address.street`. **Resultado aproximado** responde `422` em `address.number`. Nenhum dos dois vira taxa (item 9).
- **Endereço gravado no pedido:** rua, número, complemento, bairro e referência. **A cidade não entra** no `deliveryAddress` do pedido; ela só serve à cotação. Na normalização do endereço, cidade nula é a do restaurante e a UF é sempre a do restaurante.
- **Alerta** (item 14): o contador `almoseis.maps.failures` (`Meter` `AlmoSeis.Maps`) e o log de aviso, só com provedor, operação e motivo, sem endereço. Os clientes HTTP dos provedores não fazem log, porque a URL leva o endereço e, no Google, a chave.
- **Termos do Google** (item 16): continuam não verificados. Ver Q-08 e Q-31.

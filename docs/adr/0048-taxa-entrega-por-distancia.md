# ADR-0048 — Taxa de entrega por distância de rota

- **Status:** Proposto
- **Data:** 2026-11-19
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

8. `POST /public/stores/{slug}/delivery-quotes` com o endereço → `quoteId`, `formattedAddress` (o endereço como o geocodificador entendeu), `distanceKm` (`"4.5"`), `deliveryFee` (`"3.60"`) e `expiresAt` (**15 min**). Fica em `DeliveryQuote [T]` com o hash do endereço normalizado, coordenadas, metros, taxa e a versão das configurações.
9. A página mostra "Encontramos: Rua das Flores, 123 – Centro" e o cliente confirma. Se o geocodificador devolver um resultado aproximado (só rua, só bairro ou só cidade), a página pede para revisar o número ou o bairro. Resultado aproximado nunca vira taxa.
10. O pedido referencia o `quoteId`. Na criação, o servidor confere: cotação válida, do mesmo tenant e com o mesmo hash de endereço. Se venceu, recalcula. Se a taxa mudou (o restaurante mudou o preço por km, por exemplo) → `409 DeliveryQuoteChanged` com a nova taxa, e o cliente confirma de novo. **O cliente nunca paga uma taxa que não viu.**
11. **Cache por tenant** de `(hash do endereço normalizado)` → coordenadas e metros, por até **30 dias** (*limite a confirmar nos termos do provedor*). O cache nunca é compartilhado entre tenants (cada restaurante é controlador dos dados dos seus clientes, ADR-0024). Uma cotação que acerta o cache não chama o provedor.
12. A cotação só é pedida quando o cliente confirma o endereço (nunca a cada tecla) e passa pelos limites do ADR-0045 (`public-quote`).

**Falha do provedor**

13. Erro, tempo esgotado (3 s) ou cota estourada → a página **não oferece entrega naquele momento**: "Não conseguimos calcular a entrega agora. Você pode retirar no local." A retirada continua funcionando. Não existe taxa de reserva na página: uma taxa fixa poderia cobrar R$ 3,00 por uma entrega de 12 km.
14. Um alerta operacional dispara com mais de 5 falhas em 10 minutos (`docs/09-devops.md`).

**Provedor**

15. Atrás de duas interfaces: `IGeocoder` (endereço → coordenadas + precisão) e `IRouteDistanceProvider` (origem, destino → metros). As interfaces não expõem tipos do provedor.
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

- Novas entidades: `DeliveryQuote [T]` (apagada 24 h após vencer) e `GeocodeCacheEntry [T]` (30 dias). `StorefrontSettings` ganha os campos de entrega e a localização.
- O custo do provedor entra no custo por tenant. Acima da faixa grátis, ≈ US$ 0,01 por cotação nova (*preços a confirmar*).
- O texto da regra aparece no perfil público ("R$ 0,80/km, mínimo R$ 3,00, até 6 km").
- Revisitar a escolha do provedor quando o volume passar da faixa grátis.

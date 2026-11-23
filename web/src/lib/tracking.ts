import type { TrackingItem } from './api/types';
import { parseMoney, type Cents } from './money';

/**
 * Total de um item do acompanhamento somando a árvore (§6.19): o `lineTotal` de cada linha é só dela (combo 34,90;
 * bacon 4,00; acréscimo do sabor 0,50), e o total do combo para mostrar é a soma (39,40).
 */
export function trackingItemTotalCents(item: TrackingItem): Cents {
  return (
    parseMoney(item.lineTotal) +
    item.addons.reduce((sum, a) => sum + parseMoney(a.lineTotal), 0) +
    (item.parts ?? []).reduce((sum, p) => sum + trackingItemTotalCents(p), 0)
  );
}

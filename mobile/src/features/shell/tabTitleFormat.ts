/** Texto do título da aba do navegador com o contador (ADR-0053 §7.4). Sem dependência de plataforma. */
export const BASE_TITLE = 'AlmoSeis';

/** "(2) Pedidos novos · AlmoSeis"; sem pendentes, só o nome. */
export function formatTabTitle(count: number, base = BASE_TITLE): string {
  if (count <= 0) return base;
  return `(${count}) ${count === 1 ? 'Pedido novo' : 'Pedidos novos'} · ${base}`;
}

/** Texto do título da aba do navegador com o contador (ADR-0053 §7.4). Sem dependência de plataforma. */
export const BASE_TITLE = 'AlmoSeis';

/** "(2) Novos do site · AlmoSeis" (pedidos do site aguardando aceite, ADR-0046); sem nenhum, só o nome. */
export function formatTabTitle(count: number, base = BASE_TITLE): string {
  if (count <= 0) return base;
  return `(${count}) ${count === 1 ? 'Novo do site' : 'Novos do site'} · ${base}`;
}

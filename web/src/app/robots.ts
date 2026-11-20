import type { MetadataRoute } from 'next';

/** Perfis podem ser indexados (base do marketplace, ADR-0051); carrinho e acompanhamento não. */
export default function robots(): MetadataRoute.Robots {
  return {
    rules: { userAgent: '*', allow: '/', disallow: ['/*/carrinho', '/*/pedido/'] },
  };
}
